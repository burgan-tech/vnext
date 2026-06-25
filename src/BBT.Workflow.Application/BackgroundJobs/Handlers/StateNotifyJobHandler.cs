using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Dispatches a state-level notification after the transition pipeline has settled. Scheduled at
/// settle time (state/status finalized + committed) so dispatch happens off the request thread and
/// reads the committed instance. Re-loads the instance, resolves the settled state's
/// <c>notification</c> mapping from the workflow definition and invokes the platform-managed
/// <c>state</c> channel via <see cref="IStateNotificationDispatcher"/>. No-ops when the state no
/// longer declares a notification mapping.
/// </summary>
public sealed class StateNotifyJobHandler(
    IInstanceRepository instanceRepository,
    IInstanceJobRepository jobRepository,
    IScriptContextFactory scriptContextFactory,
    IStateNotificationDispatcher dispatcher,
    IRuntimeInfoProvider runtimeInfoProvider,
    ICurrentSchema currentSchema,
    ILogger<StateNotifyJobHandler> logger) : IBackgroundJobHandler<StateNotifyPayload>
{
    public const string HandlerName = "state.notify";

    public async Task HandleAsync(StateNotifyPayload args, CancellationToken cancellationToken)
    {
        using var activity = BackgroundJobActivityHelper.StartActivityAsChildWithLink("StateNotify.Execute", args);
        using (currentSchema.Change(args.FlowName))
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

                    await using var scriptContext = await scriptContextFactory
                        .NewBuilder(instanceRepository)
                        .WithWorkflow(args.Domain, args.FlowName, args.Version)
                        .WithInstance(args.InstanceId)
                        .WithRuntime(runtimeInfoProvider)
                        .BuildAsync(cancellationToken);

                    var state = scriptContext.Workflow?.States
                        .SingleOrDefault(s => s.Key == args.StateKey);

                    if (state?.Notification is not { HasMapping: true } notification)
                    {
                        logger.StateNotificationSkippedNoMapping(args.InstanceId, args.StateKey);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        return;
                    }

                    var result = await dispatcher.DispatchAsync(
                        scriptContext, notification.Mapping, cancellationToken);

                    activity?.SetStatus(
                        result.IsSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                        result.IsSuccess ? null : result.Error.Message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled");
                    logger.JobCancelled(args.JobName, "state-notify", args.InstanceId);
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
