using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Transitions.Pipeline;

/// <summary>
/// Middleware for pipeline step execution with cross-cutting concerns.
/// Wraps step failures, resolves error policies, and executes appropriate actions.
/// Supports State-level and Global-level error handling for non-task errors.
/// Implements scheduled retry for pipeline steps via background jobs.
/// </summary>
/// <remarks>
/// This middleware can be extended for various inspection/handling scenarios:
/// - Error boundary handling with configurable policies
/// - Retry scheduling via background jobs
/// - Logging and metrics collection
/// - Circuit breaker patterns
/// - Rate limiting
///
/// The TransitionPipeline uses this middleware to handle step-level errors
/// with configurable policies.
/// </remarks>
public sealed class StepExecutionMiddleware
{
    private readonly IErrorPolicyResolver _errorPolicyResolver;
    private readonly IErrorActionExecutor _errorActionExecutor;
    private readonly IErrorNormalizer _errorNormalizer;
    private readonly IBackgroundJobService _backgroundJobService;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceJobRepository _jobRepository;
    private readonly IWorkflowMetrics _workflowMetrics;
    private readonly ILogger<StepExecutionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of StepExecutionMiddleware.
    /// </summary>
    public StepExecutionMiddleware(
        IErrorPolicyResolver errorPolicyResolver,
        IErrorActionExecutor errorActionExecutor,
        IErrorNormalizer errorNormalizer,
        IBackgroundJobService backgroundJobService,
        IInstanceRepository instanceRepository,
        IInstanceJobRepository jobRepository,
        IWorkflowMetrics workflowMetrics,
        ILogger<StepExecutionMiddleware> logger)
    {
        _errorPolicyResolver = errorPolicyResolver;
        _errorActionExecutor = errorActionExecutor;
        _errorNormalizer = errorNormalizer;
        _backgroundJobService = backgroundJobService;
        _instanceRepository = instanceRepository;
        _jobRepository = jobRepository;
        _workflowMetrics = workflowMetrics;
        _logger = logger;
    }

    /// <summary>
    /// Executes a pipeline step with middleware handling.
    /// If the step fails, attempts to resolve and apply an error policy.
    /// </summary>
    /// <param name="step">The pipeline step to execute.</param>
    /// <param name="context">The transition execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The step outcome, potentially modified by error handling.</returns>
    public async Task<Result<StepOutcome>> ExecuteAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var stepResult = await step.ExecuteAsync(context, cancellationToken);

        if (stepResult.IsSuccess)
        {
            return stepResult;
        }

