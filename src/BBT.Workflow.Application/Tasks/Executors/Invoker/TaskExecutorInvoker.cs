using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Resilience;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Service for invoking task executors with error boundary and retry capabilities.
/// This is a scoped service - no per-invocation allocation required.
/// </summary>
/// <remarks>
/// Unlike the decorator pattern, this invoker:
/// - Is registered once as a scoped service (single instance per request)
/// - Accepts the executor as a method parameter
/// - Applies Polly pipelines and error policies centrally
/// - Zero allocation overhead per task execution
/// 
/// Capabilities:
/// - Polly-based retry with exponential backoff for transient failures
/// - HTTP status code based retries (408, 429, 500, 502, 503, 504)
/// - Error boundary policy resolution from OnExecuteTask.ErrorBoundary
/// - Error action execution (Abort, Ignore, Log, Notify, Transition)
/// </remarks>
public sealed class TaskExecutorInvoker : ITaskExecutorInvoker
{
    private readonly ITaskResiliencePipelineFactory _pipelineFactory;
    private readonly IErrorPolicyResolver _errorPolicyResolver;
    private readonly IErrorActionExecutor _errorActionExecutor;
    private readonly IErrorNormalizer _errorNormalizer;
    private readonly ILogger<TaskExecutorInvoker> _logger;

    // Thread-local context for transition execution context
    private static readonly AsyncLocal<TransitionExecutionContext?> _currentTransitionContext = new();

