using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Decorator for ITaskCoordinator that adds error boundary handling.
/// Wraps task execution with error policy resolution, retry logic, and action execution.
/// </summary>
/// <remarks>
/// This decorator implements the Decorator Pattern to transparently add error boundary
/// capabilities to any ITaskCoordinator implementation. When error policies are defined
/// at the OnExecuteTask, state, or workflow level, this decorator will:
/// - Catch task execution failures
/// - Resolve the appropriate error policy from OnExecuteTask.ErrorBoundary
/// - Execute retry logic if configured
/// - Apply error actions (Abort, Ignore, Log, Notify, etc.)
/// - Propagate error-driven transitions to the pipeline
/// </remarks>
public sealed class ErrorBoundaryTaskCoordinatorDecorator : ITaskCoordinator
{
    private readonly ITaskCoordinator _innerCoordinator;
    private readonly IErrorPolicyResolver _errorPolicyResolver;
    private readonly IErrorActionExecutor _errorActionExecutor;
    private readonly ILogger<ErrorBoundaryTaskCoordinatorDecorator> _logger;

    // Thread-local context for transition execution context
    private static readonly AsyncLocal<TransitionExecutionContext?> _currentTransitionContext = new();

    /// <summary>
    /// Initializes a new instance of the ErrorBoundaryTaskCoordinatorDecorator.
    /// </summary>
    public ErrorBoundaryTaskCoordinatorDecorator(
        ITaskCoordinator innerCoordinator,
        IErrorPolicyResolver errorPolicyResolver,
        IErrorActionExecutor errorActionExecutor,
        ILogger<ErrorBoundaryTaskCoordinatorDecorator> logger)
    {
        _innerCoordinator = innerCoordinator;
        _errorPolicyResolver = errorPolicyResolver;
        _errorActionExecutor = errorActionExecutor;
        _logger = logger;
    }

    /// <summary>
    /// Sets the current transition execution context for error boundary resolution.
    /// This should be called by pipeline steps before executing tasks.
    /// </summary>
    public static void SetTransitionContext(TransitionExecutionContext? context)
    {
        _currentTransitionContext.Value = context;
    }

    /// <summary>
    /// Gets the current transition execution context.
    /// </summary>
    public static TransitionExecutionContext? GetTransitionContext()
    {
        return _currentTransitionContext.Value;
    }

    /// <inheritdoc />
    public async Task<Result> ExecuteAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var transitionContext = _currentTransitionContext.Value;

        // TransitionContext must be set by pipeline steps before task execution
        // if (transitionContext == null)
        // {
        //     throw new InvalidOperationException(
        //         "TransitionContext is not set. Ensure ErrorBoundaryTaskCoordinatorDecorator.SetTransitionContext() " +
        //         "is called by pipeline steps before executing tasks.");
        // }

        var tasks = onExecuteTasks.ToList();
        if (!tasks.Any())
            return Result.Ok();

        foreach (var onExecuteTask in tasks)
        {
            var result = await ExecuteTaskWithErrorBoundaryAsync(
                onExecuteTask,
                instanceTransitionId,
                taskTrigger,
                context,
                transitionContext,
                cancellationToken);

            if (!result.IsSuccess)
                return Result.Fail(result.Error);

            // Check if error action resulted in a transition request
            var actionResult = result.Value;
            if (actionResult != null)
            {
                if (actionResult.HasTransition)
                {
                    // Store transition request in context for pipeline handling
                    transitionContext.Items["ErrorBoundary:TransitionKey"] = actionResult.TransitionKey;
                    transitionContext.Directives.RequestErrorTransition(actionResult.TransitionKey!);
                }

                if (!actionResult.ShouldContinue)
                {
                    // Error action indicates stop (e.g., Abort)
                    if (actionResult.Error is { } error)
                        return Result.Fail(error);

                    // Graceful stop without error
                    return Result.Ok();
                }
            }
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExecuteConditionAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return _innerCoordinator.ExecuteConditionAsync(script, context, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<Definitions.Timer.TimerSchedule>> ExecuteTimerAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return _innerCoordinator.ExecuteTimerAsync(script, context, cancellationToken);
    }

    /// <summary>
    /// Executes a single task with error boundary handling and retry support.
    /// </summary>
    private async Task<Result<ErrorActionResult?>> ExecuteTaskWithErrorBoundaryAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext scriptContext,
        TransitionExecutionContext transitionContext,
        CancellationToken cancellationToken)
    {
        var retryAttempt = 0;
        var maxRetryLoops = 10; // Safety guard against infinite loops

        while (retryAttempt < maxRetryLoops)
        {
            // Execute the task through inner coordinator (single task)
            var executeResult = await _innerCoordinator.ExecuteAsync(
                new[] { onExecuteTask },
                instanceTransitionId,
                taskTrigger,
                scriptContext,
                cancellationToken);

            if (executeResult.IsSuccess)
            {
                // Task succeeded
                return Result<ErrorActionResult?>.Ok(null);
            }

            // Task failed - resolve error policy using OnExecuteTask (which contains ErrorBoundary)
            var errorContext = ErrorContextBuilder.Create()
                .WithError(executeResult.Error)
                .WithOnExecuteTask(onExecuteTask)
                .WithScope(ErrorBoundaryScope.Task)
                .FromContext(transitionContext)
                .WithAttempt(retryAttempt)
                .Build();

            var resolvedPolicy = _errorPolicyResolver.Resolve(transitionContext, errorContext, onExecuteTask);

            if (resolvedPolicy == null)
            {
                // No policy found, propagate error as unhandled
                _logger.LogWarning(
                    "No error policy found for task {TaskKey}, propagating error",
                    onExecuteTask.Task.Key);
                return Result<ErrorActionResult?>.Fail(executeResult.Error);
            }

            // Execute error action
            var actionResult = await _errorActionExecutor.ExecuteAsync(
                transitionContext,
                errorContext,
                resolvedPolicy,
                cancellationToken);

            // Handle retry
            if (actionResult.ShouldRetry)
            {
                retryAttempt++;
                _logger.LogInformation(
                    "Retrying task {TaskKey} after {Delay}ms (attempt {Attempt})",
                    onExecuteTask.Task.Key,
                    actionResult.RetryDelay.TotalMilliseconds,
                    retryAttempt);

                await Task.Delay(actionResult.RetryDelay, cancellationToken);
                continue;
            }

            // Handle continue (Ignore/Log actions)
            if (actionResult.ShouldContinue)
            {
                return Result<ErrorActionResult?>.Ok(actionResult);
            }

            // Handle stop (Abort/Rollback/Notify with transition)
            if (actionResult.Error is { } error)
            {
                return Result<ErrorActionResult?>.Fail(error);
            }

            return Result<ErrorActionResult?>.Ok(actionResult);
        }

        // Max retry loops exceeded (safety guard)
        return Result<ErrorActionResult?>.Fail(
            Error.Failure("task.retry.exceeded", $"Max retry loops exceeded for task {onExecuteTask.Task.Key}"));
    }
}

