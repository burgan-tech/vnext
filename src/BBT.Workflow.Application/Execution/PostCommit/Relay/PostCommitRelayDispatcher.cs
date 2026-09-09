using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit.Relay;

/// <summary>
/// Default <see cref="IPostCommitRelayDispatcher"/>. Resolves the relay for each event's runtime
/// type through DI — the same open-generic shape <c>PostCommitExecutor</c> uses to resolve
/// <c>IPostCommitHandler&lt;TJob&gt;</c> — so a new relayed event needs no edit here.
/// <para>
/// Processes envelopes SEQUENTIALLY. A hop produces at most one terminal event by domain
/// construction (terminal outcomes are exclusive — pinned by SubItemTerminalProbe.Conflict); the
/// loop is defensive, and sequential keeps failure attribution deterministic. Concurrency with the
/// Inbox backup is serialized downstream by the per-subInstance lock in the settlement services and
/// by the same lock plus the monotonic state stamp in <c>SubflowStateService</c>.
/// </para>
/// </summary>
public sealed class PostCommitRelayDispatcher(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<PostCommitRelayDispatcher> logger) : IPostCommitRelayDispatcher
{
    /// <summary>
    /// Cap on nested in-process relay hops. A sub-state relay applied to a parent that is itself a
    /// subflow raises the grandparent's event inside that write, which the receiving service hands
    /// straight back to this dispatcher — the fast path walks the whole ancestor chain. Real chains
    /// are a handful of levels deep and terminate at the root; the cap only stops a pathological or
    /// cyclic graph from turning one hop into an unbounded awaited walk. Past it the event still
    /// travels the outbox, so nothing is lost — only the immediacy.
    /// </summary>
    private const int MaxRelayDepth = 10;

    /// <summary>
    /// In-process nesting depth. Cross-domain legs start a fresh request on the far side and
    /// therefore a fresh count, which is correct: that hop is bounded by the relay's own remote
    /// timeout instead.
    /// </summary>
    private static readonly AsyncLocal<int> RelayDepth = new();

    /// <summary>
    /// Reflection shape per event type. The job-handler equivalent rebuilds this on every dispatch;
    /// this path runs on every hop that produced events, so the <see cref="MethodInfo"/> lookups are
    /// cached. The cache holds no service instances — resolution still happens per call, per scope.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, RelayBinding> Bindings = new();

    /// <inheritdoc />
    public async Task RelayAsync(
        IReadOnlyList<DomainEventEnvelope> deferredEvents,
        CancellationToken cancellationToken)
    {
        if (deferredEvents.Count == 0)
            return;

        var depth = RelayDepth.Value;
        if (depth >= MaxRelayDepth)
        {
            logger.PostCommitEventRelayDepthExceeded(deferredEvents[0].Event.GetType().Name, depth);
            return;
        }

        RelayDepth.Value = depth + 1;
        try
        {
            foreach (var envelope in deferredEvents)
            {
                await RelayOneAsync(envelope, cancellationToken);
            }
        }
        finally
        {
            RelayDepth.Value = depth;
        }
    }

    private async Task RelayOneAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var eventType = envelope.Event.GetType();
        var binding = Bindings.GetOrAdd(eventType, RelayBinding.For);

        // Registration IS the opt-in: no relay for this type means outbox-only delivery.
        var relay = serviceProvider.GetService(binding.RelayInterfaceType);
        if (relay is null)
            return;

        using var activity = PipelineStepActivityHelper.StartOperationActivity("PostCommit.EventRelay");
        activity?.SetTag(TelemetryConstants.TagNames.EventName, eventType.Name);
        activity?.SetTag(TelemetryConstants.TagNames.DeliveryRole, "relay");

        CancellationTokenSource? timeoutSource = null;
        try
        {
            var target = binding.Describe(relay, envelope.Event);
            activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, target.ParentInstanceId);
            activity?.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, target.SubInstanceId);
            if (target.Sync.HasValue)
                activity?.SetTag(TelemetryConstants.TagNames.RelaySync, target.Sync.Value);

            // Same source the gateway routes by — the tag can never disagree with the actual route.
            var isLocal = runtimeInfoProvider.IsDomainMatch(target.Domain);
            activity?.SetTag(TelemetryConstants.TagNames.RelayRoute, isLocal ? "local" : "remote");

            var effectiveToken = cancellationToken;
            if (!isLocal && target.RemoteTimeout is { } remoteTimeout)
            {
                timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(remoteTimeout);
                effectiveToken = timeoutSource.Token;
            }

            var result = await binding.RelayAsync(relay, envelope.Event, effectiveToken);

            if (result.IsSuccess)
            {
                activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, PostCommitRelayOutcomes.Relayed);
                logger.PostCommitEventRelayed(eventType.Name, target.SubInstanceId, target.ParentInstanceId);
            }
            else
            {
                activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, PostCommitRelayOutcomes.Failed);
                logger.PostCommitEventRelayRejected(eventType.Name, result.Error.Message);
            }
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true
                                                 && !cancellationToken.IsCancellationRequested)
        {
            // The remote leg outran its bound. The child's hop must not wait any longer on it; the
            // outbox row is already durable and the Inbox backup applies the same command.
            activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, PostCommitRelayOutcomes.Timeout);
            activity?.SetStatus(ActivityStatusCode.Error, "relay timeout");
            logger.PostCommitEventRelayTimedOut(eventType.Name);
        }
        catch (Exception ex)
        {
            // The originating commit already stands; the outbox row guarantees the Inbox backup
            // delivers. Never fail the hop for a relay error.
            activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, PostCommitRelayOutcomes.Failed);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.PostCommitEventRelayFailed(ex, eventType.Name);
        }
        finally
        {
            timeoutSource?.Dispose();
        }
    }

    /// <summary>
    /// The resolved reflection shape for one event type: which closed generic to ask DI for, and
    /// the two methods to invoke on whatever comes back.
    /// </summary>
    private sealed class RelayBinding
    {
        private readonly MethodInfo _describe;
        private readonly MethodInfo _relay;

        private RelayBinding(Type relayInterfaceType, MethodInfo describe, MethodInfo relay)
        {
            RelayInterfaceType = relayInterfaceType;
            _describe = describe;
            _relay = relay;
        }

        public Type RelayInterfaceType { get; }

        public static RelayBinding For(Type eventType)
        {
            var relayInterfaceType = typeof(IPostCommitEventRelay<>).MakeGenericType(eventType);
            return new RelayBinding(
                relayInterfaceType,
                relayInterfaceType.GetMethod(nameof(IPostCommitEventRelay<object>.Describe))!,
                relayInterfaceType.GetMethod(nameof(IPostCommitEventRelay<object>.RelayAsync))!);
        }

        public PostCommitRelayTarget Describe(object relay, object @event)
            => (PostCommitRelayTarget)_describe.Invoke(relay, [@event])!;

        public Task<Result> RelayAsync(object relay, object @event, CancellationToken cancellationToken)
            => (Task<Result>)_relay.Invoke(relay, [@event, cancellationToken])!;
    }
}
