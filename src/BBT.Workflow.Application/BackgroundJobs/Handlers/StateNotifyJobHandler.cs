using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.MultiSchema;
using BBT.Aether.Tracing;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Dispatches state-level notifications after the transition pipeline has settled. Scheduled at
/// settle time (state/status finalized + committed) so dispatch happens off the request thread and
/// reads the committed instance. Re-loads the instance, rebuilds a <see cref="ScriptContext"/> from
/// the carried request context (headers/route values/body), then for each <c>state</c> notification
/// entry on the settled state evaluates the optional rule and dispatches the applicable ones through
/// the platform-managed <c>state</c> Dapr binding via <see cref="IStateNotificationDispatcher"/>.
/// No-ops when the state no longer declares any applicable notification.
/// </summary>
public sealed class StateNotifyJobHandler(
    IInstanceRepository instanceRepository,
    IInstanceJobRepository jobRepository,
    IScriptContextFactory scriptContextFactory,
    IStateNotificationDispatcher dispatcher,
    ITaskConditionService conditionService,
    IRuntimeInfoProvider runtimeInfoProvider,
    ICurrentSchema currentSchema,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<StateNotifyJobHandler> logger) : IBackgroundJobHandler<StateNotifyPayload>
{
    public const string HandlerName = "state.notify";

    public async Task HandleAsync(StateNotifyPayload args, CancellationToken cancellationToken)
    {
        // Flat lane, like the transition hops: anchored on the settling request's lane (payload
        // TraceRoot) with the scheduling hop tagged as predecessor. Reset, not Use — the ambient lane
        // here belongs to the Dapr scheduler callback. A payload without an anchor (older build)
        // degrades to exactly the previous continue-the-predecessor parenting.
        using var lane = WorkflowTraceLane.Reset(args.TraceRoot, args.ParentTraceRoot);
        using var activity = BackgroundJobActivityHelper.StartFlatLaneActivity("StateNotify.Execute", args);
        // The Dapr scheduler callback is a fresh HTTP request, so restore the originating request id
        // from the captured headers — RequestIdLogProcessor stamps it onto every log record from here.
        var requestId = args.Headers?.GetValueOrDefault(TelemetryConstants.HeaderNames.RequestId.ToLowerInvariant());
        using var correlationScope = string.IsNullOrEmpty(requestId) ? null : correlationIdProvider.Change(requestId);
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
                    await DispatchApplicableAsync(args, activity, cancellationToken);
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

    private async Task DispatchApplicableAsync(
        StateNotifyPayload args,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        // Rebuild the ScriptContext from the carried request context so rule and mapping scripts can
        // process headers / route values / body exactly as the originating request did.
        await using var scriptContext = await scriptContextFactory
            .NewBuilder(instanceRepository)
            .WithWorkflow(args.Domain, args.FlowName, args.Version)
            .WithInstance(args.InstanceId)
            .WithRuntime(runtimeInfoProvider)
            .WithHeaders(args.Headers)
            .WithRouteValues(args.RouteValues)
            .WithBody(args.Data)
            .BuildAsync(cancellationToken);

        var rootId = scriptContext.Instance?.GetRootInstanceId();
        using var rootScope = (rootId.HasValue && rootId.Value != args.InstanceId)
            ? logger.BeginScope(new Dictionary<string, object>
              { [TelemetryConstants.TagNames.RootInstanceId] = rootId.Value })
            : null;
        if (rootId.HasValue && rootId.Value != args.InstanceId)
        {
            activity?.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootId.Value.ToString());
            activity?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootId.Value.ToString());
        }

        var state = scriptContext.Workflow?.States.SingleOrDefault(s => s.Key == args.StateKey);

        var entries = state?.Notifications
            .Where(n => n.Type == StateNotificationType.State)
            .ToList() ?? [];

        if (entries.Count == 0)
        {
            logger.StateNotificationSkipped(args.InstanceId, args.StateKey, "no state notifications");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        var dispatched = 0;
        foreach (var entry in entries)
        {
            if (!await IsApplicableAsync(entry, scriptContext, cancellationToken))
                continue;

            var result = await dispatcher.DispatchAsync(scriptContext, entry.Mapping, cancellationToken);
            if (result.IsSuccess)
                dispatched++;
        }

        if (dispatched == 0)
            logger.StateNotificationSkipped(args.InstanceId, args.StateKey, "no entry matched its rule");

        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// An entry is applicable when it has no rule, or its rule evaluates to <c>true</c>.
    /// A failed rule evaluation is treated as not-applicable (logged, entry skipped).
    /// </summary>
    private async Task<bool> IsApplicableAsync(
        StateNotification entry,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        if (!entry.HasRule)
            return true;

        var ruleResult = await conditionService.ExecuteConditionAsync(entry.Rule!, scriptContext, cancellationToken);
        return ruleResult is { IsSuccess: true, Value: true };
    }
}
