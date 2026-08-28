using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Coordinates workflow task execution with support for both parallel and sequential execution strategies.
/// Implements condition and timer evaluation services.
/// Delegates single task execution to ITaskExecutionEngine.
/// </summary>
/// <remarks>
/// Refactored to follow SRP - only handles orchestration.
/// Task execution logic is delegated to TaskExecutionEngine.
/// Error boundary handling is delegated to consolidated services in Execution/ErrorHandling.
/// For parallel task groups, each task runs in its own DI scope to isolate DbContext instances
/// and avoid EF Core thread-safety violations.
/// </remarks>
public sealed class TaskCoordinator : ITaskCoordinatorExtended
{
    private readonly ITaskExecutionEngine _executionEngine;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IConditionEvaluator _conditionEvaluator;
    private readonly ITimerEvaluator _timerEvaluator;
    private readonly IExecutionErrorFactory _errorFactory;
    private readonly ILogger<TaskCoordinator> _logger;

    /// <summary>
    /// Lock object for thread-safe parallel task failure tracking.
    /// Per Microsoft guidelines: use a dedicated private readonly object for locking.
    /// </summary>
    /// <remarks>
    /// See: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock#guidelines
    /// </remarks>
    private readonly object _parallelTaskLock = new();

    /// <summary>
    /// Initializes a new instance of TaskCoordinator.
    /// </summary>
    public TaskCoordinator(
        ITaskExecutionEngine executionEngine,
        IServiceScopeFactory serviceScopeFactory,
        IConditionEvaluator conditionEvaluator,
        ITimerEvaluator timerEvaluator,
        IExecutionErrorFactory errorFactory,
        ILogger<TaskCoordinator> logger)
    {
        _executionEngine = executionEngine;
        _serviceScopeFactory = serviceScopeFactory;
        _conditionEvaluator = conditionEvaluator;
        _timerEvaluator = timerEvaluator;
        _errorFactory = errorFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> ExecuteAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteWithDetailsAsync(
            onExecuteTasks,
            instanceTransitionId,
            taskTrigger,
            origin,
            context,
            cancellationToken);

        if (!result.IsSuccess)
            return Result.Fail(result.Error);

        if (!result.Value!.IsSuccess)
        {
            var error = result.Value.TaskError?.ToError() ??
                        Error.Failure("TaskExecutionFailed", "One or more tasks failed");
            return Result.Fail(error);
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExecuteConditionAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return _conditionEvaluator.EvaluateAsync(script, context, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TimerSchedule>> ExecuteTimerAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return _timerEvaluator.EvaluateAsync(script, context, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TasksExecutionResult>> ExecuteWithDetailsAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithDetailsAsync(
            onExecuteTasks,
            instanceTransitionId,
            taskTrigger,
            origin,
            context,
            completedTaskIds: [],
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<TasksExecutionResult>> ExecuteWithDetailsAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        ScriptContext context,
        IEnumerable<string> completedTaskIds,
        bool skipJournalProbe = false,
        Func<OnExecuteTask, TaskEngineExecutionOptions, TaskEngineExecutionOptions>? optionsRefiner = null,
        CancellationToken cancellationToken = default)
    {
        // One shared options instance per call: fresh-record executions skip the guaranteed-empty
        // journal probe, everything else keeps the engine's default (probing) behavior.
        var engineOptions = skipJournalProbe
            ? TaskEngineExecutionOptions.FreshTransitionRecord
            : TaskEngineExecutionOptions.Default;

        // No span of its own: the coordinator is a pure fan-out wrapper. Its former
        // "TaskCoordinator.Execute" span added a level between transition/{key} and
        // Task.Execute.{key} without carrying information neither of those already has.
        // Tags are deliberately NOT re-stamped onto Activity.Current either — that would write
        // per-coordinator values (vnext.task.count) onto the transition span and have OnExecute,
        // OnExit and OnEntry overwrite each other.
        var tasks = onExecuteTasks.ToList();
        var completedSet = completedTaskIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var executedTasks = new List<TaskExecutionSummary>();
        var totalStartTimestamp = Stopwatch.GetTimestamp();

        if (!tasks.Any())
        {
            return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(executedTasks));
        }

        // Filter out already completed tasks
        var tasksToExecute = tasks
            .Where(t => !completedSet.Contains(t.Task.Key))
            .ToList();

        var skippedCount = tasks.Count - tasksToExecute.Count;

        _logger.LogDebug(
            "Coordinating execution of {TaskCount} tasks for instance {InstanceId}. " +
            "Bypassing {BypassCount} already completed tasks.",
            tasksToExecute.Count, context.Instance?.Id, skippedCount);

        // Group tasks by Order for parallel/sequential execution
        var taskGroups = tasksToExecute
            .GroupBy(t => t.Order)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in taskGroups)
        {
            var groupTasks = group.ToList();

            // A hook (onExecute/onEntry/onExit) may legitimately list the same task key twice at
            // the same order. Resolved once per group, from the definition's own shape, so the
            // single-task and parallel paths below apply the identical decision — the suffix must
            // not depend on which path happens to run the group.
            LogDuplicateTaskKeysIfAny(group.Key, groupTasks, taskTrigger, context.Instance?.Id, context.Transition?.Key);
            var groupOptions = ResolveGroupEngineOptions(groupTasks, engineOptions);

            // Caller-supplied per-task override (e.g. the extension path setting a distinct
            // ResponseVariableKey per extension). Applied AFTER the duplicate-key JournalTaskKey
            // disambiguation above so the two disambiguators compose instead of competing — the
            // refiner only ever adds to what ResolveGroupEngineOptions already resolved, never
            // races it.
            if (optionsRefiner is not null)
            {
                var refined = new TaskEngineExecutionOptions[groupTasks.Count];
                for (var i = 0; i < groupTasks.Count; i++)
                {
                    refined[i] = optionsRefiner(groupTasks[i], groupOptions[i]);
                }
                groupOptions = refined;
            }

            if (groupTasks.Count == 1)
            {
                // Single task - execute directly
                var result = await _executionEngine.ExecuteAsync(
                    groupTasks[0], instanceTransitionId, taskTrigger, origin, context, groupOptions[0], cancellationToken);

                var processResult = ProcessTaskResult(result, groupTasks[0], executedTasks, totalStartTimestamp);
                if (processResult.HasValue)
                    return processResult.Value;
            }
            else
            {
                // Multiple tasks with same Order - execute in parallel with cancellation
                var parallelResult = await ExecuteTaskGroupInParallelAsync(
                    groupTasks, instanceTransitionId, taskTrigger, origin, context, groupOptions, cancellationToken);

                if (!parallelResult.IsSuccess)
                {
                    return parallelResult;
                }

                var groupResult = parallelResult.Value!;
                executedTasks.AddRange(groupResult.ExecutedTasks);

                // If any task in parallel group failed with blocking action, stop
                if (!groupResult.IsSuccess)
                {
                    return parallelResult;
                }
            }

        }


        if (skippedCount > 0)
        {
            _logger.LogInformation(
                "Task coordination completed. Executed: {ExecutedCount}, Skipped: {SkippedCount}",
                executedTasks.Count, skippedCount);
        }

        var hasBusinessFailures = executedTasks.Any(t => !t.IsSuccess);
        return Result<TasksExecutionResult>.Ok(
            hasBusinessFailures
                ? TasksExecutionResult.SuccessWithFailedTasks(executedTasks, (long)Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds)
                : TasksExecutionResult.Success(executedTasks, (long)Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds));
    }

    /// <summary>
    /// Processes single task result and returns failure result if needed.
    /// </summary>
    private Result<TasksExecutionResult>? ProcessTaskResult(
        Result<TasksExecutionResult> taskResult,
        OnExecuteTask onExecuteTask,
        List<TaskExecutionSummary> executedTasks,
        long totalStartTimestamp)
    {
        // Infrastructure error
        if (!taskResult.IsSuccess)
        {
            var infraError = _errorFactory.CreateFromError(
                taskResult.Error,
                onExecuteTask.Task.Key,
                "Unknown",
                (long)Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds);

            return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Failure(
                onExecuteTask,
                infraError,
                executedTasks,
                (long)Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds));
        }

        var result = taskResult.Value!;

        // Business error with blocking action
        if (!result.IsSuccess)
        {
            return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
            {
                IsSuccess = false,
                HasFailedTasks = true,
                FailedTaskKeys = result.FailedTaskKeys,
                FailedTask = result.FailedTask ?? onExecuteTask,
                TaskError = result.TaskError,
                BoundaryAction = result.BoundaryAction,
                ExecutedTasks = executedTasks,
                TotalExecutionDurationMs = (long)Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds
            });
        }

        // Success - add to executed list
        executedTasks.AddRange(result.ExecutedTasks);
        return null;
    }

