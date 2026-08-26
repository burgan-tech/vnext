namespace BBT.Workflow.Definitions;

/// <summary>
/// Public execution-mode names accepted by Python task definitions.
/// </summary>
public static class PythonExecutionModes
{
    public const string PythonNet = "pythonNet";
    public const string Process = "process";
    public const string Container = "container";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [PythonNet, Process, Container],
        StringComparer.OrdinalIgnoreCase);
}
