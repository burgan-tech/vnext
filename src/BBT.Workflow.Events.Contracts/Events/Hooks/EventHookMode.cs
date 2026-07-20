namespace BBT.Workflow.Events.Hooks;

/// <summary>
/// Defines how an event hook participates in distributed event publishing.
/// </summary>
public enum EventHookMode
{
    /// <summary>
    /// Executes hooks before publishing and publishes to the inner bus only when hooks are absent or fail.
    /// </summary>
    HandledOrFallback = 1,

    /// <summary>
    /// Publishes to the inner bus first and executes hooks after the ambient unit of work commits.
    /// </summary>
    DurablePostCommit = 2
}
