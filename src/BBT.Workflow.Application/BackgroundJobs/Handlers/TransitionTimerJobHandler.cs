using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Handles transition timer background jobs that are triggered after a configured time delay.
/// This handler is responsible for processing scheduled transition events for workflow instances.
/// </summary>
public sealed class TransitionTimerJobHandler(
    IWorkflowExecutionService workflowExecutionService,
    IInstanceJobRepository jobRepository,
    ILogger<TransitionTimerJobHandler> logger) : IBackgroundJobHandler<TransitionTimerPayload>
{
    public const string HandlerName = "flow.transition.schedule";

    public async Task HandleAsync(TransitionTimerPayload args, CancellationToken cancellationToken)
    {
        // Restore trace context from the original request for distributed tracing correlation
        using var activity = BackgroundJobActivityHelper.StartActivityWithTraceContext("TransitionTimerJob.Execute", args);

        try
        {
            BackgroundJobActivityHelper.EnrichActivity(activity, args);
            BackgroundJobActivityHelper.EnrichActivityWithTransition(activity, args.TransitionKey);

            var input = new TransitionInput(args.Domain, args.FlowName);

            // Convert TransitionInput to WorkflowExecutionContext
            var executionContext = input.ToExecutionContext(
                args.InstanceId.ToString(),
                args.Version,
                args.TransitionKey);

            // Override trigger type to Scheduled for timer-based transitions
            executionContext.TriggerType = TriggerType.Scheduled;
            executionContext.Actor = ExecutionActor.System;
            executionContext.IsReentry = true; // Timer transitions are re-entry executions

            await workflowExecutionService.ExecuteTransitionAsync(
                executionContext,
                cancellationToken
            );

            await jobRepository.MarkAsProcessedAsync(args.JobName, cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
            logger.JobCompleted(args.JobName, args.TransitionKey, args.InstanceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled");
            logger.JobCancelled(args.JobName, args.TransitionKey, args.InstanceId);
            throw; // Re-throw cancellation exceptions
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            activity?.AddTag("error.type", e.GetType().Name);
            logger.JobFailed(e, args.JobName, args.InstanceId);
            throw;
        }
    }
}