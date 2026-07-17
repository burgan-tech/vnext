using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Typed input for the internal downward child-subflow cancellation boundary.
/// </summary>
public sealed record ChildSubflowCancelInput(
    string? Version,
    TerminationContext Termination);
