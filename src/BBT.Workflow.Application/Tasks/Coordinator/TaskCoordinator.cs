using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
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
/// </remarks>
public sealed class TaskCoordinator : ITaskCoordinatorExtended
{
    private readonly ITaskExecutionEngine _executionEngine;
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
        IConditionEvaluator conditionEvaluator,
        ITimerEvaluator timerEvaluator,
        IExecutionErrorFactory errorFactory,
        ILogger<TaskCoordinator> logger)
    {
        _executionEngine = executionEngine;
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
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteWithDetailsAsync(
            onExecuteTasks,
            instanceTransitionId,
            taskTrigger,
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
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithDetailsAsync(
            onExecuteTasks,
            instanceTransitionId,
            taskTrigger,
            context,
            completedTaskIds: [],
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<TasksExecutionResult>> ExecuteWithDetailsAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        IEnumerable<string> completedTaskIds,
        CancellationToken cancellationToken = default)
    {
        var tasks = onExecuteTasks.ToList();
        var completedSet = completedTaskIds.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var executedTasks = new List<TaskExecutionSummary>();
        var totalStopwatch = Stopwatch.StartNew();

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
            tasksToExecute.Count, context.Instance.Id, skippedCount);

        // Group tasks by Order for parallel/sequential execution
        var taskGroups = tasksToExecute
            .GroupBy(t => t.Order)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in taskGroups)
        {
            var groupTasks = group.ToList();

            if (groupTasks.Count == 1)
            {
                // Single task - execute directly
                var result = await _executionEngine.ExecuteAsync(
                    groupTasks[0], instanceTransitionId, taskTrigger, context, cancellationToken);

                var processResult = ProcessTaskResult(result, groupTasks[0], executedTasks, totalStopwatch);
                if (processResult.HasValue)
                    return processResult.Value;
            }
            else
            {
                // Multiple tasks with same Order - execute in parallel with cancellation
                var parallelResult = await ExecuteTaskGroupInParallelAsync(
                    groupTasks, instanceTransitionId, taskTrigger, context, cancellationToken);

                if (!parallelResult.IsSuccess)
                {
                    totalStopwatch.Stop();
                    return parallelResult;
                }

                var groupResult = parallelResult.Value!;
                executedTasks.AddRange(groupResult.ExecutedTasks);

                // If any task in parallel group failed with blocking action, stop
                if (!groupResult.IsSuccess)
                {
                    totalStopwatch.Stop();
                    return parallelResult;
                }
            }
        }

        totalStopwatch.Stop();

        if (skippedCount > 0)
        {
            _logger.LogInformation(
                "Task coordination completed. Executed: {ExecutedCount}, Skipped: {SkippedCount}",
                executedTasks.Count, skippedCount);
        }

        return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
            executedTasks,
            totalStopwatch.ElapsedMilliseconds));
    }

    /// <summary>
    /// Processes single task result and returns failure result if needed.
    /// </summary>
    private Result<TasksExecutionResult>? ProcessTaskResult(
        Result<TasksExecutionResult> taskResult,
        OnExecuteTask onExecuteTask,
        List<TaskExecutionSummary> executedTasks,
        Stopwatch totalStopwatch)
    {
        // Infrastructure error
        if (!taskResult.IsSuccess)
        {
            totalStopwatch.Stop();
            var infraError = _errorFactory.CreateFromError(
                taskResult.Error,
                onExecuteTask.Task.Key,
                "Unknown",
                totalStopwatch.ElapsedMilliseconds);

            return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Failure(
                onExecuteTask,
                infraError,
                executedTasks,
                totalStopwatch.ElapsedMilliseconds));
        }

        var result = taskResult.Value!;

        // Business error with blocking action
        if (!result.IsSuccess)
        {
            totalStopwatch.Stop();
            return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
            {
                IsSuccess = false,
                HasFailedTasks = true,
                FailedTaskKeys = result.FailedTaskKeys,
                FailedTask = result.FailedTask ?? onExecuteTask,
                TaskError = result.TaskError,
                BoundaryAction = result.BoundaryAction,
                ExecutedTasks = executedTasks,
                TotalExecutionDurationMs = totalStopwatch.ElapsedMilliseconds
            });
        }

        // Success - add to executed list
        executedTasks.AddRange(result.ExecutedTasks);
        return null;
    }

    /// <summary>
    /// Executes a group of tasks with same Order in parallel.
    /// If one task fails, cancels all other tasks and triggers error boundary.
    /// </summary>
    private async Task<Result<TasksExecutionResult>> ExecuteTaskGroupInParallelAsync(
        List<OnExecuteTask> tasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;
        var executedTasks = new List<TaskExecutionSummary>();
        var stopwatch = Stopwatch.StartNew();

        _logger.LogDebug(
            "Executing {TaskCount} tasks in parallel for instance {InstanceId}",
            tasks.Count, context.Instance.Id);

        // Track first failure for error boundary (thread-safe)
        TasksExecutionResult? firstFailure = null;
        OnExecuteTask? firstFailedTask = null;

        var executionTasks = tasks.Select(async task =>
        {
            try
            {
                var result = await _executionEngine.ExecuteAsync(
                    task, instanceTransitionId, taskTrigger, context, linkedToken);

                if (!result.IsSuccess || !result.Value!.IsSuccess)
                {
                    // Use class-level lock per Microsoft guidelines
                    // https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock#guidelines
                    lock (_parallelTaskLock)
                    {
                        if (firstFailure == null)
                        {
                            firstFailure = result.Value;
                            firstFailedTask = task;
                            // Cancel other tasks
                            linkedCts.CancelAsync();
                        }
                    }
                }

                return (Task: task, Result: result);
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled due to another task failure
                return (Task: task, Result: Result<TasksExecutionResult>.Fail(
                    Error.Failure("TaskCancelled", $"Task {task.Task.Key} was cancelled")));
            }
        }).ToList();

        try
        {
            var results = await Task.WhenAll(executionTasks);

            stopwatch.Stop();

            // If there was a failure, return it with error boundary info
            if (firstFailure != null && firstFailedTask != null)
            {
                // Collect successful tasks before failure
                foreach (var (_, result) in results)
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
                    TotalExecutionDurationMs = stopwatch.ElapsedMilliseconds
                });
            }

            // All succeeded
            foreach (var (_, result) in results)
            {
                if (result.IsSuccess && result.Value != null)
                {
                    executedTasks.AddRange(result.Value.ExecutedTasks);
                }
            }

            return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
                executedTasks, stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
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
                    TotalExecutionDurationMs = stopwatch.ElapsedMilliseconds
                });
            }

            return Result<TasksExecutionResult>.Fail(Error.Failure("ParallelExecutionFailed", ex.Message));
        }
    }
}
