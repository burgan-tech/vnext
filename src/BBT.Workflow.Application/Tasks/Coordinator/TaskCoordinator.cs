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
    // The coordinator takes an unhandled failure as an outcome, so the engine's ExecutionError — its
    // stack trace included — reaches the incident instead of being rebuilt from an Error (vnext#1268).
    private static readonly TaskEngineExecutionOptions Coordinated =
        TaskEngineExecutionOptions.Default with { ReportUnhandledFailureAsOutcome = true };

    private static readonly TaskEngineExecutionOptions CoordinatedFreshRecord =
        TaskEngineExecutionOptions.FreshTransitionRecord with { ReportUnhandledFailureAsOutcome = true };

    private readonly ITaskExecutionEngine _executionEngine;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IConditionEvaluator _conditionEvaluator;
    private readonly ITimerEvaluator _timerEvaluator;
    private readonly IExecutionErrorFactory _errorFactory;
    private readonly ILogger<TaskCoordinator> _logger;

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
        var engineOptions = skipJournalProbe ? CoordinatedFreshRecord : Coordinated;

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
            LogDuplicateTaskKeysIfAny(group.Key, groupTasks, taskTrigger, origin, context.Instance?.Id, context.Transition?.Key);
            var groupOptions = ResolveGroupEngineOptions(groupTasks, engineOptions);

            // Caller-supplied per-task override (e.g. the extension path setting a distinct
            // ResponseVariableKey per extension). Applied AFTER the duplicate-key JournalTaskKey
            // disambiguation above so the two disambiguators compose instead of competing — the
            // refiner only ever adds to what ResolveGroupEngineOptions already resolved, never
            // races it.
            groupOptions = RefineGroupOptions(groupTasks, groupOptions, optionsRefiner);

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

                // Infrastructure failure, or a task in the group failed with a blocking outcome: stop.
                if (!parallelResult.IsSuccess || !parallelResult.Value!.IsSuccess)
                {
                    return parallelResult;
                }

                executedTasks.AddRange(parallelResult.Value.ExecutedTasks);
            }

        }


        if (skippedCount > 0)
        {
            _logger.LogInformation(
                "Task coordination completed. Executed: {ExecutedCount}, Skipped: {SkippedCount}",
                executedTasks.Count, skippedCount);
        }

        return CompleteHook(executedTasks, totalStartTimestamp);
    }

    /// <summary>
    /// The result of a hook (or one parallel group) that ran to its end: success, or success with
    /// the failures an <c>Ignore</c> / <c>Log</c> boundary handled.
    /// </summary>
    private static Result<TasksExecutionResult> CompleteHook(
        List<TaskExecutionSummary> executedTasks,
        long startTimestamp)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return Result<TasksExecutionResult>.Ok(executedTasks.Exists(t => !t.IsSuccess)
            ? TasksExecutionResult.CompletedWithHandledFailures(executedTasks, durationMs)
            : TasksExecutionResult.Success(executedTasks, durationMs));
    }

    /// <summary>
    /// Applies the caller-supplied per-task override (e.g. the extension path setting a distinct
    /// ResponseVariableKey per extension) on top of the group's resolved options. Applied AFTER the
    /// duplicate-key JournalTaskKey disambiguation so the two compose instead of competing.
    /// </summary>
    private static IReadOnlyList<TaskEngineExecutionOptions> RefineGroupOptions(
        List<OnExecuteTask> groupTasks,
        IReadOnlyList<TaskEngineExecutionOptions> groupOptions,
        Func<OnExecuteTask, TaskEngineExecutionOptions, TaskEngineExecutionOptions>? optionsRefiner)
    {
        if (optionsRefiner is null)
            return groupOptions;

        var refined = new TaskEngineExecutionOptions[groupTasks.Count];
        for (var i = 0; i < groupTasks.Count; i++)
        {
            refined[i] = optionsRefiner(groupTasks[i], groupOptions[i]);
        }
        return refined;
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
        // Infrastructure error. With ReportUnhandledFailureAsOutcome the engine answers every
        // unhandled failure as an outcome itself (re-derived exactly like this, plus the stack trace);
        // what still arrives here is a failure before any task ran, e.g. a flow task without a
        // transition record id.
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

        // A failure an Ignore/Log boundary let through: the hook goes on (vnext#1257).
        if (IsHandledFailure(result))
        {
            executedTasks.AddRange(result.ExecutedTasks);
            return null;
        }

        // Unhandled failure (no boundary) or a blocking boundary action: the hook stops here.
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
    /// A task failure the error boundary handled with a non-blocking action (<c>Ignore</c> /
    /// <c>Log</c>): failed, but with neither a <c>TaskError</c> (unhandled) nor a
    /// <c>BoundaryAction</c> (blocking) — see <see cref="TasksExecutionResult.HandledFailure"/>.
    /// It does not stop the hook.
    /// </summary>
    private static bool IsHandledFailure(TasksExecutionResult result) =>
        result is { IsSuccess: false, TaskError: null, BoundaryAction: null };

    /// <summary>
    /// Executes a group of tasks with same Order in parallel.
    /// Each task runs in its own DI scope, which isolates SERVICES but not the DbContext: a context is
    /// bound to the ambient (AsyncLocal) unit of work, keyed by schema, and that unit of work flows into
    /// every branch. Every database touch a branch makes therefore has to bring its own unit of work —
    /// the task journal (<c>RequiresNew</c> in the persistence strategy), the instance command gateway,
    /// and the local read gateways (<c>ExecuteReadWithWorkflowAsync</c>). A new read path reachable from
    /// a task must do the same, or two same-order tasks touching one schema fail with "A second operation
    /// was started on this context instance".
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

        // Track first blocking failure for error boundary (thread-safe)
        var firstFailure = new FirstBlockingFailure();

        var executionTasks = tasks.Select(async (task, index) =>
        {
            var branchContext = context.CreateParallelBranch();
            try
            {
                // Own DI scope per branch — services only; DbContext isolation comes from the
                // per-operation unit of work each database path opens (see the method summary).
                await using var scope = _serviceScopeFactory.CreateAsyncScope();
                var scopedEngine = scope.ServiceProvider.GetRequiredService<ITaskExecutionEngine>();

                var result = ToTaskOutcome(
                    await scopedEngine.ExecuteAsync(
                        task, instanceTransitionId, taskTrigger, origin, branchContext, engineOptionsPerTask[index], linkedToken),
                    task,
                    startTimestamp);

                if (IsBlockingFailure(result.Value!) && firstFailure.TryClaim(result.Value!, task))
                {
                    await linkedCts.CancelAsync();
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

            // One round: the merge must see every branch at once to tell a slot an earlier order left
            // behind (overwritten, like a sequential run) from two branches of this group colliding.
            context.MergeParallelBranches(results.Select(outcome => outcome.Context).ToList());


            // Tasks that completed or whose failure a boundary handled. A blocking failure (or a
            // branch the first failure cancelled) is not counted as executed.
            executedTasks.AddRange(results
                .Where(outcome => outcome.Result is { IsSuccess: true, Value: { } value } && !IsBlockingFailure(value))
                .SelectMany(outcome => outcome.Result.Value!.ExecutedTasks));

            // If there was a blocking failure, return it with error boundary info
            return firstFailure.Result is { } failure
                ? Result<TasksExecutionResult>.Ok(BlockingGroupFailure(
                    failure, failure.FailedTask ?? firstFailure.Task!, executedTasks, startTimestamp))
                : CompleteHook(executedTasks, startTimestamp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parallel task execution failed unexpectedly");

            return firstFailure.Result is { } failure
                ? Result<TasksExecutionResult>.Ok(BlockingGroupFailure(
                    failure, firstFailure.Task!, executedTasks, startTimestamp))
                : Result<TasksExecutionResult>.Fail(Error.Failure("ParallelExecutionFailed", ex.Message));
        }
    }

    /// <summary>
    /// Turns an engine infrastructure failure (a failed <see cref="Result{T}"/>) into the same
    /// unhandled-failure shape a business failure has, so every branch reports one shape.
    /// </summary>
    private Result<TasksExecutionResult> ToTaskOutcome(
        Result<TasksExecutionResult> result,
        OnExecuteTask task,
        long startTimestamp)
    {
        if (result.IsSuccess)
            return result;

        // Same remaining producers as ProcessTaskResult's infrastructure branch.
        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var infrastructureError = _errorFactory.CreateFromError(result.Error, task.Task.Key, "Unknown", durationMs);
        return Result<TasksExecutionResult>.Ok(
            TasksExecutionResult.Failure(task, infrastructureError, totalDurationMs: durationMs));
    }

    /// <summary>A failure that stops the hook: unhandled, or a blocking boundary action.</summary>
    private static bool IsBlockingFailure(TasksExecutionResult result) =>
        !result.IsSuccess && !IsHandledFailure(result);

    /// <summary>The group's result when one of its tasks failed with a blocking outcome.</summary>
    private static TasksExecutionResult BlockingGroupFailure(
        TasksExecutionResult failure,
        OnExecuteTask failedTask,
        List<TaskExecutionSummary> executedTasks,
        long startTimestamp) => new()
    {
        IsSuccess = false,
        HasFailedTasks = true,
        FailedTaskKeys = failure.FailedTaskKeys,
        FailedTask = failedTask,
        TaskError = failure.TaskError,
        BoundaryAction = failure.BoundaryAction,
        ExecutedTasks = executedTasks,
        TotalExecutionDurationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
    };

    /// <summary>
    /// The first blocking failure of a parallel group. Claimed once, under a lock, by the branch that
    /// gets there first; that branch cancels its siblings.
    /// </summary>
    private sealed class FirstBlockingFailure
    {
        private readonly Lock _gate = new();

        public TasksExecutionResult? Result { get; private set; }

        public OnExecuteTask? Task { get; private set; }

        public bool TryClaim(TasksExecutionResult result, OnExecuteTask task)
        {
            lock (_gate)
            {
                if (Result is not null)
                    return false;

                Result = result;
                Task = task;
                return true;
            }
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
    /// Also threads an authored <c>variableKey</c> into <see cref="TaskEngineExecutionOptions.ResponseVariableKey"/>,
    /// so two runs of the same task at one order file under distinct slots and the parallel merge accepts both.
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

        Dictionary<string, int>? keyCounts = null;
        if (groupTasks.Count > 1)
        {
            keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var task in groupTasks)
            {
                keyCounts[task.Task.Key] = keyCounts.GetValueOrDefault(task.Task.Key) + 1;
            }
        }

        var seenPerKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < groupTasks.Count; i++)
        {
            var task = groupTasks[i];
            var options = baseOptions;

            if (keyCounts is not null && keyCounts[task.Task.Key] > 1 && string.IsNullOrEmpty(options.JournalTaskKey))
            {
                var position = seenPerKey.GetValueOrDefault(task.Task.Key);
                seenPerKey[task.Task.Key] = position + 1;
                options = options with { JournalTaskKey = $"{task.Task.Key}#{position}" };
            }

            // The definition's own response slot. A caller-supplied key (the extension path) is
            // never overwritten, and the extension refiner still runs after this and wins.
            if (!string.IsNullOrWhiteSpace(task.VariableKey) && options.ResponseVariableKey is null)
            {
                options = options with { ResponseVariableKey = task.VariableKey };
            }

            result[i] = options;
        }

        return result;
    }

    /// <summary>
    /// Emits <see cref="WorkflowLogs.DuplicateTaskKeyAtSameOrder"/> once per shared response slot
    /// within this Order group. Two entries sharing a response slot at one order cannot both be
    /// merged, so the run fails; publish rejects this shape (WorkflowValidator) and only definitions
    /// published before that check can still reach here.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="TaskExecutionOrigin.Extension"/> — NOT <see cref="TaskTrigger.Extension"/>.
    /// Two extensions sharing one task Reference at the same order is a documented, intentional
    /// pattern (see <c>InstanceExtensionService.ExecuteExtensionsInternalAsync</c>): each extension
    /// owns its own <c>OnExecuteTask</c> — with its own <c>Mapping</c>/<c>ErrorBoundary</c> — and
    /// files its output under its own <c>ResponseVariableKey</c>, so the two writes never collide.
    /// The remedy this warning carries ("give the entries distinct orders") would also be actively
    /// wrong advice for this hook: it targets the journal-key collision that
    /// <see cref="ResolveGroupEngineOptions"/>'s "#0"/"#1" suffixing exists to prevent, but
    /// <c>ExtensionTaskPersistenceStrategy</c> never persists an <c>InstanceTask</c> row for
    /// Extension-origin executions in the first place — there is no journal entry to collide, so
    /// there is nothing for that suffixing to disambiguate here.
    ///
    /// Custom functions (<c>FunctionAppService.cs</c>) execute through the SAME
    /// <see cref="TaskTrigger.Extension"/> trigger but with <see cref="TaskExecutionOrigin.Function"/>
    /// — a multi-task function (<c>FunctionAppService.GetSingleTaskVariableKey</c> exists precisely
    /// to distinguish single-task from multi-task functions) has no per-entry response-key override,
    /// so a duplicated task key at the same order there is still a plain authoring mistake. Gating
    /// on <c>taskTrigger</c> instead of <c>origin</c> would silently swallow that case too — do not
    /// revert to the trigger check.
    ///
    /// For every OTHER hook (transition OnEntry/OnExit/OnExecute/Manual) the duplicate is still
    /// almost certainly a copy-paste mistake and must keep warning with the current remedy.
    /// </remarks>
    private void LogDuplicateTaskKeysIfAny(
        int order,
        IReadOnlyList<OnExecuteTask> groupTasks,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        Guid? instanceId,
        string? transitionKey)
    {
        if (groupTasks.Count < 2 || origin == TaskExecutionOrigin.Extension)
            return;

        var duplicates = groupTasks
            .Where(t => t.ResponseVariableKey is not null)
            .GroupBy(t => t.ResponseVariableKey!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            // Task key(s) (not the camelCased slot name) so the log stays searchable by definition
            // key; a slot shared by entries with different task keys names every distinct key.
            var taskKeys = string.Join(", ", duplicate.Select(t => t.Task.Key).Distinct(StringComparer.Ordinal));
            _logger.DuplicateTaskKeyAtSameOrder(
                transitionKey ?? "N/A",
                taskTrigger.ToString(),
                taskKeys,
                duplicate.Count(),
                order,
                instanceId);
        }
    }
}
