namespace BBT.Workflow.Definitions.Events;

/// <summary>
/// The two actions an inbound external event can perform on a workflow.
/// </summary>
public enum EventAction
{
    /// <summary>Create a new workflow instance from the event.</summary>
    Start,

    /// <summary>Advance an existing workflow instance via a transition.</summary>
    Transition
}