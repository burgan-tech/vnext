namespace BBT.Workflow.Definitions;

/// <summary>
/// Discriminates the kind of state-level notification entry. Only <see cref="State"/> is processed
/// today; further kinds (e.g. <see cref="Command"/>) are reserved for future expansion and are
/// currently ignored by the engine.
/// </summary>
public enum StateNotificationType
{
    /// <summary>
    /// State change notification dispatched through the platform-managed <c>state</c> Dapr binding.
    /// Default when the <c>type</c> field is omitted.
    /// </summary>
    State = 0,

    /// <summary>
    /// Command notification. Reserved for future use; not processed yet.
    /// </summary>
    Command = 1
}
