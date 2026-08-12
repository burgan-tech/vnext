using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Aether.Aspects;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution;

/// <summary>
/// Contains all necessary context data for transition execution.
/// This is a minimal, service-free context that carries only essential data and state.
/// Services are injected into pipeline steps and handlers, not stored in the context.
/// </summary>
public sealed class TransitionExecutionContext
{
    // Identity (immutable)
    /// <summary>Gets the domain/tenant identifier.</summary>
    [Enrich(Name = "vnext.domain")]
    public string Domain { get; init; } = default!;

    /// <summary>Gets the workflow instance identifier.</summary>
    [Enrich(Name = "vnext.instance.id")]
    public Guid InstanceId { get; init; }

    /// <summary>Gets the workflow key.</summary>
    [Enrich(Name = "vnext.flow.key")]
    public string WorkflowKey { get; init; } = default!;

    /// <summary>Gets the transition key being executed.</summary>
    [Enrich(Name = "vnext.flow.transition")]
    public string TransitionKey { get; init; } = default!;

    /// <summary>Gets or sets instance key (Optional).</summary>
    public string? InstanceKey { get; set; }
    
    /// <summary>Gets or sets instance tags (Optional).</summary>
    public string[]? Tags { get; set; }

    /// <summary>Gets or sets the instance stage label (Optional, max 120 chars).</summary>
    public string? Stage { get; set; }

    /// <summary>Gets the trigger type that initiated this transition.</summary>
    public TriggerType Trigger { get; init; }

    /// <summary> Get or sets the execution actor (default: User) </summary>
    public ExecutionActor Actor { get; set; } = ExecutionActor.User;

    /// <summary>Gets the correlation identifier for tracking related operations.</summary>
    public string CorrelationId { get; init; } = default!;

    /// <summary>Gets the causation identifier linking this transition to its cause.</summary>
    public string? CausationId { get; init; }

    /// <summary>Gets the execution chain identifier for re-entry tracking.</summary>
    public string ExecutionChainId { get; init; } = default!;

    /// <summary>Gets the depth in the execution chain (for automatic transitions).</summary>
    public int ChainDepth { get; init; }

    /// <summary>Gets the timestamp when this transition was requested.</summary>
    public DateTimeOffset RequestedAt { get; init; }

    // Definitions (rehydrated)
    /// <summary>Gets the workflow definition.</summary>
    public Definitions.Workflow Workflow { get; init; } = default!;

    /// <summary>Gets or sets the current workflow state.</summary>
    public State Current { get; set; } = default!;

    /// <summary>Gets or sets the target workflow state (set during execution).</summary>
    public State? Target { get; set; }

    /// <summary>Gets the transition definition being executed.</summary>
    public Transition? Transition { get; init; } = default!;

    // Instance snapshot
    /// <summary>Gets or sets the workflow instance aggregate.</summary>
    public Instance Instance { get; set; } = default!;

    /// <summary>Gets or sets the concurrency token for optimistic locking.</summary>
    public string ConcurrencyToken { get; set; } = default!;

    /// <summary>Gets or sets the instance data payload.</summary>
    public object? Data { get; set; }

    // Execution flags
    /// <summary>Gets the execution mode (sync/async/resume) from the original request.</summary>
    public ExecMode Mode { get; init; } = ExecMode.Sync;

    /// <summary>Gets the original caller's execution mode intent (sync/async). Used by post-commit handlers for subflow mode propagation.</summary>
    public ExecMode CallerMode { get; init; } = ExecMode.Sync;

    /// <summary>Gets or sets whether to skip immediate execution (for scheduled transitions).</summary>
    public bool SkipImmediateExecution { get; set; }

    /// <summary>Gets whether this is a re-entry execution (automatic/scheduled).</summary>
    public bool IsReentry { get; init; }

    /// <summary>Gets whether this transition was requested by an error boundary (e.g. Rollback/Notify). When true, state policy checks are bypassed so the transition can run from any state.</summary>
    public bool IsErrorBoundaryTransition { get; init; }

    /// <summary>
    /// Gets or sets the active pipeline execution profile for this transition (assigned by <c>TransitionPipeline</c> before executing steps).
    /// </summary>
    public PipelineExecutionProfile? Profile { get; set; }

    /// <summary>
    /// Gets or sets whether the auto-chain continuation should be enqueued as a separate
    /// background job (transition-per-job) instead of executed in-process. Set from the
    /// originating <see cref="WorkflowExecutionContext"/>; selects the continuation strategy
    /// (Enqueue vs Inline) in <c>TransitionPipeline</c>. Default false (Inline).
    /// </summary>
    public bool EnqueueContinuations { get; set; }

    /// <summary>
    /// Gets or sets whether the instance was already reserved (flipped to Busy) for this
    /// execution before the pipeline ran — background-job re-entry after an async accept, or a
    /// chain continuation job. Pre-reserved executions skip the Busy admission check and the
    /// reserve; the accept that created them owns the Busy flag.
    /// </summary>
    public bool IsPreReserved { get; set; }

    /// <summary>
    /// Gets or sets whether this execution owns the instance's Busy lifecycle. Assigned by the
    /// pipeline admission: reserve/takeover/owner re-entry ⇒ true; subflow forward ⇒ false;
    /// updateData ⇒ true only when its opportunistic reserve succeeded or the parent rests in a
    /// SubFlow state. Gates status resolution (<c>ResolveAvailableStep</c>), settlement and
    /// auto-transition advancement for updateData: a non-owner must never flip a Busy it does
    /// not hold, and must not start a competing chain.
    /// </summary>
    public bool OwnsStatus { get; set; }

