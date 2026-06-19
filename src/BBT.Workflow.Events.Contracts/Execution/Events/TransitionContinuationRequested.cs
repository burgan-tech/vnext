using System.Text.Json;
using BBT.Aether.Events;

namespace BBT.Workflow.Execution.Events;

/// <summary>
/// Event requesting that a (chained) transition be enqueued as a background job.
/// Published through the transactional outbox in the SAME unit of work that commits the
/// durable job intent, so "transition committed" and "next transition enqueued" are atomic
/// (closing the dual-write gap of a pre-commit Dapr enqueue). The Inbox handler performs the
/// actual Dapr enqueue (at-least-once); idempotency is enforced downstream via the active
/// <c>InstanceJob</c> guard and (later) the chain token.
/// </summary>
/// <remarks>
/// Intentionally has NO <c>[EventHook]</c>: events without a registered hook are always
/// published to the inner bus / outbox (see <c>HookedDistributedEventBus</c>), which is
/// exactly the desired distributed delivery for a continuation.
/// </remarks>
[EventName("transition.continuation.requested")]
public sealed class TransitionContinuationRequested : IDistributedEvent
{
    /// <summary>The instance the transition belongs to.</summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }

    /// <summary>The workflow domain.</summary>
    public required string Domain { get; init; }

    /// <summary>The workflow (flow) key.</summary>
    public required string Flow { get; init; }

    /// <summary>The workflow version.</summary>
    public required string Version { get; init; }

    /// <summary>The transition key to execute.</summary>
    public required string TransitionKey { get; init; }

    /// <summary>The deterministic job name (used for the active-job idempotency guard).</summary>
    public required string JobName { get; init; }

    /// <summary>
    /// The caller-generated job id. Threaded so the downstream enqueue uses the SAME id for
    /// <c>BackgroundJobInfo.Id</c> as the upstream <c>InstanceJob.JobId</c> — keeping the two in sync
    /// (no placeholder) so cancellation-by-id works across the outbox path.
    /// </summary>
    public required Guid JobId { get; init; }

    /// <summary>The transition payload data (JSON), if any.</summary>
    public JsonElement? Data { get; init; }

    /// <summary>Optional instance key.</summary>
    public string? InstanceKey { get; init; }

    /// <summary>Optional instance tags.</summary>
    public string[]? Tags { get; init; }

    /// <summary>Optional instance stage.</summary>
    public string? Stage { get; init; }

    /// <summary>Request headers to forward to the job.</summary>
    public Dictionary<string, string?> Headers { get; init; } = new();

    /// <summary>Route values to forward to the job.</summary>
    public Dictionary<string, string?> RouteValues { get; init; } = new();

    /// <summary>The execution actor, serialized as its enum name (mapped back by the handler).</summary>
    public required string ExecutionActor { get; init; }

    /// <summary>W3C traceparent for cross-service correlation, if available.</summary>
    public string? TraceParent { get; init; }

    /// <summary>W3C tracestate, if available.</summary>
    public string? TraceState { get; init; }

    /// <summary>
    /// The chain ownership token (see ChainToken gate spec). Carried so the resumed job is
    /// recognized as the chain's own continuation and not rejected as a foreign transition.
    /// </summary>
    public Guid? ChainToken { get; init; }

    /// <summary>The chain depth of the continuation (for the chain-depth guard).</summary>
    public int ChainDepth { get; init; }

    public override string ToString() =>
        $"{nameof(TransitionContinuationRequested)}: InstanceId={InstanceId} Domain={Domain} Flow={Flow} Version={Version} TransitionKey={TransitionKey} JobName={JobName} ChainDepth={ChainDepth}";
}
