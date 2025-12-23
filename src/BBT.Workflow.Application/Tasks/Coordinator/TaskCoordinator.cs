using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Persistence;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Coordinates workflow task execution with support for both parallel and sequential execution strategies.
/// Implements condition and timer evaluation services.
/// Uses ITaskExecutorRegistry to resolve executors for each task type.
/// </summary>
public sealed class TaskCoordinator : ITaskCoordinator
{
    private readonly ITaskExecutorRegistry _executorRegistry;
    private readonly IConditionEvaluator _conditionEvaluator;
    private readonly ITimerEvaluator _timerEvaluator;
    private readonly ITaskFactory _taskFactory;
    private readonly ITaskPersistenceStrategyFactory _persistenceStrategyFactory;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IWorkflowMetrics _workflowMetrics;
    private readonly ILogger<TaskCoordinator> _logger;

    /// <summary>
    /// Initializes a new instance of TaskCoordinator.
    /// </summary>
    public TaskCoordinator(
        ITaskExecutorRegistry executorRegistry,
        IConditionEvaluator conditionEvaluator,
        ITimerEvaluator timerEvaluator,
        ITaskFactory taskFactory,
        ITaskPersistenceStrategyFactory persistenceStrategyFactory,
        IGuidGenerator guidGenerator,
        IWorkflowMetrics workflowMetrics,
        ILogger<TaskCoordinator> logger)
    {
        _executorRegistry = executorRegistry;
        _conditionEvaluator = conditionEvaluator;
        _timerEvaluator = timerEvaluator;
        _taskFactory = taskFactory;
        _persistenceStrategyFactory = persistenceStrategyFactory;
        _guidGenerator = guidGenerator;
        _workflowMetrics = workflowMetrics;
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
        var tasks = onExecuteTasks.ToList();

        if (!tasks.Any()) return Result.Ok();

        _logger.LogDebug("Coordinating execution of {TaskCount} tasks for instance {InstanceId}",
            tasks.Count, context.Instance.Id);

        // Check if tasks can be executed in parallel (no dependencies)
        var canExecuteInParallel = CanExecuteInParallel(tasks);

        if (canExecuteInParallel)
        {
            return await ExecuteTasksInParallelAsync(tasks, instanceTransitionId, taskTrigger, context,
                cancellationToken);
        }

        return await ExecuteTasksSequentiallyAsync(tasks, instanceTransitionId, taskTrigger, context,
            cancellationToken);
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

    /// <summary>
    /// Executes a single task with full lifecycle management.
    /// </summary>
    private async Task<Result> ExecuteTaskAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken)
    {
        // Load task from factory
        var taskResult = await _taskFactory.CreateExecutionTaskAsync(onExecuteTask.Task, cancellationToken);
        if (!taskResult.IsSuccess)
        {
            return Result.Fail(taskResult.Error);
        }

        var task = taskResult.Value!;
        var taskType = task.GetTaskType();
        var taskTypeStr = taskType.ToString();
        var workflowKey = context.Workflow.Key;
        var stopwatch = Stopwatch.StartNew();

        // Create instance task for tracking
        var instanceTask = new InstanceTask(
            _guidGenerator.Create(),
            instanceTransitionId ?? _guidGenerator.Create(),
            task.Key);

        // Get persistence strategy
        var persistenceStrategy = GetPersistenceStrategy(taskTrigger);

        // Record metrics start
        _workflowMetrics.RecordTaskExecuted(taskTypeStr, workflowKey);
        _workflowMetrics.StartTaskExecution(taskTypeStr, workflowKey);

        _logger.LogInformation("Executing task {TaskKey} of type {TaskType} for instance {InstanceId}",
            task.Key, taskType, context.Instance.Id);

        // Persist creation (non-blocking)
        await PersistCreationAsync(persistenceStrategy, instanceTask, task.Key, cancellationToken);

        // Get executor and execute
        var executorResult = _executorRegistry.GetExecutor(taskType);
        if (!executorResult.IsSuccess)
        {
            stopwatch.Stop();
            RecordFailure(instanceTask, persistenceStrategy, taskTypeStr, workflowKey, stopwatch,
                executorResult.Error, cancellationToken);
            return Result.Fail(executorResult.Error);
        }

        // Create executor context
        var executorContext = new TaskExecutorContext(
            task,
            onExecuteTask,
            context,
            instanceTransitionId,
            taskTrigger);

        // Execute
        var executeResult = await executorResult.Value!.ExecuteAsync(executorContext, cancellationToken);

        stopwatch.Stop();
        
        if (!executeResult.IsSuccess)
        {
            RecordFailure(instanceTask, persistenceStrategy, taskTypeStr, workflowKey, stopwatch,
                executeResult.Error, cancellationToken);
            // Persist completion (non-blocking)
            await PersistCompletionAsync(persistenceStrategy, instanceTask, task.Key, cancellationToken);
            return Result.Fail(executeResult.Error);
        }

        // Complete task
        var response = executeResult.Value!;

        if (response.Error.HasValue)
        {
            RecordFailure(instanceTask, persistenceStrategy, taskTypeStr, workflowKey, stopwatch,
                response.Error.Value, cancellationToken);
            // Persist completion (non-blocking)
            await PersistCompletionAsync(persistenceStrategy, instanceTask, task.Key, cancellationToken);
            return Result.Fail(response.Error.Value);
        }
        
        ApplyOutputToContext(task, response, taskTrigger, context);
        instanceTask.Completed(new JsonData(JsonSerializer.Serialize(response, JsonSerializerConstants.JsonOptions)));

        // Persist completion (non-blocking)
        await PersistCompletionAsync(persistenceStrategy, instanceTask, task.Key, cancellationToken);

        // Record success metrics
        _workflowMetrics.RecordTaskCompleted(taskTypeStr, workflowKey, stopwatch.Elapsed.TotalSeconds);
        _workflowMetrics.FinishTaskExecution(taskTypeStr, workflowKey);

        return Result.Ok();
    }

