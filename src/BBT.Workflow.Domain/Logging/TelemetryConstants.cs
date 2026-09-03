namespace BBT.Workflow.Logging;

/// <summary>
/// Constants for OpenTelemetry span attributes used throughout the vNext workflow system.
/// Centralizes all telemetry-related string constants for maintainability.
/// </summary>
public static class TelemetryConstants
{
    /// <summary>
    /// Tag names for OpenTelemetry span attributes.
    /// </summary>
    public static class TagNames
    {
        public const string Domain = "vnext.domain";
        public const string Flow = "vnext.flow.key";
        public const string FlowVersion = "vnext.flow.version";
        public const string InstanceId = "vnext.instance.id";
        /// <summary>
        /// Vendor-neutral workflow instance identifier used to correlate telemetry
        /// emitted by dependencies that are not part of vNext.
        /// </summary>
        public const string WorkflowInstanceId = "workflow.instance.id";
        /// <summary>
        /// Business-operation correlation identifier. This is intentionally separate
        /// from the per-request X-Request-Id and the W3C trace identifier.
        /// </summary>
        public const string CorrelationId = "correlation.id";
        /// <summary>
        /// Primary subject exposed by the gateway authentication chain.
        /// It is propagated as correlation metadata only.
        /// </summary>
        public const string Sub = "sub";
        /// <summary>
        /// Actor subject exposed by the gateway authentication chain.
        /// </summary>
        public const string ActSub = "act.sub";
        public const string InstanceKey = "vnext.instance.key";
        public const string TransitionKey = "vnext.transition.key";
        public const string TriggerType = "vnext.trigger.type";
        public const string HandlerName = "vnext.handler.name";
        public const string TaskKey = "vnext.task.key";
        public const string TaskType = "vnext.task.type";

        /// <summary>Task trigger origin (OnExecute/OnEntry/OnExit/Extension/…): <c>vnext.task.trigger</c>.</summary>
        public const string TaskTrigger = "vnext.task.trigger";

        /// <summary>Stable identity of one fan-out item within its batch.</summary>
        public const string FanOutItemKey = "vnext.fanout.item.key";

        /// <summary>Zero-based position of a fan-out item in its batch.</summary>
        public const string FanOutItemIndex = "vnext.fanout.item.index";

        /// <summary>
        /// The batch's readability label for one item (<c>FanOutTask.ItemAlias</c>), or a neutral
        /// substitute when the task declares none. Always present, so a trace query can group on it
        /// without having to handle a missing attribute.
        /// </summary>
        public const string FanOutItemAlias = "vnext.fanout.item.alias";

        /// <summary>
        /// Milliseconds a fan-out item spent queueing for its concurrency slots before execution
        /// began. Separates "the batch is slow because it is throttled" from "the batch is slow
        /// because one item is slow" without correlating two spans.
        /// </summary>
        public const string FanOutItemQueueWaitMs = "vnext.fanout.item.queue_wait_ms";
        public const string Layer = "vnext.layer";
        public const string SpanCategory = "vnext.span.category";

        /// <summary>Caller-role provider that answered: <c>default</c> or <c>morph-idm</c>.</summary>
        public const string AuthProvider = "vnext.auth.provider";
        /// <summary>
        /// True when the request-scope memo answered and no provider call was made. Counting spans
        /// with this false against the provider's HTTP client spans is how the one-call-per-request
        /// guarantee is verified in production.
        /// </summary>
        public const string AuthMemoHit = "vnext.auth.memo.hit";
        /// <summary>How many roles the caller holds. Zero denies every allowlist grant downstream.</summary>
        public const string AuthRoleCount = "vnext.auth.roles.count";
        /// <summary><c>resolved</c> | <c>empty</c> | <c>failed</c>.</summary>
        public const string AuthOutcome = "vnext.auth.outcome";
        /// <summary>
        /// The caller's organizational posting. Part of the identity the provider keys its answer on
        /// (with sub and act_sub), so a wrong or missing role set is unexplainable without it.
        /// </summary>
        public const string AuthPosition = "vnext.auth.position";
        /// <summary>Provider HTTP status when the call failed with a response rather than an exception.</summary>
        public const string AuthProviderStatusCode = "vnext.auth.provider.status_code";

