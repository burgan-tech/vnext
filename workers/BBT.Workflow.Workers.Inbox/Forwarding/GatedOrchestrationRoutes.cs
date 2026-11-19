using System;
using BBT.Workflow.Execution.RuntimeCaller;

namespace BBT.Workflow.Workers.Inbox.Forwarding;

/// <summary>
/// The Orchestration routes the Inbox forwards to that sit behind the sub-endpoint gate, each built
/// together with the runtime-caller target it is signed for, so the URL sent and the route values the
/// token is bound to can never disagree (vnext#1265).
/// </summary>
internal static class GatedOrchestrationRoutes
{
    /// <summary>The completion backup for parent <paramref name="instanceId"/> of <paramref name="domain"/>/<paramref name="flow"/>.</summary>
    public static (string Route, RuntimeCallTarget Target) Complete(string domain, string flow, Guid instanceId)
        => ($"api/v1/{domain}/workflows/{flow}/instances/{instanceId:D}/complete",
            RuntimeCallTarget.Complete(domain, flow, instanceId));
}
