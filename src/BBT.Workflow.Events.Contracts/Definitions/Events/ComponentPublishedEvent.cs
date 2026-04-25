using BBT.Aether.Events;

namespace BBT.Workflow.Definitions.Events;

/// <summary>
/// Granular broadcast event signaling that a single component (workflow, task, schema, etc.)
/// has just been published. Published to vnext-pubsub-broadcast so every pod can warm
/// its local snapshot for the affected component without a full <see cref="DefinitionCacheInvalidationEvent"/>
/// reload and without a DB scan.
/// </summary>
/// <remarks>
/// Receiving pods are expected to:
///  1. Validate environment and domain match.
///  2. Resolve the cache set for <see cref="ComponentType"/> (sys-flows, sys-tasks, ...).
///  3. Warm the local snapshot from the distributed cache for the produced cache key
///     (snapshot &lt;- Redis body cache, falling back to a single-version DB load on miss).
/// This event complements the Redis version index: even though "vidx-authoritative latest"
/// already guarantees correctness, this event proactively fills the snapshot so subsequent
/// reads avoid the extra Redis hop.
/// </remarks>
public class ComponentPublishedEvent : IDistributedEvent
{
    public const string TopicName = "definition.component.published";

    /// <summary>
    /// Domain the component belongs to.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Environment the publish happened in (e.g., "Development", "Production").
    /// Receiving pods ignore the event when this does not match their own environment.
    /// </summary>
    public required string Environment { get; init; }

    /// <summary>
    /// System component type discriminator (e.g., "sys-flows", "sys-tasks", "sys-schemas",
    /// "sys-functions", "sys-views", "sys-extensions"). Mirrors RuntimeSysSchemaInfo constants.
    /// </summary>
    public required string ComponentType { get; init; }

    /// <summary>
    /// Logical key of the published component.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Concrete (full) version that was published.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Who/what triggered the publish (CI/CD pipeline id, user, "System", ...).
    /// </summary>
    public required string PublishedBy { get; init; }

    /// <summary>
    /// When the component was published.
    /// </summary>
    public required DateTime PublishedAt { get; init; }
}