        /// <summary>
        /// 1-based level of a built-in function's descent into an active subflow. Depth 1 is the
        /// first child; the caller's own level has no descent span and is therefore not numbered.
        /// </summary>
        public const string SubflowDepth = "vnext.subflow.depth";
        /// <summary>
        /// <c>local</c> (in-process re-entry) or <c>remote</c> (HTTP to another domain). The two
        /// transports have very different costs and, before this span existed, only the remote one
        /// was visible at all.
        /// </summary>
        public const string DescentTransport = "vnext.descent.transport";
        /// <summary>Which built-in function descended: <c>state</c>, <c>data</c>, <c>schema</c>, <c>master</c>, <c>view</c>, <c>extensions</c>, <c>authorize</c>.</summary>
        public const string DescentFunction = "vnext.descent.function";
        /// <summary>
        /// Set only when a descent did NOT yield a usable answer, naming why. Absent on the normal
        /// path — a fallback that leaves no mark is indistinguishable from a successful descent.
        /// </summary>
        public const string DescentOutcome = "vnext.descent.outcome";
        public const string StateFrom = "vnext.state.from";
        public const string StateTo = "vnext.state.to";
        public const string JobName = "vnext.job.name";
        /// <summary>
        /// Parent instance ID for subflow/subprocess correlation in traces and logs.
        /// </summary>
        public const string ParentInstanceId = "vnext.parent.instance.id";
        /// <summary>
        /// Subflow instance ID for subflow/subprocess correlation in traces and logs.
        /// </summary>
        public const string SubflowInstanceId = "vnext.subflow.instance.id";
        /// <summary>
        /// Root (ancestor) instance ID — the top-level flow in a nested subflow chain (A→B→C→D always carries A's ID).
        /// </summary>
        public const string RootInstanceId = "vnext.root.instance.id";
        /// <summary>
        /// Originating request id (X-Request-Id value) — joins spans/logs across the async
        /// job, Execution and worker hops back to the client request that started them.
        /// <para>
        /// The value is the normalized form of the <c>X-Request-Id</c> header (lowercase, '-'
        /// replaced by '_') so log backends surface it under exactly the name the platform uses
        /// for the header. It carries no dot on purpose: backends that flatten dotted keys
        /// (OpenObserve, Elasticsearch) leave this one untouched, so the queried field is
        /// <c>x_request_id</c> everywhere.
        /// </para>
        /// <para>
        /// Because this key is identical to what Aether's header enricher would produce for
        /// <c>X-Request-Id</c>, that header must NEVER be listed in
        /// <c>Telemetry:Logging:Enrichers:Headers</c> — the enricher writes first and reports a
        /// value fabricated from <c>HttpContext.TraceIdentifier</c> on platform-originated
        /// requests (Dapr job callbacks, pub/sub deliveries), which would then suppress the
        /// correct value stamped from the correlation provider.
        /// </para>
        /// </summary>
        public const string RequestId = "x_request_id";
        public const string SubItemType = "vnext.subitem.type";
        public const string SubItemOutcome = "vnext.subitem.outcome";
        public const string TerminationOrigin = "vnext.termination.origin";
        public const string TerminationInitiator = "vnext.termination.initiator";
        public const string TerminationCascadeId = "vnext.termination.cascade_id";

        /// <summary>
        /// True when the span was parented to its trace lane anchor (see
        /// <c>WorkflowTraceLane</c>), false when it fell back to the ambient/predecessor parent.
        /// Lets a query separate flat-laned traces from legacy ones during a rolling deploy.
        /// </summary>
        public const string TraceLane = "vnext.trace.lane";

        /// <summary>The lane anchor's span id — groups a trace's lanes when it has more than one.</summary>
        public const string TraceLaneAnchor = "vnext.trace.lane.anchor";

