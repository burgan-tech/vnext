using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Handles asynchronous transition background jobs.
/// This handler processes workflow transition requests that were submitted with Sync=true.
/// </summary>
public sealed class TransitionJobHandler(
    IInstanceJobRepository jobRepository,
    IWorkflowExecutionService workflowExecutionService,
    ICurrentSchema currentSchema,
    IJobTimeoutRecoveryService recoveryService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    IHostApplicationLifetime hostLifetime,
    ILogger<TransitionJobHandler> logger) : IBackgroundJobHandler<TransitionJobPayload>
{
    public const string HandlerName = "flow.transition";

    public async Task HandleAsync(TransitionJobPayload args, CancellationToken cancellationToken)
    {
        // Restore trace context from the original request for distributed tracing correlation
        using var activity = BackgroundJobActivityHelper.StartActivityAsChildWithLink("TransitionJob.Execute", args);
        using (currentSchema.Change(args.Workflow))
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
            {
                var timeoutSeconds = executionOptions.Value.TransitionJobTimeoutSeconds;

                // Separate execution budget CTS from the incoming Dapr/host cancellation token.
                // This lets us distinguish: own timeout vs Dapr HTTP timeout vs host shutdown.
                using var executionCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, executionCts.Token);

                bool needsRecovery = false;

                try
                {
                    // Expose the original raw request body to mappings built inside this job (no live
                    // HttpContext here) so background signature verification (JWS/mTLS) can run.
                    using var rawBodyScope = RawBodyExecutionScope.Set(args.RawBody);

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
                                Tags = args.Tags,
                                Stage = args.Stage
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
                    context.CallerMode = args.CallerSync ? ExecMode.Sync : ExecMode.Async;
                    context.ChainToken = args.ChainToken;

                    // Transition-per-job: this job runs exactly ONE transition; its auto-chain
                    // continuation is enqueued as the next job via ITransitionEnqueueGateway
                    // instead of running in-process.
                    context.EnqueueContinuations = executionOptions.Value.TransitionPerJob;

                    // Use the background-specific method that handles pre-reserved instances
                    var result = await workflowExecutionService.ExecuteTransitionAsync(context, linkedCts.Token);

                    if (!result.IsSuccess)
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
                        logger.JobFailed(args.JobName, args.InstanceId, result.Error.Message ?? "Unknown error");
                    }
                    else
                    {
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        logger.JobCompleted(args.JobName, args.TransitionKey, args.InstanceId);
                    }
                }
                catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
                {
                    // Own 300s execution budget exceeded
                    needsRecovery = true;
                    activity?.SetStatus(ActivityStatusCode.Error, "Job execution timeout");
                    activity?.SetTag("timeout.layer", "job");
                    logger.JobTimedOut(args.JobName, timeoutSeconds, args.TransitionKey, args.InstanceId);
                }
                catch (OperationCanceledException) when (
                    !hostLifetime.ApplicationStopping.IsCancellationRequested)
                {
                    // Dapr HTTP response timeout or external cancellation (not host shutdown)
                    needsRecovery = true;
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled by Dapr/external");
                    activity?.SetTag("timeout.layer", "dapr-cancel");
                    logger.JobCancelledByExternal(args.JobName, args.TransitionKey, args.InstanceId);
                }
                catch (OperationCanceledException)
                {
                    // Host shutdown (SIGTERM) — recovery not feasible, host is going down
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled (host shutdown)");
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
                    if (needsRecovery)
                    {
                        // CancellationToken.None: recovery must complete even if host is shutting down
                        await recoveryService.FaultInstanceAsync(args, CancellationToken.None);
                    }

                    await jobRepository.MarkAsProcessedAsync(args.InstanceId, args.JobName, CancellationToken.None);
                }
            }
        }
    }
}
