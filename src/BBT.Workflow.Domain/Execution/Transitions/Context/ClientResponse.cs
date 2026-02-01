using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution;

/// <summary>
/// Response object for client-facing transition results.
/// Contains instance status and optional error information.
/// </summary>
public class ClientResponse
{
    public Guid Id { get; set; }
    public InstanceStatus Status { get; set; }
    
    /// <summary>
    /// Optional error that occurred during transition execution.
    /// When present, indicates that the transition failed with client-visible error.
    /// </summary>
    public Error? Error { get; set; }
}