        // Step failed - attempt error handling
        return await HandleStepErrorAsync(step, context, stepResult.Error, cancellationToken);
    }

    /// <summary>
    /// Handles a step error by resolving and executing error policies.
    /// </summary>
    private async Task<Result<StepOutcome>> HandleStepErrorAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        Error error,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Step {StepName} failed with error {ErrorCode}: {ErrorMessage}. Attempting error policy resolution.",
            step.GetType().Name,
            error.Code,
            error.Message);

        // Build error context for policy resolution
        // Pipeline step errors are handled at State level boundary
        var errorContext = ErrorContextBuilder.Create(_errorNormalizer)
            .WithError(error)
            .WithScope(ErrorBoundaryScope.State)
            .FromContext(context)
            .Build();

        // Record metric
        _workflowMetrics.RecordErrorBoundaryUnhandled(
            context.WorkflowKey,
            errorContext.ExceptionTypeName,
            nameof(ErrorBoundaryScope.State));

        // Resolve error policy (State -> Global hierarchy for pipeline errors)
        var resolvedPolicy = _errorPolicyResolver.Resolve(context, errorContext, onExecuteTask: null);

        if (resolvedPolicy == null)
        {
            _logger.LogWarning(
                "No error policy found for pipeline error in step {StepName}. Propagating error.",
                step.GetType().Name);

            // No policy found - propagate the original error
            return Result<StepOutcome>.Fail(error);
        }

        // Record resolution metric
        _workflowMetrics.RecordErrorBoundaryResolution(
            context.WorkflowKey,
            resolvedPolicy.Level.ToString(),
            resolvedPolicy.Rule.Action.ToString());

        // Execute the error action
        var actionResult = await _errorActionExecutor.ExecuteAsync(
            context,
            errorContext,
            resolvedPolicy,
            cancellationToken);

        // Handle action result
        return await HandleActionResultAsync(step, context, actionResult, error, resolvedPolicy, cancellationToken);
    }

    /// <summary>
    /// Converts an error action result to a step outcome or error.
    /// Handles retry scheduling for pipeline steps.
    /// </summary>
    private async Task<Result<StepOutcome>> HandleActionResultAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        ErrorActionResult actionResult,
        Error originalError,
        ResolvedPolicy resolvedPolicy,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Error action {Action} applied for step {StepName}. Continue={Continue}, Retry={Retry}, Transition={Transition}",
            actionResult.Action,
            step.GetType().Name,
            actionResult.ShouldContinue,
            actionResult.ShouldRetry,
            actionResult.TransitionKey);

        if (actionResult.ShouldContinue)
        {
            // Action allows continuation (Ignore/Log)
            // Clear any existing retry state on success
            await ClearRetryStateAsync(context, cancellationToken);
            
            return Result<StepOutcome>.Ok(new StepOutcome
            {
                ErrorActionResult = actionResult
            });
        }

        if (actionResult.HasTransition)
        {
            // Request error transition via directives
            context.Directives.RequestErrorTransition(actionResult.TransitionKey!);
            
            // Clear retry state when transitioning
            await ClearRetryStateAsync(context, cancellationToken);

            // Return outcome that stops current pipeline
            return Result<StepOutcome>.Ok(new StepOutcome
            {
                StopPipeline = true,
                ErrorActionResult = actionResult
            });
        }

        if (actionResult.ShouldRetry)
        {
            // Schedule a retry job for this step
            return await HandleRetryAsync(
                step, 
                context, 
                actionResult, 
                originalError, 
                resolvedPolicy, 
                cancellationToken);
        }

        // Action indicates stop (Abort/Rollback without transition)
        // Clear retry state on final failure
        await ClearRetryStateAsync(context, cancellationToken);
        
        return Result<StepOutcome>.Fail(actionResult.Error ?? originalError);
    }

    /// <summary>
    /// Handles retry logic by scheduling a background job.
    /// </summary>
    private async Task<Result<StepOutcome>> HandleRetryAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        ErrorActionResult actionResult,
        Error originalError,
        ResolvedPolicy resolvedPolicy,
        CancellationToken cancellationToken)
    {
        // Get or create retry state
        var retryState = GetOrCreateRetryState(context, step, resolvedPolicy, originalError);

        // Check if max retries exceeded
        if (retryState.IsMaxRetriesExceeded)
        {
            _logger.LogWarning(
                "Max retries ({MaxRetries}) exceeded for step {StepName} in instance {InstanceId}. " +
                "Propagating error to upper boundary.",
                retryState.MaxRetries,
                step.GetType().Name,
                context.InstanceId);

            // Clear retry state and propagate error
            await ClearRetryStateAsync(context, cancellationToken);

            // Record metric
            _workflowMetrics.RecordErrorBoundaryResolution(
                context.WorkflowKey,
                "MaxRetryExceeded",
                "Propagate");

            return Result<StepOutcome>.Fail(originalError);
        }

        // Increment attempt and save retry state
        var updatedRetryState = retryState.IncrementAttempt();
        await SaveRetryStateAsync(context, updatedRetryState, cancellationToken);

        // Schedule retry job
        await ScheduleRetryJobAsync(context, step, updatedRetryState, actionResult.RetryDelay, cancellationToken);

        _logger.LogInformation(
            "Retry scheduled for step {StepName} in instance {InstanceId}. " +
            "Attempt {Attempt}/{MaxRetries}, delay: {Delay}ms",
            step.GetType().Name,
            context.InstanceId,
            updatedRetryState.CurrentAttempt,
            updatedRetryState.MaxRetries,
            actionResult.RetryDelay.TotalMilliseconds);

        // Return outcome that stops pipeline gracefully for retry
        return Result<StepOutcome>.Ok(StepOutcome.RetryScheduled());
    }

    /// <summary>
    /// Gets existing retry state or creates a new one.
    /// </summary>
    private StepRetryState GetOrCreateRetryState(
        TransitionExecutionContext context,
        ITransitionStep step,
        ResolvedPolicy resolvedPolicy,
        Error error)
    {
        // Try to get existing retry state
        if (context.Instance.ExtraProperties.TryGetValue(StepRetryState.ExtraPropertiesKey, out var value) &&
            value is Dictionary<string, object?> dict)
        {
            var existing = StepRetryState.FromDictionary(dict);
            if (existing != null && existing.StepOrder == step.Order)
            {
                return existing;
            }
        }

        // Create new retry state
        var maxRetries = resolvedPolicy.Rule.RetryPolicy?.MaxRetries ?? 3;

        return StepRetryState.Create(
            stepOrder: step.Order,
            stepName: step.GetType().Name,
            maxRetries: maxRetries,
            errorCode: error.Code,
            errorMessage: error.Message,
            transitionKey: context.TransitionKey,
            workflowKey: context.WorkflowKey,
            workflowVersion: context.Workflow.Version);
    }

    /// <summary>
    /// Saves retry state to instance extra properties.
    /// </summary>
    private async Task SaveRetryStateAsync(
        TransitionExecutionContext context,
        StepRetryState retryState,
        CancellationToken cancellationToken)
    {
        context.Instance.ExtraProperties[StepRetryState.ExtraPropertiesKey] = retryState.ToDictionary();
        await _instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
    }

    /// <summary>
    /// Clears retry state from instance extra properties.
    /// </summary>
    private async Task ClearRetryStateAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.Instance.ExtraProperties.ContainsKey(StepRetryState.ExtraPropertiesKey))
        {
            context.Instance.ExtraProperties.Remove(StepRetryState.ExtraPropertiesKey);
            await _instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
        }
    }

    /// <summary>
    /// Schedules a retry job for the failed step.
    /// </summary>
    private async Task ScheduleRetryJobAsync(
        TransitionExecutionContext context,
        ITransitionStep step,
        StepRetryState retryState,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var jobName = RetryStepJobPayload.CreateJobName(
            context.InstanceId,
            step.Order,
            retryState.CurrentAttempt);

        var payload = new RetryStepJobPayload
        {
            JobName = jobName,
            Domain = context.Domain,
            InstanceId = context.InstanceId,
            WorkflowKey = context.WorkflowKey,
            Version = context.Workflow.Version,
            TransitionKey = context.TransitionKey,
            StepOrder = step.Order,
            StepName = step.GetType().Name,
            RetryAttempt = retryState.CurrentAttempt,
            MaxRetries = retryState.MaxRetries,
            ErrorCode = retryState.ErrorCode
        };

        // Schedule the job with the specified delay using Dapr job schedule
        var scheduledTime = DateTime.UtcNow.Add(delay);
        var schedule = TimerSchedule.FromDateTime(scheduledTime);
        var scheduleExpression = schedule.ToDaprJobSchedule().ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["instanceId"] = context.InstanceId.ToString(),
            ["workflowKey"] = context.WorkflowKey,
            ["stepName"] = step.GetType().Name,
            ["retryAttempt"] = retryState.CurrentAttempt
        };

        var jobId = await _backgroundJobService.EnqueueAsync(
            RetryStepJobHandler.HandlerName,
            jobName,
            payload,
            scheduleExpression,
            metadata: metadata,
            cancellationToken);

        // Record the job in the repository for tracking
        await _jobRepository.InsertAsync(
            InstanceJob.Create(
                jobId,
                jobName,
                jobId,
                context.Domain,
                context.WorkflowKey,
                context.InstanceId),
            true,
            cancellationToken);

        _logger.LogDebug(
            "Retry job {JobName} scheduled for {ScheduledTime}",
            jobName,
            scheduledTime);
    }
}
