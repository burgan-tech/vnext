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

    /// <summary>
    /// The forwarded SubFlow's OWN status, as its relay answered it — set only by
    /// <c>ForwardToSubflowJobHandler</c> on a successful forward. Unlike <see cref="Status"/>, which
    /// the handler swaps for the parent's fresh status once the child completed, this is never
    /// rewritten. A non-terminal value proves the parent's blocking correlation is still open, which
    /// is what lets <c>TransitionRunner</c> skip a settlement that could not change anything.
    /// </summary>
    public InstanceStatus? SubflowStatus { get; set; }
}