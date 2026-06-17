using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Fallback handler for declarative long-poll termination. Fires when a client did not acknowledge
/// the long-poll termination signal within the configured window, resuming the paused pipeline so it
/// continues past the entered state. Idempotent: if the instance already resumed (acknowledge won the
/// race), <see cref="ILongPollAckResumeService"/> no-ops.
/// </summary>
public sealed class LongPollAckTimeoutJobHandler(
    IInstanceJobRepository jobRepository,
    ILongPollAckResumeService resumeService,
    ICurrentSchema currentSchema,
    ILogger<LongPollAckTimeoutJobHandler> logger) : IBackgroundJobHandler<LongPollAckTimeoutPayload>
{
    public const string HandlerName = "longpoll.ack.timeout";

    public async Task HandleAsync(LongPollAckTimeoutPayload args, CancellationToken cancellationToken)
    {
        using var activity = BackgroundJobActivityHelper.StartActivityAsChildWithLink("LongPollAckTimeout.Execute", args);
        using (currentSchema.Use(args.FlowName))
        {
            using (logger.BeginScope(new Dictionary<string, object>
                   {
                       [TelemetryConstants.TagNames.InstanceId] = args.InstanceId,
                       [TelemetryConstants.TagNames.Flow] = args.FlowName,
                       [TelemetryConstants.TagNames.Domain] = args.Domain,
                       [TelemetryConstants.TagNames.FlowVersion] = args.Version,
                       [TelemetryConstants.TagNames.JobName] = args.JobName
                   }))
            {
                try
                {
                    BackgroundJobActivityHelper.EnrichActivity(activity, args);

                    var result = await resumeService.ResumeAsync(
                        args.Domain, args.FlowName, args.Version, args.InstanceId, cancellationToken);

                    activity?.SetStatus(
                        result.IsSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                        result.IsSuccess ? null : result.Error.Message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled");
                    logger.JobCancelled(args.JobName, "longpoll-ack", args.InstanceId);
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
}
