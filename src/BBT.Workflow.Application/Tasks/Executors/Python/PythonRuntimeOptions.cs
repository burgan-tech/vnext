using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Where the Orchestration host finds the domain's Python runtime container. Bound from
/// configuration section "Workflow:PythonRuntime". The Helm chart sets <see cref="AppId"/> when the
/// domain enables the runtime; without it every Python task fails with a configuration error and
/// nothing else in the host depends on Python.
/// </summary>
public sealed class PythonRuntimeOptions
{
    public const string SectionName = "Workflow:PythonRuntime";

    /// <summary>
    /// Dapr app-id of the Python runtime deployment. Empty means the domain has no Python runtime.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// Invoke route of the runtime contract.
    /// </summary>
    [Required]
    public string InvokeMethod { get; set; } = "v1/invoke";

    /// <summary>
    /// Extra seconds the Orchestration-side call waits beyond the handler timeout, so the runtime's
    /// own timeout answer (504) arrives before the caller gives up.
    /// </summary>
    [Range(1, 30)]
    public int TimeoutGraceSeconds { get; set; } = 5;
}