    /// <summary>
    /// Initializes a new instance of TaskExecutorInvoker.
    /// </summary>
    public TaskExecutorInvoker(
        ITaskResiliencePipelineFactory pipelineFactory,
        IErrorPolicyResolver errorPolicyResolver,
        IErrorActionExecutor errorActionExecutor,
        IErrorNormalizer errorNormalizer,
        ILogger<TaskExecutorInvoker> logger)
    {
        _pipelineFactory = pipelineFactory;
        _errorPolicyResolver = errorPolicyResolver;
        _errorActionExecutor = errorActionExecutor;
        _errorNormalizer = errorNormalizer;
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
    public async Task<Result<StandardTaskResponse>> InvokeAsync(
        ITaskExecutor executor,
        TaskExecutorContext context,
        CancellationToken cancellationToken = default)
    {
        var transitionContext = _currentTransitionContext.Value;
        var onExecuteTask = context.OnExecuteTask;
        var taskKey = context.Task.Key;

        // Get Retry ErrorHandlerRule from ErrorBoundary if available
        // This includes ErrorCodes, ErrorTypes, and RetryPolicy for precise retry matching
        var retryRule = GetRetryRuleFromErrorBoundary(onExecuteTask);

        // Create Polly pipeline that only retries for codes defined in the retry rule
        // Other error codes (e.g., those for Abort) will NOT be retried
        var pipeline = _pipelineFactory.CreateStandardResponsePipelineFromRule(taskKey, retryRule);

        var attemptNumber = 0;
        StandardTaskResponse? lastResponse = null;

        // Execute task through Polly pipeline
        var executeResult = await pipeline.ExecuteAsync(async ct =>
        {
            attemptNumber++;

            _logger.LogDebug(
                "Executing task {TaskKey} attempt {Attempt}",
                taskKey,
                attemptNumber);

            var result = await executor.ExecuteAsync(context, ct);

            if (result.IsSuccess)
            {
                lastResponse = result.Value;
            }

            return result;
        }, cancellationToken);

        // Check if execution succeeded (no Result.Fail AND no retryable StatusCode)
        if (executeResult.IsSuccess)
        {
            var response = executeResult.Value;

            // Check if response indicates success using standardized extension method
            if (response.IsSuccessful())
            {
                if (attemptNumber > 1)
                {
                    _logger.LogInformation(
                        "Task {TaskKey} succeeded after {Attempts} attempts",
                        taskKey,
                        attemptNumber);
                }
                return Result<StandardTaskResponse>.Ok(response!);
            }

            // Response has error or non-success StatusCode but not retryable - apply error boundary
            return await HandleResponseFailureAsync(
                context,
                response,
                transitionContext,
                attemptNumber,
                cancellationToken);
        }

        // Task failed after all retry attempts - apply error actions
        return await HandleExecutionFailureAsync(
            context,
            executeResult.Error,
            lastResponse,
            transitionContext,
            attemptNumber,
            cancellationToken);
    }

    /// <summary>
    /// Gets the complete Retry ErrorHandlerRule from the OnExecuteTask's ErrorBoundary.
    /// Returns the full rule including ErrorCodes, ErrorTypes, and RetryPolicy.
    /// </summary>
    private static ErrorHandlerRule? GetRetryRuleFromErrorBoundary(OnExecuteTask onExecuteTask)
    {
        var errorBoundary = onExecuteTask.ErrorBoundary;
        if (errorBoundary?.OnError == null || !errorBoundary.OnError.Any())
            return null;

        // Find the first retry action rule with a retry policy
        // The complete rule contains ErrorCodes/ErrorTypes that define WHAT to retry
        return errorBoundary.OnError
            .FirstOrDefault(r => r is { Action: ErrorAction.Retry, RetryPolicy: not null });
    }

    /// <summary>
    /// Handles failure when StandardTaskResponse indicates error (non-retryable).
    /// </summary>
    private async Task<Result<StandardTaskResponse>> HandleResponseFailureAsync(
        TaskExecutorContext context,
        StandardTaskResponse? response,
        TransitionExecutionContext? transitionContext,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        // Build error from response
        var error = response?.Error ?? Error.Failure(
            response?.ErrorCode ?? WorkflowErrorCodes.TaskExecution,
            response?.ErrorMessage ?? $"Task failed with StatusCode {response?.StatusCode}");

        return await HandleExecutionFailureAsync(
            context,
            error,
            response,
            transitionContext,
            attemptNumber,
            cancellationToken);
    }

    /// <summary>
    /// Handles execution failure after all Polly retries are exhausted.
    /// Applies remaining error actions (Abort, Ignore, Log, Notify, Transition).
    /// </summary>
    private async Task<Result<StandardTaskResponse>> HandleExecutionFailureAsync(
        TaskExecutorContext context,
        Error error,
        StandardTaskResponse? response,
        TransitionExecutionContext? transitionContext,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        var onExecuteTask = context.OnExecuteTask;
        var taskKey = context.Task.Key;

        if (transitionContext == null)
        {
            _logger.LogWarning(
                "No transition context available for error handling. Propagating error for task {TaskKey}",
                taskKey);
            return Result<StandardTaskResponse>.Fail(error);
        }

        // Build error context with normalized error for consistent boundary matching
        var errorContextBuilder = ErrorContextBuilder.Create(_errorNormalizer)
            .WithError(error)
            .WithOnExecuteTask(onExecuteTask)
            .WithScope(ErrorBoundaryScope.Task)
            .FromContext(transitionContext)
            .WithAttempt(attemptNumber);

        // Add StandardTaskResponse for normalized error creation
        // This enables consistent matching against ErrorCodes like "Task:Http:503"
        if (response != null)
        {
            errorContextBuilder.WithStandardResponse(response);
        }

        var errorContext = errorContextBuilder.Build();

        // Resolve non-retry error policy (Abort, Ignore, Log, Notify, Transition)
        var resolvedPolicy = _errorPolicyResolver.Resolve(
            transitionContext,
            errorContext,
            onExecuteTask,
            excludeRetry: true);

        if (resolvedPolicy == null)
        {
            _logger.LogWarning(
                "No non-retry error policy found for task {TaskKey} after {Attempts} attempts. Propagating error.",
                taskKey,
                attemptNumber);
            return Result<StandardTaskResponse>.Fail(error);
        }

        _logger.LogInformation(
            "Applying error action {Action} for task {TaskKey} after {Attempts} attempts (StatusCode: {StatusCode})",
            resolvedPolicy.Rule.Action,
            taskKey,
            attemptNumber,
            response?.StatusCode);

        // Execute error action
        var actionResult = await _errorActionExecutor.ExecuteAsync(
            transitionContext,
            errorContext,
            resolvedPolicy,
            cancellationToken);

        // Handle continue (Ignore/Log actions)
        if (actionResult.ShouldContinue)
        {
            // Create a response that indicates the error was handled
            var handledResponse = response ?? new StandardTaskResponse
            {
                IsSuccess = false,
                Error = error,
                ErrorCode = error.Code,
                ErrorMessage = error.Message,
                TaskType = context.Task.GetTaskType().ToString()
            };

            return Result<StandardTaskResponse>.Ok(handledResponse);
        }

        // Handle transition request
        if (actionResult.HasTransition)
        {
            // Store transition request in context for pipeline handling
            transitionContext.Items["TaskExecutorInvoker:TransitionKey"] = actionResult.TransitionKey;
            transitionContext.Directives.RequestErrorTransition(actionResult.TransitionKey!);
        }

        // Handle stop (Abort/Rollback/Notify with transition)
        if (actionResult.Error is { } actionError)
        {
            return Result<StandardTaskResponse>.Fail(actionError);
        }

        // Graceful stop with the response
        return Result<StandardTaskResponse>.Ok(response ?? new StandardTaskResponse
        {
            IsSuccess = false,
            Error = error,
            ErrorCode = error.Code,
            ErrorMessage = error.Message,
            TaskType = context.Task.GetTaskType().ToString()
        });
    }
}