        /// <summary>
        /// Set when an anchor was rejected for belonging to a different trace. The span keeps its
        /// predecessor/ambient parent, so a stale or forged anchor cannot teleport it into a
        /// foreign trace.
        /// </summary>
        public const string TraceLaneMismatch = "vnext.trace.lane.mismatch";

        /// <summary>
        /// Span id of the immediate logical predecessor (hop N for hop N+1). Primary causality tag:
        /// the chain can be reconstructed by self-join even in a UI that hides ActivityLinks.
        /// </summary>
        public const string HopPredecessor = "vnext.hop.predecessor";

        /// <summary>
        /// Monotonic ordinal of a hop within its lane. Needed because <c>ChainDepth</c> resets to 0
        /// at subflow resume, long-poll resume, timeout and retry boundaries, so it cannot order a
        /// lane on its own.
        /// </summary>
        public const string LaneSeq = "vnext.lane.seq";

        /// <summary>Chain depth of the transition hop, promoted onto the lane span.</summary>
        public const string ChainDepth = "vnext.chain.depth";

        /// <summary>
        /// Set when the ambient Dapr scheduler-callback span belongs to a different trace from the
        /// business span. The callback is correlated by id tags rather than an ActivityLink so
        /// Elastic does not splice its transport trace into the business waterfall.
        /// </summary>
        public const string DaprCallback = "vnext.dapr.callback";

        /// <summary>Trace id of an originating context retained for searchable correlation.</summary>
        public const string OriginTraceId = "vnext.origin.trace_id";

        /// <summary>Span id of an originating context retained for searchable correlation.</summary>
        public const string OriginSpanId = "vnext.origin.span_id";

        /// <summary>Trace id of the ambient Dapr callback/delivery transport context.</summary>
        public const string DaprCallbackTraceId = "vnext.dapr.callback.trace_id";

        /// <summary>Span id of the ambient Dapr callback/delivery transport context.</summary>
        public const string DaprCallbackSpanId = "vnext.dapr.callback.span_id";

        /// <summary>
        /// Number of script compilations (hits + misses) that ran while this span was current.
        /// Accumulated onto the nearest task span (the span carrying <see cref="TaskKey"/>) so the
        /// compiler cost of a task is queryable without a dedicated compile span.
        /// </summary>
        public const string ScriptCompileCount = "vnext.script.compile.count";

        /// <summary>Number of those compilations that were cache misses (actually compiled).</summary>
        public const string ScriptCompileMissCount = "vnext.script.compile.miss.count";

        /// <summary>Total wall-clock milliseconds spent inside script compilation calls.</summary>
        public const string ScriptCompileTotalMs = "vnext.script.compile.total_ms";

        /// <summary>Continuation mode realized after a hop: Inline (in-process chain) or Enqueue (job).</summary>
        public const string ContinuationMode = "vnext.continuation.mode";

        /// <summary>True when the continuation produced another in-process hop.</summary>
        public const string ContinuationHasNext = "vnext.continuation.has_next";

        /// <summary>Resting status a transition settled the instance into.</summary>
        public const string SettledStatus = "vnext.settle.status";

        /// <summary>
        /// What the Busy→Active compare-and-set at settlement actually did: <c>flipped</c> (this
        /// hop made the instance Active), <c>lost</c> (the row was no longer Busy — somebody else
        /// settled it), or <c>skipped</c> (the settlement guard did not apply: non-owner, terminal,
        /// Busy-subtype target, open SubFlow correlation). <c>vnext.settle.status</c> alone cannot
        /// tell these apart.
        /// </summary>
        public const string SettleCas = "vnext.settle.cas";

        /// <summary>
        /// How the activation episode ended — see <see cref="ActivationOutcomes"/>. Stamped on
        /// <c>Instance.Activation/{key}</c>.
        /// </summary>
        public const string ActivationOutcome = "vnext.activation.outcome";

        /// <summary>What started the activation episode — see <see cref="ActivationTriggers"/>.</summary>
        public const string ActivationTrigger = "vnext.activation.trigger";

