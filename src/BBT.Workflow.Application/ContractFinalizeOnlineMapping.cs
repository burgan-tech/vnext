using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of notify-render-ready: DirectTrigger the parent contract-flow's render-ready
// transition, using the renderReady ref + contractInstanceId passed at start.
// The task carries an errorBoundary Retry to absorb instance-lock contention / transient
// "transition not available" windows while the parent is mid-transition.
public class OnlineNotifyRenderReadyMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trig = task as DirectTriggerTask;
        if (trig == null) throw new InvalidOperationException("Task must be a DirectTriggerTask");

        var data = context.Instance.Data as IDictionary<string, object>;
        var rr = Ref(data, "renderReady");
        trig.SetDomain(rr.domain ?? "core");
        trig.SetKey(rr.flow ?? "contract-flow");
        trig.SetTransitionName(rr.key ?? "render-ready");
        trig.SetInstance(Str(data, "contractInstanceId"));
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("OnlineNotifyRenderReadyMapping: parent render-ready triggered");
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    private static (string domain, string flow, string key) Ref(IDictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v is IDictionary<string, object> r)
            return (Get(r, "domain"), Get(r, "flow"), Get(r, "key"));
        return (null, null, null);
    }
    private static string Get(IDictionary<string, object> d, string k)
        => (d != null && d.TryGetValue(k, out var v) && v != null) ? v.ToString() : null;
    private static string Str(IDictionary<string, object> d, string k)
        => (d != null && d.TryGetValue(k, out var v) && v != null) ? v.ToString() : null;
}