    /// <summary>
    /// Gets or sets the transition record id this execution RETRIES. Set only by the retry
    /// entry point (<c>RetryInfo.TransitionId</c>): <c>CreateTransitionRecordStep</c> then
    /// reuses the original record instead of creating a fresh one, so the task journal
    /// (<c>InstanceTask</c>, keyed by transition record id) lines up and already-completed
    /// tasks are bypassed instead of re-running their side effects.
    /// </summary>
    public Guid? RetryOfTransitionRecordId { get; set; }

    /// <summary>Gets or sets typed terminal-cascade context for this execution.</summary>
    public TerminationContext? Termination { get; set; }

    // Telemetry & Headers & Temporary storage
    /// <summary>Gets the distributed tracing trace identifier.</summary>
    public string TraceId { get; init; } = default!;

    /// <summary>Gets the distributed tracing span identifier.</summary>
    public string SpanId { get; init; } = default!;

    /// <summary>Gets the request headers.</summary>
    public IReadOnlyDictionary<string, string?> Headers { get; init; } = new Dictionary<string, string?>();

    /// <summary>Gets the request route values.</summary>
    public IReadOnlyDictionary<string, string?> RouteValues { get; init; } = new Dictionary<string, string?>();

    /// <summary>Gets a temporary storage bag for pipeline steps to share data.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public IDictionary<string, object?> Cache { get; } = new Dictionary<string, object?>();
    public void ClearCacheForFinalize() => Cache.Clear();

    // Typed instructions
    public PipelineDirectives Directives { get; } = new();

    public ClientResponse? ClientResponse { get; set; }

    /// <summary>
    /// Gets the distributed lock key for this instance.
    /// Used by TransitionPipeline to acquire/release instance-level locks.
    /// </summary>
    public string LockKey => $"vnext:{Domain}:{WorkflowKey}:{InstanceId}";

    public JsonElement? DataElement => Data switch
    {
        JsonElement element => element,
        string jsonString => JsonSerializer.Deserialize<JsonElement>(jsonString),
        null => null,
        _ => JsonSerializer.SerializeToElement(Data)
    };

    /// <summary>
    /// Gets or builds a ScriptContext using the provided factory function.
    /// The ScriptContext is cached in Cache to avoid recreating it multiple times.
    /// </summary>
    /// <param name="factory">Async factory function to create a new ScriptContext if not cached.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>The cached or newly created ScriptContext.</returns>
    public async Task<ScriptContext> GetOrBuildScriptContextAsync(
        Func<CancellationToken, Task<ScriptContext>> factory,
        CancellationToken cancellationToken = default)
    {
        if (Cache.TryGetValue("ScriptContext", out var cached) && cached is ScriptContext scriptContext)
            return scriptContext;

        var created = await factory(cancellationToken);
        Cache["ScriptContext"] = created;
        return created;
    }

    /// <summary>
    /// Re-bases the cached <see cref="ScriptContext"/>'s instance snapshot onto the current
    /// (live) <see cref="Instance"/>. Call this after a state change so that subsequent steps
    /// reusing the cached ScriptContext (e.g. OnEntry tasks and state-level error boundary
    /// resolution) observe the new <c>CurrentState</c> rather than the snapshot frozen before
    /// the change. No-op when no ScriptContext has been built yet, so the lazy-build behavior
    /// of <see cref="GetOrBuildScriptContextAsync"/> is preserved.
    /// </summary>
    public void RefreshScriptContextInstance()
    {
        if (Instance is null)
            return;

        if (Cache.TryGetValue("ScriptContext", out var cached) && cached is ScriptContext scriptContext)
            scriptContext.RefreshInstance(Instance);
    }

    /// <summary>
    /// Extracts pending distributed events from the Instance aggregate and defers them
    /// in <see cref="Directives"/> for explicit publishing after UoW commit.
    /// Clears the aggregate's event list so they won't be dispatched automatically via IDomainEventSink/SaveChanges.
    /// </summary>
    public void ExtractAndDeferInstanceEvents()
    {
        if (Instance == null)
            return;

        var domainEvents = Instance.GetDomainEvents();
        if (domainEvents.Count == 0)
            return;

        Directives.DeferEvents(domainEvents);
        Instance.ClearDomainEvents();
    }

    /// <summary>
    /// Applies changes made within the provided <see cref="ScriptContext"/> back to the live transition context.
    /// </summary>
    /// <param name="scriptContext">The script context containing potential instance updates.</param>
    public void ApplyScriptContextChanges(ScriptContext scriptContext)
    {
        ArgumentNullException.ThrowIfNull(scriptContext);

        var scriptInstance = scriptContext.Instance;
        if (scriptInstance == null || Instance == null)
        {
            return;
        }

        // Task outputs are persisted IMMEDIATELY by the InstanceData write service (identity
        // computed under the per-instance row lock) — there is no data replay here anymore.
        // What remains is keeping the LIVE aggregate's in-memory latest in sync with the
        // snapshot's freshest persisted row (parallel-branch scopes write through their own
        // DbContext, so EF fixup cannot attach those rows to this aggregate) and applying the
        // non-data mutations.
        var snapshotLatest = scriptInstance.LatestData;
        if (snapshotLatest is not null
            && Instance.DataList.All(data => data.Id != snapshotLatest.Id))
        {
            Instance.AcceptPersistedData(snapshotLatest.CreateSnapshot());
            Data = Instance.Data;
        }

        if (scriptContext.Mutations.HasChanges)
        {
            scriptContext.Mutations.ApplyTo(Instance);
        }
    }
}
