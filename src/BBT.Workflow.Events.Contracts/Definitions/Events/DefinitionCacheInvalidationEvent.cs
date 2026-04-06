using BBT.Aether.Events;

namespace BBT.Workflow.Definitions.Events;

/// <summary>
/// Broadcast event to invalidate definition cache across all pods.
/// Published to vnext-pubsub-broadcast for multi-pod synchronization.
/// </summary>
/// <remarks>
/// This event is broadcast to all pods in the cluster (Orchestration and Worker.Inbox)
/// to ensure cache consistency across all instances. Unlike domain events, this event
/// does NOT use hooks - it's handled directly via Dapr subscriptions.
/// </remarks>
public class DefinitionCacheInvalidationEvent : IDistributedEvent
{
    public const string TopicName = "definition.cache.invalidate";
    /// <summary>
    /// The domain requesting cache invalidation.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The environment requesting cache invalidation (e.g., "Development", "Production").
    /// Used to ensure cache invalidation only affects pods in the same environment.
    /// </summary>
    public required string Environment { get; init; }

    /// <summary>
    /// The source that requested the cache invalidation (e.g., "Manual", "System").
    /// </summary>
    public required string RequestedBy { get; init; }

    /// <summary>
    /// When the cache invalidation was requested.
    /// </summary>
    public required DateTime RequestedAt { get; init; }

    /// <summary>
    /// When <c>true</c>, receiving pods perform a full cache reload (replace all).
    /// When <c>false</c> (default), only records modified since the last initialization are fetched
    /// and merged into the existing cache (incremental update).
    /// </summary>
    public bool FullLoad { get; init; } = false;
}