    /// <summary>
    /// Executes a group of tasks with same Order in parallel.
    /// Each task runs in its own DI scope to isolate DbContext instances and avoid
    /// EF Core thread-safety violations during concurrent SaveChanges/change-tracker operations.
    /// If one task fails, cancels all other tasks and triggers error boundary.
    /// </summary>
    private async Task<Result<TasksExecutionResult>> ExecuteTaskGroupInParallelAsync(
        List<OnExecuteTask> tasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        ScriptContext context,
        IReadOnlyList<TaskEngineExecutionOptions> engineOptionsPerTask,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;
        var executedTasks = new List<TaskExecutionSummary>();
        var startTimestamp = Stopwatch.GetTimestamp();

        _logger.LogDebug(
            "Executing {TaskCount} tasks in parallel for instance {InstanceId}",
            tasks.Count, context.Instance?.Id);

        // Track first failure for error boundary (thread-safe)
        TasksExecutionResult? firstFailure = null;
        OnExecuteTask? firstFailedTask = null;

        var executionTasks = tasks.Select(async (task, index) =>
        {
            var branchContext = context.CreateParallelBranch();
            try
            {
                // Each parallel task gets its own DI scope with an isolated DbContext.
                // This prevents EF Core thread-safety violations when multiple tasks
                // perform concurrent InsertAsync/UpdateAsync on InstanceTask entities.
                await using var scope = _serviceScopeFactory.CreateAsyncScope();
                var scopedEngine = scope.ServiceProvider.GetRequiredService<ITaskExecutionEngine>();

                var result = await scopedEngine.ExecuteAsync(
                    task, instanceTransitionId, taskTrigger, origin, branchContext, engineOptionsPerTask[index], linkedToken);

                if (!result.IsSuccess)
                {
                    var infrastructureError = _errorFactory.CreateFromError(
                        result.Error,
                        task.Task.Key,
                        "Unknown",
                        (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
                    result = Result<TasksExecutionResult>.Ok(
                        TasksExecutionResult.Failure(
                            task,
                            infrastructureError,
                            totalDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds));
                }

                if (!result.Value!.IsSuccess)
                {
                    lock (_parallelTaskLock)
                    {
                        if (firstFailure == null)
                        {
                            firstFailure = result.Value;
                            firstFailedTask = task;
                            linkedCts.CancelAsync();
                        }
                    }
                }

                return (Task: task, Result: result, Context: branchContext);
            }
            catch (OperationCanceledException)
            {
                return (Task: task, Result: Result<TasksExecutionResult>.Fail(
                    Error.Failure("TaskCancelled", $"Task {task.Task.Key} was cancelled")), Context: branchContext);
            }
        }).ToList();

        try
        {
            var results = await Task.WhenAll(executionTasks);

            foreach (var outcome in results)
                context.MergeParallelBranch(outcome.Context);


            // If there was a failure, return it with error boundary info
            if (firstFailure != null && firstFailedTask != null)
            {
                // Collect successful tasks before failure
                foreach (var (_, result, _) in results)
                {
                    if (result.IsSuccess && result.Value != null && result.Value.IsSuccess)
                    {
                        executedTasks.AddRange(result.Value.ExecutedTasks);
                    }
                }

                return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
                {
                    IsSuccess = false,
                    HasFailedTasks = true,
                    FailedTaskKeys = firstFailure.FailedTaskKeys,
                    FailedTask = firstFailure.FailedTask ?? firstFailedTask,
                    TaskError = firstFailure.TaskError,
                    BoundaryAction = firstFailure.BoundaryAction,
                    ExecutedTasks = executedTasks,
                    TotalExecutionDurationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
                });
            }

            // All succeeded
            foreach (var (_, result, _) in results)
            {
                if (result.IsSuccess && result.Value != null)
                {
                    executedTasks.AddRange(result.Value.ExecutedTasks);
                }
            }

            var hasBusinessFailures = executedTasks.Any(t => !t.IsSuccess);
            return Result<TasksExecutionResult>.Ok(
                hasBusinessFailures
                    ? TasksExecutionResult.SuccessWithFailedTasks(executedTasks, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds)
                    : TasksExecutionResult.Success(executedTasks, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parallel task execution failed unexpectedly");

            if (firstFailure != null && firstFailedTask != null)
            {
                return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
                {
                    IsSuccess = false,
                    HasFailedTasks = true,
                    FailedTaskKeys = firstFailure.FailedTaskKeys,
                    FailedTask = firstFailedTask,
                    TaskError = firstFailure.TaskError,
                    BoundaryAction = firstFailure.BoundaryAction,
                    ExecutedTasks = executedTasks,
                    TotalExecutionDurationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
                });
            }

            return Result<TasksExecutionResult>.Fail(Error.Failure("ParallelExecutionFailed", ex.Message));
        }
    }

    /// <summary>
    /// Resolves the per-task <see cref="TaskEngineExecutionOptions"/> for one Order group. A task
    /// key that appears only once in the group keeps <paramref name="baseOptions"/> unchanged — no
    /// journal-key churn for the overwhelmingly common case. A task key that REPEATS within the
    /// group gets a positional suffix on EVERY occurrence ("key#0", "key#1", … by position among
    /// that key's occurrences) so <c>InstanceTask.ExecutionKey</c> (which folds in
    /// <c>options.JournalTaskKey ?? task.Key</c>, see <c>TaskExecutionEngine</c>) is distinct per
    /// occurrence instead of colliding on <c>UX_InstanceTasks_ExecutionKey</c> when two entries
    /// share both key and order (a legitimate hook shape — see
    /// <see cref="WorkflowLogs.DuplicateTaskKeyAtSameOrder"/> for the accompanying warning).
    /// Suffixing only the second-onward occurrence would leave a confusing asymmetric pair in the
    /// journal ("script-task" next to "script-task#1"); suffixing all of them reads correctly.
    /// A <see cref="TaskEngineExecutionOptions.JournalTaskKey"/> the caller already set (FanOut sets
    /// its own, e.g. "fan-out-docs#3") is never overwritten.
    /// </summary>
    /// <remarks>
    /// Called for every group regardless of size (including groups of one) so the decision comes
    /// from the definition's own shape rather than from which execution path — single-task or
    /// parallel — happens to run the group.
    /// </remarks>
    internal static IReadOnlyList<TaskEngineExecutionOptions> ResolveGroupEngineOptions(
        IReadOnlyList<OnExecuteTask> groupTasks,
        TaskEngineExecutionOptions baseOptions)
    {
        var result = new TaskEngineExecutionOptions[groupTasks.Count];

        if (groupTasks.Count == 1)
        {
            result[0] = baseOptions;
            return result;
        }

        var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var task in groupTasks)
        {
            keyCounts[task.Task.Key] = keyCounts.GetValueOrDefault(task.Task.Key) + 1;
        }

        var seenPerKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < groupTasks.Count; i++)
        {
            var task = groupTasks[i];
            var options = baseOptions;

            if (keyCounts[task.Task.Key] > 1 && string.IsNullOrEmpty(options.JournalTaskKey))
            {
                var position = seenPerKey.GetValueOrDefault(task.Task.Key);
                seenPerKey[task.Task.Key] = position + 1;
                options = options with { JournalTaskKey = $"{task.Task.Key}#{position}" };
            }

            result[i] = options;
        }

        return result;
    }

