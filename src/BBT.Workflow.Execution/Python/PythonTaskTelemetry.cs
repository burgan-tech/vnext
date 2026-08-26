using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BBT.Workflow.Execution.Python;

internal static class PythonTaskTelemetry
{
    public const string InstrumentationName = "BBT.Workflow.Execution.Python";
    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    private static readonly Meter Meter = new(InstrumentationName);
    private static readonly Counter<long> Invocations = Meter.CreateCounter<long>(
        "vnext.python.invocations",
        description: "Number of Python task invocations");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "vnext.python.duration",
        unit: "ms",
        description: "Python task execution duration");

    public static void Record(
        string mode,
        string status,
        double durationMs,
        string? runtimeVersion = null)
    {
        var tags = new TagList
        {
            { "task.type", TaskTypes.Python },
            { "python.execution_mode", mode },
            { "status", status },
            { "python.runtime_version", runtimeVersion ?? "unknown" }
        };
        Invocations.Add(1, tags);
        Duration.Record(durationMs, tags);
    }
}
