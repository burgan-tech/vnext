using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Aspects;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Persistence;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Executes single tasks with full lifecycle management.
/// Handles factory creation, execution, error boundary resolution, persistence, and metrics.
/// </summary>
/// <remarks>
/// Extracted from TaskCoordinator following SRP.
/// Integrates with consolidated ErrorBoundary services from Execution/ErrorHandling.
/// </remarks>
public sealed class TaskExecutionEngine : ITaskExecutionEngine
{
    private readonly ITaskExecutorRegistry _executorRegistry;
    private readonly ITaskFactory _taskFactory;
    private readonly ITaskPersistenceStrategyFactory _persistenceStrategyFactory;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IWorkflowMetrics _workflowMetrics;
    private readonly IErrorBoundaryResolver _boundaryResolver;
    private readonly IErrorActionExecutor _actionExecutor;
    private readonly IExecutionErrorFactory _errorFactory;
    private readonly ILogger<TaskExecutionEngine> _logger;

    /// <summary>
    /// Initializes a new instance of TaskExecutionEngine.
    /// </summary>
    public TaskExecutionEngine(
        ITaskExecutorRegistry executorRegistry,
        ITaskFactory taskFactory,
        ITaskPersistenceStrategyFactory persistenceStrategyFactory,
        IGuidGenerator guidGenerator,
        IWorkflowMetrics workflowMetrics,
        IErrorBoundaryResolver boundaryResolver,
        IErrorActionExecutor actionExecutor,
        IExecutionErrorFactory errorFactory,
        ILogger<TaskExecutionEngine> logger)
    {
        _executorRegistry = executorRegistry;
        _taskFactory = taskFactory;
        _persistenceStrategyFactory = persistenceStrategyFactory;
        _guidGenerator = guidGenerator;
        _workflowMetrics = workflowMetrics;
        _boundaryResolver = boundaryResolver;
        _actionExecutor = actionExecutor;
        _errorFactory = errorFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    [Trace]
    public async Task<Result<TasksExecutionResult>> ExecuteAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken)
    {
        // 1. Build boundary chain upfront (Task -> State -> Global)
        var boundaryChain = CompiledBoundaryChain.Compile(
            onExecuteTask.ErrorBoundary,
            GetStateBoundary(context),
            context.Workflow?.ErrorBoundary);

        Activity.Current?.SetDisplayName($"Task.Execute.{onExecuteTask.Task.Key}");
        var activity = Activity.Current;
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.TaskKey, onExecuteTask.Task.Key);
            activity.SetTag(TelemetryConstants.TagNames.InstanceId, context.Instance?.Id.ToString());
            activity.SetTag(TelemetryConstants.TagNames.Flow, context.Workflow?.Key);
        }

        _logger.LogInformation(
            "Executing task {TaskKey} with error-aware retry.",
            onExecuteTask.Task.Key);

        // 2. Execute with error-aware retry (retry policy resolved per failure from matching rule)
        return await ExecuteWithErrorAwareRetryAsync(
            onExecuteTask, instanceTransitionId, taskTrigger, context,
            boundaryChain, cancellationToken);
    }

    /// <summary>
    /// Executes a task with error-aware retry. Resolves retry policy per failure from the rule
    /// that matches the current error (Task -> State -> Global). Only retries when the matching rule has Action = Retry.
    /// After retries are exhausted or no retry applies, resolves boundary for fallback actions.
    /// </summary>
    /// <param name="onExecuteTask">The task definition to execute.</param>
    /// <param name="instanceTransitionId">Optional transition ID for tracking.</param>
    /// <param name="taskTrigger">The trigger type for persistence strategy selection.</param>
    /// <param name="context">The script context containing workflow and instance data.</param>
    /// <param name="boundaryChain">The compiled boundary chain for resolution and fallback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution result with boundary action if applicable.</returns>
    private async Task<Result<TasksExecutionResult>> ExecuteWithErrorAwareRetryAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CompiledBoundaryChain boundaryChain,
        CancellationToken cancellationToken)
    {
        var taskKey = onExecuteTask.Task.Key;
        var totalStopwatch = Stopwatch.StartNew();
        Result<TasksExecutionResult> result;

        try
        {
            var attempt = 0;

            while (true)
            {
                result = await ExecuteCoreAsync(onExecuteTask, instanceTransitionId, taskTrigger, context, boundaryChain, cancellationToken);

                // Success - return directly
                if (result is { IsSuccess: true, Value.IsSuccess: true })
                {
                    totalStopwatch.Stop();
                    Activity.Current?.SetStatus(ActivityStatusCode.Ok);
                    _logger.LogInformation(
                        "Task {TaskKey} completed successfully. Total duration: {Duration}ms",
                        taskKey, totalStopwatch.ElapsedMilliseconds);
                    return result;
                }

                // No boundary + business failure (e.g. 404) - flow continues; do not resolve fallback
                if (result is { IsSuccess: true, Value: var value } && value is { TaskError: null, HasFailedTasks: true })
                {
                    totalStopwatch.Stop();
                    _logger.LogDebug(
                        "Task {TaskKey} failed with business error but no ErrorBoundary defined. Flow will continue with auto-transitions.",
                        taskKey);
                    return result;
                }

                // Failure - resolve execution error and check if we should retry
                ExecutionError? executionError = result.IsSuccess && result.Value != null ? result.Value.TaskError : null;
                if (executionError == null)
                {
                    executionError = _errorFactory.CreateFromError(
                        result.Error, taskKey, "Unknown", totalStopwatch.ElapsedMilliseconds);
                }

                var retryPolicy = GetRetryPolicyForError(boundaryChain, executionError.NormalizedError);
                if (retryPolicy == null || attempt >= retryPolicy.MaxRetries)
                    break;

                var delay = ApplyJitterIfNeeded(retryPolicy.CalculateDelay(attempt + 1), retryPolicy.UseJitter);
                _logger.LogInformation(
                    "Error-aware retry {Attempt}/{MaxRetries} for task {TaskKey}. Delay: {Delay}ms. Error: {Error}",
                    attempt + 1, retryPolicy.MaxRetries, taskKey, delay.TotalMilliseconds, executionError.ErrorMessage);

                TaskExecutionActivityHelper.AddRetryEvent(
                    Activity.Current, attempt + 1, retryPolicy.MaxRetries, executionError?.ErrorMessage, delay);

                await Task.Delay(delay, cancellationToken);
                attempt++;
            }

            totalStopwatch.Stop();

            // Retry exhausted or matching rule is not Retry - resolve boundary for fallback actions
            return await HandlePostRetryFailureAsync(
                result,
                onExecuteTask,
                boundaryChain,
                totalStopwatch.ElapsedMilliseconds,
                cancellationToken);
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            _logger.LogError(ex, "Task execution failed with exception for {TaskKey}", taskKey);

            Activity.Current?.RecordExceptionWithStatus(ex, $"Task {taskKey} threw unhandled exception");

            var taskError = _errorFactory.CreateFromException(ex, taskKey, "Unknown", totalStopwatch.ElapsedMilliseconds);
            return Result<TasksExecutionResult>.Fail(taskError.ToError());
        }
    }

    /// <summary>
    /// Handles failure after retry is exhausted.
    /// Resolves boundary for fallback actions (Abort, Notify, Rollback, Log, Ignore).
    /// Retry action is excluded since retries are already exhausted.
    /// </summary>
    /// <param name="failedResult">The failed result from execution (after retries exhausted or no retry applies).</param>
    /// <param name="onExecuteTask">The task definition that failed.</param>
    /// <param name="boundaryChain">The compiled boundary chain for resolution.</param>
    /// <param name="totalDurationMs">Total execution duration including retries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution result with appropriate boundary action.</returns>
    private async Task<Result<TasksExecutionResult>> HandlePostRetryFailureAsync(
        Result<TasksExecutionResult> failedResult,
        OnExecuteTask onExecuteTask,
        CompiledBoundaryChain boundaryChain,
        long totalDurationMs,
        CancellationToken cancellationToken)
    {
        var taskKey = onExecuteTask.Task.Key;

        // Extract execution error from result
        var executionError = failedResult.Value?.TaskError;
        if (executionError == null)
        {
            // No TaskError with successful Result = no boundary + business failure; should not reach here
            // (ExecuteWithPollyAsync returns early). Return result so pipeline continues.
            if (failedResult is { IsSuccess: true, Value: not null })
            {
                _logger.LogDebug(
                    "Task {TaskKey}: no TaskError with successful Result (no-boundary business failure). Returning result for pipeline continuation.",
                    taskKey);
                return failedResult;
            }

            // Infrastructure error - create from Result.Error
            executionError = _errorFactory.CreateFromError(
                failedResult.Error, taskKey, "Unknown", totalDurationMs);
        }

        _logger.LogWarning(
            "Retry exhausted for task {TaskKey}. Resolving fallback boundary action (excluding Retry). Error: {Error}",
            taskKey, executionError.ErrorMessage);

        // Resolve boundary for fallback actions - EXCLUDE Retry since it's already exhausted
        var resolution = _boundaryResolver.ResolveExcluding(
            executionError.NormalizedError,
            boundaryChain,
            ErrorAction.Retry);

        // No matching boundary found (excluding Retry) - let pipeline handle as fault
        if (!resolution.IsHandled)
        {
            _logger.LogWarning(
                "No fallback boundary found for task {TaskKey}. Pipeline will mark instance as faulted.",
                taskKey);

            TaskExecutionActivityHelper.SetError(Activity.Current, executionError?.ErrorMessage, executionError?.TaskType, executionError?.StatusCode);
            TaskExecutionActivityHelper.AddFailedEvent(Activity.Current, executionError?.ErrorMessage, executionError?.TaskType, executionError?.StatusCode);

            // Return failure without boundary action - pipeline will fault
            return Result<TasksExecutionResult>.Fail(executionError!.ToError());
        }

        // Execute matched action (Abort, Notify, Rollback, Log, Ignore)
        var actionResult = await _actionExecutor.ExecuteAsync(
            resolution,
            executionError,
            // Retry executor is not used here since retries are exhausted
            (_, _) => Task.FromResult(Result<ActionExecutionResult>.Ok(
                ActionExecutionResult.Abort(executionError.ToError(), null, executionError))),
            cancellationToken);

        return ConvertActionResult(actionResult, onExecuteTask, executionError, totalDurationMs);
    }

    /// <summary>
    /// Converts ActionExecutionResult to TasksExecutionResult.
    /// </summary>
    private static Result<TasksExecutionResult> ConvertActionResult(
        ActionExecutionResult actionResult,
        OnExecuteTask onExecuteTask,
        ExecutionError executionError,
        long executionDurationMs)
    {
        if (actionResult.ShouldContinue)
        {
            // Ignore/Log or retry succeeded - pipeline continues
            var summary = TaskExecutionSummary.Failure(
                onExecuteTask.Task.Key,
                executionError.TaskType,
                executionError.ErrorMessage,
                executionError.StatusCode,
                executionDurationMs);

            return Result<TasksExecutionResult>.Ok(TasksExecutionResult.SuccessWithFailedTasks(
                [summary],
                executionDurationMs));
        }

        // Create boundary result for TasksExecutionResult
        TaskExecutionActivityHelper.SetError(Activity.Current, executionError.ErrorMessage, executionError.TaskType, executionError.StatusCode);
        TaskExecutionActivityHelper.AddFailedEvent(Activity.Current, executionError.ErrorMessage, executionError.TaskType, executionError.StatusCode);

        var boundaryResult = new BoundaryActionResult
        {
            Action = actionResult.ExecutedAction,
            TransitionKey = actionResult.TransitionKey,
            ResolvedAtLevel = actionResult.ResolvedAtLevel,
            ShouldRetry = false
        };

        return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
        {
            IsSuccess = false,
            HasFailedTasks = true,
            FailedTaskKeys = [onExecuteTask.Task.Key],
            FailedTask = onExecuteTask,
            TaskError = executionError,
            BoundaryAction = boundaryResult,
            ExecutedTasks = [],
            TotalExecutionDurationMs = executionDurationMs
        });
    }

    /// <summary>
    /// Gets the retry policy for the given error from the boundary chain.
    /// Uses the first matching rule (Task -> State -> Global) and returns its RetryPolicy
    /// only if that rule's action is Retry and has a valid policy with MaxRetries > 0.
    /// </summary>
    /// <param name="boundaryChain">The compiled boundary chain to search.</param>
    /// <param name="error">The normalized error to match.</param>
    /// <returns>The retry policy for the matching rule, or null if no Retry rule matches or policy is invalid.</returns>
    private static RetryPolicy? GetRetryPolicyForError(CompiledBoundaryChain boundaryChain, NormalizedError error)
    {
        var match = boundaryChain.FindMatch(error);
        if (!match.HasValue)
            return null;

        var handlerRule = match.Value.Rule.Rule;
        if (handlerRule.Action != ErrorAction.Retry || handlerRule.RetryPolicy == null || handlerRule.RetryPolicy.MaxRetries <= 0)
            return null;

        return handlerRule.RetryPolicy;
    }

    /// <summary>
    /// Applies jitter to the delay when enabled (±25%) to prevent thundering herd.
    /// </summary>
    private static TimeSpan ApplyJitterIfNeeded(TimeSpan delay, bool useJitter)
    {
        if (!useJitter)
            return delay;

        const double jitterFactor = 0.25;
        var jitter = delay.TotalMilliseconds * jitterFactor * (Random.Shared.NextDouble() * 2 - 1);
        var adjustedDelay = delay.TotalMilliseconds + jitter;
        return TimeSpan.FromMilliseconds(Math.Max(0, adjustedDelay));
    }

    /// <summary>
    /// Gets state-level error boundary from script context.
    /// Resolves the current state from Instance.CurrentState key and Workflow definition.
    /// </summary>
    private static ErrorBoundary? GetStateBoundary(ScriptContext context)
    {
        var instance = context.Instance;
        var workflow = context.Workflow;

        if (string.IsNullOrEmpty(instance?.CurrentState))
            return null;

        var state = workflow?.FindState(instance.CurrentState);
        return state?.ErrorBoundary;
    }



    /// <summary>
    /// Gets the persistence strategy for the given task trigger.
    /// </summary>
    private ITaskPersistenceStrategy? GetPersistenceStrategy(TaskTrigger taskTrigger)
    {
        var strategyResult = _persistenceStrategyFactory.GetStrategy(taskTrigger);
        if (!strategyResult.IsSuccess)
        {
            _logger.LogDebug(
                "No persistence strategy found for trigger {TaskTrigger}, skipping persistence",
                taskTrigger);
            return null;
        }

        return strategyResult.Value;
    }

    /// <summary>
    /// Persists task creation.
    /// </summary>
    private static async Task PersistCreationAsync(
        ITaskPersistenceStrategy? strategy,
        InstanceTask instanceTask,
        CancellationToken cancellationToken)
    {
        if (strategy == null)
            return;
        await strategy.HandleCreationAsync(instanceTask, cancellationToken);
    }

    /// <summary>
    /// Persists task completion.
    /// </summary>
    private static async Task PersistCompletionAsync(
        ITaskPersistenceStrategy? strategy,
        InstanceTask instanceTask,
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
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        instanceTask.Faulted(errorMessage ?? "Unknown error");

        if (strategy != null)
        {
            _ = strategy.HandleCompletionAsync(instanceTask, cancellationToken);
        }

        _workflowMetrics.RecordTaskFailed(taskType, workflowKey, stopwatch.Elapsed.TotalSeconds);
        _workflowMetrics.FinishTaskExecution(taskType, workflowKey);

        _logger.LogError("Task {TaskKey} failed: {Error}", instanceTask.TaskId, errorMessage);
    }

    /// <summary>
    /// Applies the output response to the script context.
    /// </summary>
    private void ApplyOutputToContext(
        StandardTaskResponse response,
        TaskTrigger taskTrigger,
        ScriptContext context)
    {
        if (taskTrigger != TaskTrigger.Extension && response.Data is not null)
        {
            context.Instance?.AddData(
                _guidGenerator.Create(),
                new JsonData(JsonSerializer.Serialize(response.Data, JsonSerializerConstants.JsonOptions)),
                VersionStrategy.IncreasePatch);
        }
    }

    /// <summary>
    /// Executes task without boundary resolution. Used by error-aware retry loop.
    /// Returns raw execution result for retry evaluation - does NOT trigger boundary resolution.
    /// This prevents infinite loops where retry → ExecuteAsync → HandleBusinessFailureAsync → retry.
    /// </summary>
    private async Task<Result<TasksExecutionResult>> ExecuteCoreAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CompiledBoundaryChain boundaryChain,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1. Load task from factory
        var taskResult = await _taskFactory.CreateExecutionTaskAsync(onExecuteTask.Task, cancellationToken);
        if (!taskResult.IsSuccess)
        {
            stopwatch.Stop();
            var error = _errorFactory.CreateFromException(
                new InvalidOperationException(taskResult.Error.Message ?? "Failed to create task"),
                onExecuteTask.Task.Key,
                "Unknown",
                stopwatch.ElapsedMilliseconds);

            TaskExecutionActivityHelper.AddFailedEvent(Activity.Current, error.ErrorMessage, "TaskFactoryError");

            return Result<TasksExecutionResult>.Fail(error.ToError());
        }

        var task = taskResult.Value!;
        var taskType = task.GetTaskType();
        var taskTypeStr = taskType.ToString();
        var workflowKey = context.Workflow?.Key ?? "N/A";

        Activity.Current?.SetTag(TelemetryConstants.TagNames.TaskType, taskTypeStr);

        // 2. Create instance task for tracking
        var instanceTask = new InstanceTask(
            _guidGenerator.Create(),
            instanceTransitionId ?? _guidGenerator.Create(),
            task.Key);

        // 3. Get persistence strategy
        var persistenceStrategy = GetPersistenceStrategy(taskTrigger);

        // 4. Record metrics start
        _workflowMetrics.RecordTaskExecuted(taskTypeStr, workflowKey);
        _workflowMetrics.StartTaskExecution(taskTypeStr, workflowKey);

        _logger.LogDebug(
            "Executing task {TaskKey} of type {TaskType} for instance {InstanceId} (retry attempt)",
            task.Key, taskType, context.Instance?.Id);

        // 5. Persist creation
        await PersistCreationAsync(persistenceStrategy, instanceTask, cancellationToken);

        // 6. Get executor
        var executorResult = _executorRegistry.GetExecutor(taskType);
        if (!executorResult.IsSuccess)
        {
            stopwatch.Stop();
            RecordFailure(instanceTask, persistenceStrategy, taskTypeStr, workflowKey, stopwatch,
                executorResult.Error.Message, cancellationToken);

            var infraError = _errorFactory.CreateFromError(
                executorResult.Error, task.Key, taskTypeStr, stopwatch.ElapsedMilliseconds);

            TaskExecutionActivityHelper.AddFailedEvent(Activity.Current, infraError.ErrorMessage, "ExecutorNotFound");

            // Return failure without boundary resolution - not retriable
            return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Failure(
                onExecuteTask, infraError, [], stopwatch.ElapsedMilliseconds));
        }

        // 7. Create executor context
        var executorContext = new TaskExecutorContext(
            task, onExecuteTask, context, instanceTransitionId, taskTrigger);

        // 8. Execute task
        var executeResult = await executorResult.Value!.ExecuteAsync(executorContext, cancellationToken);

        var requestPayload = new
        {
            Task = task,
            executorContext.InputResponse
        };
        var requestJson = new JsonData(JsonSerializer.Serialize(
            requestPayload,
            JsonSerializerConstants.JsonOptions));
        instanceTask.SetRequest(requestJson);

        if (executorContext.RawInvocationResultJson != null)
        {
            instanceTask.SetInvocationResult(new JsonData(executorContext.RawInvocationResultJson));
        }

        stopwatch.Stop();

        // 9. Handle infrastructure error - return without boundary resolution (not retriable)
        if (!executeResult.IsSuccess)
        {
            RecordFailure(instanceTask, persistenceStrategy, taskTypeStr, workflowKey, stopwatch,
                executeResult.Error.Message ?? "Unknown infrastructure error", cancellationToken);

            var infraError = _errorFactory.CreateFromError(
                executeResult.Error, task.Key, taskTypeStr, stopwatch.ElapsedMilliseconds);

            TaskExecutionActivityHelper.AddFailedEvent(Activity.Current, infraError.ErrorMessage, taskTypeStr);

            return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Failure(
                onExecuteTask, infraError, [], stopwatch.ElapsedMilliseconds));
        }

        // 10. Process response
        var response = executeResult.Value!;
        var responseJson = new JsonData(JsonSerializer.Serialize(response, JsonSerializerConstants.JsonOptions));

        // ALWAYS apply output to context
        ApplyOutputToContext(response, taskTrigger, context);

        // Mark task completed with business status
        instanceTask.Completed(responseJson, isBusinessSuccess: response.IsSuccess);

        // Persist completion
        await PersistCompletionAsync(persistenceStrategy, instanceTask, cancellationToken);

        // Record metrics
        if (response.IsSuccess)
        {
            _workflowMetrics.RecordTaskCompleted(taskTypeStr, workflowKey, stopwatch.Elapsed.TotalSeconds);
        }
        else
        {
            _workflowMetrics.RecordTaskFailed(taskTypeStr, workflowKey, stopwatch.Elapsed.TotalSeconds);
        }
        _workflowMetrics.FinishTaskExecution(taskTypeStr, workflowKey);

        // 11. Handle business failure
        // response.IsSuccess is already overridden by AcceptedStatusCodes in TaskExecutorBase,
        // so a matching accepted code will arrive here as IsSuccess = true and fall through to step 12.
        if (!response.IsSuccess)
        {
            var executionError = _errorFactory.CreateFromResponse(
                response, task.Key, taskTypeStr, stopwatch.ElapsedMilliseconds);

            // Critical Decision: Check if ErrorBoundary is configured
            // - No boundary: Developer manages via auto-transitions (e.g., 404 → NotFound state)
            // - Has boundary: Apply retry/fallback policies
            if (!boundaryChain.HasAnyBoundary)
            {
                // No ErrorBoundary - let flow continue with auto-transitions
                _logger.LogDebug(
                    "Task {TaskKey} failed with business error (StatusCode: {StatusCode}), " +
                    "but no ErrorBoundary is defined. Flow will continue with auto-transitions.",
                    task.Key, response.StatusCode);

                // Add event only — span Status stays OK since the flow continues normally via auto-transitions
                TaskExecutionActivityHelper.AddFailedEvent(Activity.Current, executionError.ErrorMessage, taskTypeStr, response.StatusCode);

                var failureSummary = TaskExecutionSummary.Failure(
                    task.Key, taskTypeStr, executionError.ErrorMessage,
                    response.StatusCode, stopwatch.ElapsedMilliseconds);

                // Return success to let pipeline continue (auto-transitions will handle routing)
                return Result<TasksExecutionResult>.Ok(
                    TasksExecutionResult.SuccessWithFailedTasks(
                        [failureSummary], stopwatch.ElapsedMilliseconds));
            }

            // ErrorBoundary exists - return failure for retry evaluation
            _logger.LogDebug(
                "Task {TaskKey} failed with business error (StatusCode: {StatusCode}). " +
                "ErrorBoundary is configured - retry policy will be evaluated.",
                task.Key, response.StatusCode);

            // Add event per attempt so each failure is visible in the trace timeline
            TaskExecutionActivityHelper.AddFailedEvent(Activity.Current, executionError.ErrorMessage, executionError.TaskType, response.StatusCode);

            return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
            {
                IsSuccess = false,
                HasFailedTasks = true,
                FailedTaskKeys = [task.Key],
                FailedTask = onExecuteTask,
                TaskError = executionError,
                BoundaryAction = null, // No boundary action yet - raw result for retry
                ExecutedTasks = [],
                TotalExecutionDurationMs = stopwatch.ElapsedMilliseconds
            });
        }

        // 12. Business success
        var summary = TaskExecutionSummary.Success(
            task.Key, taskTypeStr, response.StatusCode, stopwatch.ElapsedMilliseconds);

        return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
            [summary], stopwatch.ElapsedMilliseconds));
    }
}