    /// <summary>
    /// Emits <see cref="WorkflowLogs.DuplicateTaskKeyAtSameOrder"/> once per task key that repeats
    /// within this Order group. A hook listing the same task key twice at the same order now
    /// executes correctly (see <see cref="ResolveGroupEngineOptions"/>) but is still almost
    /// certainly an authoring mistake, so it is surfaced as a warning rather than silently accepted
    /// or rejected outright — <c>WorkflowValidationResult</c> has no warning severity to carry this
    /// at definition-validation time (only hard errors), so it is logged here at execution time.
    /// </summary>
    /// <remarks>
    /// Never fires for <see cref="TaskTrigger.Extension"/>. Two extensions sharing one task
    /// Reference at the same order is a documented, intentional pattern (see
    /// <c>InstanceExtensionService.ExecuteExtensionsInternalAsync</c>): each extension owns its own
    /// <c>OnExecuteTask</c> — with its own <c>Mapping</c>/<c>ErrorBoundary</c> — and files its
    /// output under its own <c>ResponseVariableKey</c>, so the two writes never collide. The
    /// remedy this warning carries ("give the entries distinct orders") would also be actively
    /// wrong advice for this hook: it targets the journal-key collision that
    /// <see cref="ResolveGroupEngineOptions"/>'s "#0"/"#1" suffixing exists to prevent, but
    /// <c>ExtensionTaskPersistenceStrategy</c> never persists an <c>InstanceTask</c> row for
    /// Extension-origin executions in the first place — there is no journal entry to collide, so
    /// there is nothing for that suffixing to disambiguate here. For every OTHER hook (transition
    /// OnEntry/OnExit/OnExecute/Manual) the duplicate is still almost certainly a copy-paste
    /// mistake and must keep warning with the current remedy.
    /// </remarks>
    private void LogDuplicateTaskKeysIfAny(
        int order,
        IReadOnlyList<OnExecuteTask> groupTasks,
        TaskTrigger taskTrigger,
        Guid? instanceId,
        string? transitionKey)
    {
        if (groupTasks.Count < 2 || taskTrigger == TaskTrigger.Extension)
            return;

        var duplicates = groupTasks
            .GroupBy(t => t.Task.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            _logger.DuplicateTaskKeyAtSameOrder(
                transitionKey ?? "N/A",
                taskTrigger.ToString(),
                duplicate.Key,
                duplicate.Count(),
                order,
                instanceId);
        }
    }
}
