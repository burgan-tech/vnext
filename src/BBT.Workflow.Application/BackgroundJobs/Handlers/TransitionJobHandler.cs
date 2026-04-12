using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Handles asynchronous transition background jobs.
/// This handler processes workflow transition requests that were submitted with Sync=true.
/// </summary>
public sealed class TransitionJobHandler(
    IInstanceJobRepository jobRepository,
    IWorkflowExecutionService workflowExecutionService,
    ICurrentSchema currentSchema,
    ILogger<TransitionJobHandler> logger) : IBackgroundJobHandler<TransitionJobPayload>
{
    public const string HandlerName = "flow.transition";

    public async Task HandleAsync(TransitionJobPayload args, CancellationToken cancellationToken)
    {
        // Restore trace context from the original request for distributed tracing correlation
        using var activity = BackgroundJobActivityHelper.StartActivityAsChildWithLink("TransitionJob.Execute", args);
        using (currentSchema.Use(args.Workflow))
        {
            using (logger.BeginScope(new Dictionary<string, object>
                   {
                       [TelemetryConstants.TagNames.InstanceId] = args.InstanceId,
                       [TelemetryConstants.TagNames.Flow] = args.Workflow,
                       [TelemetryConstants.TagNames.Domain] = args.Domain,
                       [TelemetryConstants.TagNames.FlowVersion] = args.Version,
                       [TelemetryConstants.TagNames.InstanceKey] = args.InstanceKey ?? "N/A",
                       [TelemetryConstants.TagNames.TransitionKey] = args.TransitionKey,
                       [TelemetryConstants.TagNames.JobName] = args.JobName
                   }))
                try
                {
                    BackgroundJobActivityHelper.EnrichActivity(activity, args);
                    BackgroundJobActivityHelper.EnrichActivityWithTransition(activity, args.TransitionKey);

                    // For async processing, instance should already be pre-reserved and in Busy status
                    // Reconstruct the original TransitionInput with Sync=true
                    var transitionInput = new TransitionInput(
                            args.Domain,
                            args.Workflow,
                            new TransitionDataInput(args.Data)
                            {
                                Key = args.InstanceKey,
                                Tags = args.Tags
                            },
                            sync: true) // Force sync=true to avoid infinite loop
                        {
                            Headers = args.Headers,
                            RouteValues = args.RouteValues
                        };

                    var context =
                        transitionInput.ToExecutionContext(args.InstanceId.ToString(), args.Version,
                            args.TransitionKey);
                    context.Actor = args.ExecutionActor;

                    // Use the background-specific method that handles pre-reserved instances
                    var result = await workflowExecutionService.ExecuteTransitionAsync(context, cancellationToken);

                    activity?.SetStatus(ActivityStatusCode.Ok);
                    logger.JobCompleted(args.JobName, args.TransitionKey, args.InstanceId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled");
                    logger.JobCancelled(args.JobName, args.TransitionKey, args.InstanceId);
                }
                catch (Exception e)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                    activity?.AddTag("error.type", e.GetType().Name);
                    logger.JobFailed(e, args.JobName, args.InstanceId);
                }
                finally
                {
                    await jobRepository.MarkAsProcessedAsync(args.InstanceId, args.JobName, CancellationToken.None);
                }
        }
    }
}