        /// <summary>Number of lane hops the episode spanned (the settling hop's <c>vnext.lane.seq</c>).</summary>
        public const string ActivationHops = "vnext.activation.hops";

        /// <summary>Trigger → rest-point duration in milliseconds, as the client experienced it.</summary>
        public const string ActivationDurationMs = "vnext.activation.duration_ms";

        /// <summary>
        /// True when the episode start was not carried to the settling hop (payload from an older
        /// build, or an entry point that seeded no episode) and the span therefore covers only the
        /// settling hop. Exclude from latency aggregates.
        /// </summary>
        public const string ActivationPartial = "vnext.activation.partial";

        /// <summary>
        /// True when the carried episode start lay in the future of the settling replica's clock;
        /// the span was clamped to zero length rather than reported negative.
        /// </summary>
        public const string ActivationClockSkew = "vnext.activation.clock_skew";

        /// <summary>The transition the episode was triggered with (the first hop's key).</summary>
        public const string ActivationTransitionKey = "vnext.activation.transition.key";

        /// <summary>
        /// On <c>Transition.Settle</c>: true when this settlement closed the activation episode and
        /// an <c>Instance.Activation</c> span will be emitted after commit.
        /// </summary>
        public const string ActivationEmitted = "vnext.activation.emitted";

        /// <summary>On <c>Instance.Create</c>: whether the start request carried attributes, i.e. an initial data version was appended.</summary>
        public const string InstanceDataAppended = "vnext.instance.data.appended";

        /// <summary>On <c>Transition.Intake</c>: the Busy flag the fast-fail projection read.</summary>
        public const string InstanceBusy = "vnext.instance.busy";

        /// <summary>On <c>Transition.Enqueue</c>: which delivery path the enqueue gateway took (<c>Direct</c> or <c>Outbox</c>).</summary>
        public const string EnqueuePath = "vnext.enqueue.path";

        /// <summary>Number of items in a fan-out batch.</summary>
        public const string FanOutItemCount = "vnext.fanout.item.count";

        /// <summary>Number of fan-out items that succeeded.</summary>
        public const string FanOutSucceededCount = "vnext.fanout.succeeded.count";

        /// <summary>Number of fan-out items that failed.</summary>
        public const string FanOutFailedCount = "vnext.fanout.failed.count";

        /// <summary>True when the batch hit its deadline before every item settled.</summary>
        public const string FanOutTimedOut = "vnext.fanout.timed_out";

        /// <summary>Target domain of a trigger-family task's local (in-process) invocation.</summary>
        public const string TriggerTargetDomain = "vnext.trigger.target.domain";

        /// <summary>Target flow of a trigger-family task's local (in-process) invocation.</summary>
        public const string TriggerTargetFlow = "vnext.trigger.target.flow";

        /// <summary>Target instance of a trigger-family task's local (in-process) invocation.</summary>
        public const string TriggerTargetInstance = "vnext.trigger.target.instance";

        /// <summary>Lifecycle order of a pipeline step span (see LifecycleOrder).</summary>
        public const string StepOrder = "vnext.step.order";

        /// <summary>Flow-control outcome of a pipeline step: continue | stop | skipTo:{order}.</summary>
        public const string StepOutcome = "vnext.step.outcome";

        /// <summary>Distributed status-lock key (vnext:{domain}:{flow}:{id}).</summary>
        public const string LockKey = "vnext.lock.key";

        /// <summary>Whether the single-attempt status-lock acquire succeeded.</summary>
        public const string LockAcquired = "vnext.lock.acquired";

        /// <summary>Lease seconds requested for the status lock.</summary>
        public const string LockLeaseSeconds = "vnext.lock.lease_seconds";

