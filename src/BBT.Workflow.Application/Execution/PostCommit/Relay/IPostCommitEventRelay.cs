using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit.Relay;

/// <summary>
/// Declares the "Outbox + PostCommitRelay" publish mode for ONE event type: the event still rides
/// the transactional outbox as the durable fact (its Inbox handler becomes the backup), and the
/// runner ADDITIONALLY relays it as an immediate post-commit command so the receiver settles with
/// gap ≈ 0.
/// <para>
/// The DI REGISTRATION is the mode declaration — there is no marker interface on the contract and
/// no central switch to edit. Opting a new event in is one class plus one
/// <c>AddScoped&lt;IPostCommitEventRelay&lt;TEvent&gt;, …&gt;()</c> line; removing that line is the
/// kill switch. Registration alone is not sufficient grounds: per the standing precedent each
/// relayed event also needs a durable backup, an idempotent order-safe receiver guard, measured
/// latency evidence and a council row.
/// </para>
/// </summary>
/// <typeparam name="TEvent">The concrete event type. Resolution is by the event's runtime type, so
/// a registration for a base type is never picked up for a derived one.</typeparam>
public interface IPostCommitEventRelay<in TEvent> where TEvent : class
{
    /// <summary>
    /// Route and tag metadata for <paramref name="event"/>. Called BEFORE
    /// <see cref="RelayAsync"/> so the span carries the identifiers even when the call throws.
    /// Must be a pure function and must NOT throw.
    /// </summary>
    PostCommitRelayTarget Describe(TEvent @event);

    /// <summary>
    /// Sends the event to the receiver as a command. Implementations do not catch: the dispatcher
    /// swallows and logs every failure, because the originating hop's commit already stands and the
    /// outbox row guarantees the backup delivery.
    /// </summary>
    Task<Result> RelayAsync(TEvent @event, CancellationToken cancellationToken);
}
