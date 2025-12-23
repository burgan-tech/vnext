using BBT.Aether.BackgroundJob;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Handles retry step jobs scheduled by the error boundary system.
/// Resumes pipeline execution from the failed step after the configured delay.
/// </summary>
public sealed class RetryStepJobHandler(
    IWorkflowExecutionService workflowExecutionService,
    IInstanceRepository instanceRepository,
    IInstanceJobRepository jobRepository,
    ILogger<RetryStepJobHandler> logger) : IBackgroundJobHandler<RetryStepJobPayload>
{
    /// <summary>
    /// The handler name for DI registration.
    /// </summary>
    public const string HandlerName = "flow.step.retry";

    /// <inheritdoc />
    public async Task HandleAsync(RetryStepJobPayload args, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Processing step retry job {JobName} for instance {InstanceId}, step {StepName} (attempt {Attempt}/{MaxRetries})",
            args.JobName,
            args.InstanceId,
            args.StepName,
            args.RetryAttempt,
            args.MaxRetries);

        try
        {
            // Load the instance to verify it still exists and get current state
            var instanceResult = await instanceRepository.GetActiveAsync(args.InstanceId.ToString(), cancellationToken);
            var instance = instanceResult.IsSuccess ? instanceResult.Value : null;
            if (instance == null)
            {
                logger.LogWarning(
                    "Instance {InstanceId} not found for retry job {JobName}. Job will be marked as processed.",
                    args.InstanceId,
                    args.JobName);
                await jobRepository.MarkAsProcessedAsync(args.JobName, cancellationToken);
                return;
            }

            // Verify retry state still exists in instance
            var retryState = GetRetryState(instance);
            if (retryState == null)
            {
                logger.LogWarning(
                    "Retry state not found in instance {InstanceId} for job {JobName}. " +
                    "This may indicate the retry was already processed or cleared.",
                    args.InstanceId,
                    args.JobName);
                await jobRepository.MarkAsProcessedAsync(args.JobName, cancellationToken);
                return;
            }

            // Verify the retry state matches this job
            if (retryState.CurrentAttempt != args.RetryAttempt || retryState.StepOrder != args.StepOrder)
            {
                logger.LogWarning(
                    "Retry state mismatch for instance {InstanceId}. " +
                    "Expected step {ExpectedStep} attempt {ExpectedAttempt}, " +
                    "found step {ActualStep} attempt {ActualAttempt}. Skipping.",
                    args.InstanceId,
                    args.StepOrder,
                    args.RetryAttempt,
                    retryState.StepOrder,
                    retryState.CurrentAttempt);
                await jobRepository.MarkAsProcessedAsync(args.JobName, cancellationToken);
                return;
            }

            // Create execution context for retry
            var executionContext = CreateRetryExecutionContext(args, retryState);

            // Execute the transition (will resume from the specified step)
            await workflowExecutionService.ExecuteTransitionAsync(executionContext, cancellationToken);

            // Mark job as processed
            await jobRepository.MarkAsProcessedAsync(args.JobName, cancellationToken);

            logger.LogInformation(
                "Step retry job {JobName} completed successfully for instance {InstanceId}",
                args.JobName,
                args.InstanceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Step retry job {JobName} was cancelled for instance {InstanceId}",
                args.JobName,
                args.InstanceId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Step retry job {JobName} failed for instance {InstanceId}. " +
                "Error will be handled by error boundary on next attempt.",
                args.JobName,
                args.InstanceId);
            throw;
        }
    }

    /// <summary>
    /// Gets the retry state from the instance's extra properties.
    /// </summary>
    private static StepRetryState? GetRetryState(Instance instance)
    {
        if (!instance.ExtraProperties.TryGetValue(StepRetryState.ExtraPropertiesKey, out var value))
            return null;

        if (value is Dictionary<string, object?> dict)
            return StepRetryState.FromDictionary(dict);

        return null;
    }

    /// <summary>
    /// Creates a WorkflowExecutionContext configured for step retry.
    /// </summary>
    private static WorkflowExecutionContext CreateRetryExecutionContext(
        RetryStepJobPayload args,
        StepRetryState retryState)
    {
        return new WorkflowExecutionContext
        {
            Domain = args.Domain,
            InstanceId = args.InstanceId.ToString(),
            WorkflowKey = args.WorkflowKey,
            WorkflowVersion = args.Version,
            TransitionKey = args.TransitionKey ?? string.Empty,
            TriggerType = TriggerType.Automatic, // Retry is automatic
            Mode = ExecMode.Sync,
            Actor = ExecutionActor.System,
            IsReentry = true,
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Execution = new ExecutionInfo
            {
                ExecutionChainId = Guid.NewGuid().ToString("N"),
                ChainDepth = 0,
                ResumeFrom = args.StepOrder, // Resume from the failed step
                IsSubFlowResume = false,
                IsStepRetry = true,
                RetryAttempt = args.RetryAttempt
            }
        };
    }
}