        /// <summary>
        /// Which lock funnel a <c>Lock.Acquire</c>/<c>Lock.Release</c> span belongs to: <c>status</c>
        /// (the short-lease status check-and-set, <c>InstanceStatusLock</c>) or <c>chain</c> (the
        /// auto-chain-budget lock, <c>TransitionLockScopeFactory</c>). Without this tag the two
        /// funnels' spans are indistinguishable by name alone.
        /// </summary>
        public const string LockKind = "vnext.lock.kind";

        /// <summary>What a script span was executing: lockKey | subflowInputMapping | subflowOutputMapping | compilation.</summary>
        public const string ScriptKind = "vnext.script.kind";

        /// <summary>True when the compile was served from the type cache (no Roslyn work).</summary>
        public const string ScriptCacheHit = "vnext.script.cache.hit";

        /// <summary>Number of helper components resolved into a compile's helper set.</summary>
        public const string ScriptHelperCount = "vnext.script.helper.count";

        /// <summary>
        /// Short identity of the compiled script: the evaluator cache key when the caller
        /// precomputed one, else a SHA-256 prefix of the source. Tagged on <c>Script.Compile</c>
        /// only when compilation actually ran (cache miss) — a cache hit never computes or sets
        /// this tag, keeping the hot path allocation-free.
        /// </summary>
        public const string ScriptKey = "vnext.script.key";

        /// <summary>
        /// How many times a transition reused its already-built <c>ScriptContext</c> instead of building
        /// one. Set on the enclosing span. A miss produces the <c>ScriptContext.Build</c> span tree; a hit
        /// produced nothing at all before this counter, so the tree could not distinguish "reused" from
        /// "never needed".
        /// </summary>
        public const string ScriptContextMemoHits = "vnext.script.context.memo.hits";

        /// <summary>
        /// How many times a task execution reused an already-compiled mapping factory. Set on the
        /// enclosing span. On a hit the script engine is never called, so no <c>Script.Compile</c> span
        /// exists — this counter is the only evidence the compile was avoided.
        /// </summary>
        public const string MappingFactoryMemoHits = "vnext.script.mapping.memo.hits";

        /// <summary>SemVer version of the instance-data row being appended.</summary>
        public const string DataVersion = "vnext.data.version";

        /// <summary>Serialized byte size of the instance-data payload being appended.</summary>
        public const string DataSizeBytes = "vnext.data.size_bytes";

        /// <summary>Short CLR name of the event being relayed (e.g. <c>InstanceSubFaultedEvent</c>).</summary>
        public const string EventName = "vnext.event.name";

        /// <summary>Outcome of a subflow-terminal relay attempt: <c>relayed</c> | <c>failed</c> | <c>skipped</c>.</summary>
        public const string RelayOutcome = "vnext.relay.outcome";

        /// <summary>
        /// Routing lane a subflow-terminal relay took: <c>local</c> (same-domain, in-process) or
        /// <c>remote</c> (cross-domain, one Dapr service invocation). Sourced from the same
        /// <c>IRuntimeInfoProvider.IsDomainMatch</c> check the gateway routes by, so the tag can
        /// never disagree with the actual route.
        /// </summary>
        public const string RelayRoute = "vnext.relay.route";

        /// <summary>True when the relayed terminal event's originating chain executed synchronously end-to-end.</summary>
        public const string RelaySync = "vnext.relay.sync";

        /// <summary>Which delivery path produced a subflow settlement: relay (immediate) or inbox (durable backup).</summary>
        public const string DeliveryRole = "vnext.delivery.role";

        /// <summary>Domain whose endpoint is being resolved from service discovery. Set on every Discovery.Resolve span.</summary>
        public const string DiscoveryDomain = "vnext.discovery.domain";

        /// <summary>Endpoint kind requested from service discovery (Url or AppId). Set on every Discovery.Resolve span.</summary>
        public const string DiscoveryEndpointKind = "vnext.discovery.endpoint_kind";

        /// <summary>Execution chain id correlating hops within one auto-chain/subflow run.</summary>
        public const string ChainId = "vnext.chain.id";

        /// <summary>Name of the pipeline execution profile resolved for the transition (e.g. Manual, AutoChain).</summary>
        public const string PipelineProfile = "vnext.pipeline.profile";

