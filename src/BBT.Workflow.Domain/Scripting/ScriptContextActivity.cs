using System.Diagnostics;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Low-cardinality tracing for ScriptContext materialization. The source name is already registered
/// by the hosts for script compile/execute spans. With no Activity listener every method is a cheap
/// null-returning fast path.
/// </summary>
internal static class ScriptContextActivity
{
    private static readonly ActivitySource Source = new("BBT.Workflow.Scripting");

    public static Activity? Start(string operation) =>
        Source.StartActivity(operation, ActivityKind.Internal);

    public static void TagInstanceShape(Activity? activity, Instance? instance)
    {
        if (activity is null || instance is null)
            return;

        activity.SetTag("vnext.script.context.instance_data_rows", instance.DataList.Count);
        // Keep instrumentation O(1): these spans exist specifically to diagnose context overhead,
        // so they must not scan correlation collections on every sampled context operation.
        activity.SetTag("vnext.script.context.correlation_count", instance.ChildCorrelations.Count);
        activity.SetTag("vnext.script.context.incident_count", instance.Incidents.Count);
    }
}