    /// <summary>
    /// Executes multiple tasks in parallel.
    /// </summary>
    private async Task<Result> ExecuteTasksInParallelAsync(
        List<OnExecuteTask> tasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken)
    {
        var executionTasks = tasks.Select(task =>
            ExecuteTaskAsync(task, instanceTransitionId, taskTrigger, context, cancellationToken));

        var results = await Task.WhenAll(executionTasks);

        // Check for any failures
        if (results.Any(r => !r.IsSuccess))
        {
            var firstFailure = results.First(r => !r.IsSuccess);
            return Result.Fail(firstFailure.Error);
        }

        return Result.Ok();
    }

    /// <summary>
    /// Executes multiple tasks in parallel.
    /// </summary>
    private async Task<Result> ExecuteTasksSequentiallyAsync(
        List<OnExecuteTask> tasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken)
    {
        foreach (var task in tasks)
        {
            var result = await ExecuteTaskAsync(task, instanceTransitionId, taskTrigger, context, cancellationToken);
            if (!result.IsSuccess)
            {
                return Result.Fail(result.Error);
            }
        }

        return Result.Ok();
    }

    /// <summary>
    /// Gets the persistence strategy for the given task trigger.
    /// </summary>
    private ITaskPersistenceStrategy? GetPersistenceStrategy(TaskTrigger taskTrigger)
    {
        var strategyResult = _persistenceStrategyFactory.GetStrategy(taskTrigger);
        if (!strategyResult.IsSuccess)
        {
            _logger.LogDebug("No persistence strategy found for trigger {TaskTrigger}, skipping persistence",
                taskTrigger);
            return null;
        }

        return strategyResult.Value;
    }

    /// <summary>
    /// Persists task creation (non-blocking).
    /// </summary>
    private async Task PersistCreationAsync(
        ITaskPersistenceStrategy? strategy,
        InstanceTask instanceTask,
        string taskKey,
        CancellationToken cancellationToken)
    {
        if (strategy == null)
            return;
        await strategy.HandleCreationAsync(instanceTask, cancellationToken);
    }

    /// <summary>
    /// Persists task completion (non-blocking).
    /// </summary>
    private async Task PersistCompletionAsync(
        ITaskPersistenceStrategy? strategy,
        InstanceTask instanceTask,
        string taskKey,
        CancellationToken cancellationToken)
    {
        if (strategy == null)
            return;
        await strategy.HandleCompletionAsync(instanceTask, cancellationToken);
    }

    /// <summary>
    /// Records task failure metrics and persists the failure.
    /// </summary>
    private void RecordFailure(
        InstanceTask instanceTask,
        ITaskPersistenceStrategy? strategy,
        string taskType,
        string workflowKey,
        Stopwatch stopwatch,
        Error error,
        CancellationToken cancellationToken)
    {
        instanceTask.Faulted(error);

        // Fire and forget persistence for failed tasks
        if (strategy != null)
        {
            _ = strategy.HandleCompletionAsync(instanceTask, cancellationToken);
        }

        _workflowMetrics.RecordTaskFailed(taskType, workflowKey, stopwatch.Elapsed.TotalSeconds);
        _workflowMetrics.FinishTaskExecution(taskType, workflowKey);

        _logger.LogError("Task {TaskKey} failed: {Error}", instanceTask.TaskId, error.Message);
    }

    /// <summary>
    /// Applies the output response to the script context.
    /// </summary>
    private void ApplyOutputToContext(
        WorkflowTask task,
        StandardTaskResponse response,
        TaskTrigger taskTrigger,
        ScriptContext context)
    {
        // Store in TaskResponse WARN: No needed
        // var variableKey = task.Key.ToVariableName();
        // context.TaskResponse[variableKey] = response;

        // Add to instance data if not an extension task and has data
        if (taskTrigger != TaskTrigger.Extension && response.Data is not null)
        {
            context.Instance.AddData(
                _guidGenerator.Create(),
                new JsonData(JsonSerializer.Serialize(response.Data, JsonSerializerConstants.JsonOptions)),
                VersionStrategy.IncreasePatch);
        }
    }

    private static bool CanExecuteInParallel(IList<OnExecuteTask> tasks)
    {
        // Simple heuristic: if tasks have different orders, they might have dependencies
        // In a more sophisticated implementation, you would analyze actual dependencies
        var orders = tasks.Select(t => t.Order).Distinct().ToList();
        return orders.Count == 1 || tasks.Count == 1;
    }
}