        /// <summary>Causation id linking a hop to the execution chain that produced it.</summary>
        public const string CausationId = "vnext.causation.id";

        /// <summary>Vendor-neutral messaging message id, following OpenTelemetry semantic conventions.</summary>
        public const string MessagingMessageId = "messaging.message.id";

        /// <summary>Delivery attempt count for a redelivered message or job.</summary>
        public const string DeliveryAttempt = "vnext.delivery.attempt";
    }

    /// <summary>
    /// Well-known values used to group workflow spans into a compact business view
    /// or the full diagnostic view.
    /// </summary>
    public static class Layers
    {
        public const string Orchestration = "orchestration";
        public const string Execution = "execution";
    }

    public static class SpanCategories
    {
        public const string Business = "business";
        public const string Diagnostic = "diagnostic";
    }

    /// <summary>
    /// Values for <see cref="TagNames.ActivationTrigger"/>: what opened an activation episode.
    /// </summary>
    public static class ActivationTriggers
    {
        /// <summary>Seeded at request entry before the endpoint has classified itself.</summary>
        public const string Http = "http";
        public const string Start = "start";
        public const string Manual = "manual";
        public const string Event = "event";
        public const string Retry = "retry";
        /// <summary>A long-poll acknowledgement resumed the instance.</summary>
        public const string Ack = "ack";
        public const string Scheduled = "scheduled";
        public const string Timeout = "timeout";
        public const string AckTimeout = "ack-timeout";
        /// <summary>A trigger-family task started or advanced another instance.</summary>
        public const string Trigger = "trigger";
        /// <summary>A job hop whose payload carried no episode (older build) — the span is partial.</summary>
        public const string Job = "job";
    }

    /// <summary>
    /// Values for <see cref="TagNames.ActivationOutcome"/>: the rest point an activation episode
    /// reached. Every value is a state a client can observe through the state function.
    /// </summary>
    public static class ActivationOutcomes
    {
        /// <summary>The Busy→Active flip committed — the instance is available.</summary>
        public const string Active = "active";
        public const string Completed = "completed";
        public const string Canceled = "canceled";
        public const string Faulted = "faulted";
        /// <summary>The instance handed off to a SubFlow and rests Busy while the child runs.</summary>
        public const string BusySubflow = "busy.subflow";
        /// <summary>The instance parked Busy at a state whose automatic transitions did not fire.</summary>
        public const string BusyParked = "busy.parked";
        /// <summary>The instance came to rest in a Busy-subtype state, awaiting an external signal.</summary>
        public const string BusySubtype = "busy.subtype";
    }

    /// <summary>
    /// Values for <see cref="TelemetryConstants.TagNames.AuthOutcome"/>.
    /// </summary>
    /// <summary>
    /// Values for <see cref="TelemetryConstants.TagNames.DescentTransport"/>.
    /// </summary>
    public static class DescentTransports
    {
        /// <summary>Same domain: the gateway re-enters the query service in-process.</summary>
        public const string Local = "local";
        /// <summary>Another domain: the gateway calls over HTTP.</summary>
        public const string Remote = "remote";
    }

    /// <summary>
    /// Values for <see cref="TelemetryConstants.TagNames.DescentFunction"/>.
    /// </summary>
    public static class DescentFunctions
    {
        public const string State = "state";
        public const string Master = "master";
        public const string Schema = "schema";
        public const string View = "view";
        public const string Extensions = "extensions";
        public const string Authorize = "authorize";
    }

    public static class AuthOutcomes
    {
        /// <summary>The provider returned a non-empty role set.</summary>
        public const string Resolved = "resolved";
        /// <summary>
        /// The provider answered that this caller holds no roles. A valid answer, not a failure —
        /// but a distinct one: it denies every allowlist grant, and a 403 caused by it looks nothing
        /// like a 403 caused by a caller whose roles simply did not match.
        /// </summary>
        public const string Empty = "empty";
        /// <summary>The provider could not be reached or did not answer usably; the request fails closed.</summary>
        public const string Failed = "failed";
    }

