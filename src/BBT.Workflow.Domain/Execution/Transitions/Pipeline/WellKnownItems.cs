namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Well-known string keys used to store and retrieve values from TransitionExecutionContext.Items.
/// Centralizes magic strings to prevent typos and enable discoverability.
/// </summary>
public static class WellKnownItems
{
    /// <summary>The ID of the transition record created by CreateTransitionRecordStep.</summary>
    public const string TransitionRecordId = "TransitionRecordId";

    /// <summary>The transition record created or reused by CreateTransitionRecordStep.</summary>
    public const string InstanceTransition = "InstanceTransition";
}