    /// <summary>
    /// HTTP header names used for cross-domain correlation.
    /// </summary>
    public static class HeaderNames
    {
        /// <summary>
        /// Canonical workflow instance identifier propagated to trusted HTTP dependencies.
        /// </summary>
        public const string WorkflowInstanceId = "X-Workflow-Instance-Id";
        /// <summary>
        /// Canonical business correlation identifier propagated to trusted HTTP dependencies.
        /// </summary>
        public const string CorrelationId = "X-Correlation-Id";
        /// <summary>
        /// Primary subject emitted by the gateway after authentication.
        /// </summary>
        public const string Sub = "sub";
        /// <summary>
        /// Actor subject emitted by the gateway after authentication.
        /// </summary>
        public const string ActSub = "act_sub";
        /// <summary>
        /// Request header carrying the parent instance ID when invoking subflow/subprocess remotely.
        /// </summary>
        public const string ParentInstanceId = "X-Parent-Instance-Id";
        /// <summary>
        /// Descent depth of a built-in function's walk into a subflow, carried across a domain
        /// boundary so a mixed local/remote chain numbers its levels 1,2,3 rather than restarting at
        /// each hop. Absent or unparseable ⇒ 0, so an older peer degrades to the previous behaviour
        /// instead of failing.
        /// </summary>
        public const string SubflowDepth = "X-Subflow-Depth";
        /// <summary>
        /// Request header carrying the root (ancestor) instance ID across the full subflow chain.
        /// Remains constant at A's ID regardless of nesting depth.
        /// </summary>
        public const string RootInstanceId = "X-Root-Instance-Id";
        /// <summary>
        /// Request header carrying the originating request id. Read/generated at the edge by
        /// the gateway and by Aether's correlation middleware; forwarded on every internal hop.
        /// </summary>
        public const string RequestId = "X-Request-Id";
        /// <summary>
        /// Request header carrying the caller's W3C trace id (the 32-hex <c>trace.id</c> of the
        /// ambient <see cref="System.Diagnostics.Activity"/>) as a flat value.
        /// <para>
        /// This is NOT a replacement for <c>traceparent</c>: the runtime already propagates the
        /// full W3C trace context on outbound HTTP, and stamping <c>traceparent</c> by hand would
        /// duplicate the header. This one exists so a dependency can log and query the caller's
        /// <c>trace.id</c> through a plain header enricher, without having to parse traceparent.
        /// </para>
        /// </summary>
        public const string TraceId = "X-Trace-Id";

        /// <summary>
        /// The W3C Trace Context headers. Never copied from a captured request onto an outbound one:
        /// <c>HttpClient</c>'s <c>DiagnosticsHandler</c> injects <c>traceparent</c> fill-if-absent,
        /// so a stale copy taken from the inbound request (or from a persisted job payload) would win
        /// over the live <see cref="System.Diagnostics.Activity"/> and parent the callee to a span that
        /// is not the caller's. The task-invoker path keeps its own, wider list in
        /// <c>HttpTaskInvocation.ReservedTraceHeaders</c> (it also drops correlation headers the
        /// binding must not override); this one is deliberately only the W3C trio, because the
        /// remote app-service path legitimately forwards <c>X-Request-Id</c> and friends.
        /// </summary>
        public static readonly string[] W3CTraceContext = ["traceparent", "tracestate", "baggage"];

        /// <summary>True for <c>traceparent</c>, <c>tracestate</c> and <c>baggage</c> (case-insensitive).</summary>
        public static bool IsW3CTraceContextHeader(string? headerName) =>
            headerName is not null &&
            Array.Exists(W3CTraceContext, h => h.Equals(headerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Accepts a constrained actor claim emitted by the gateway authentication chain.
    /// The exact value is retained; unsafe or unexpectedly large values are omitted.
    /// </summary>
    public static bool TryNormalizeIdentityClaim(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        normalized = value;
        return true;
    }
}
