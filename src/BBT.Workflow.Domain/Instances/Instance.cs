using BBT.Aether;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.Instances;

/// <summary>
/// Instance
/// </summary>
public sealed class Instance : AggregateRoot<Guid>, ICreationAuditedObject, IModifyAuditedObject, IHasExtraProperties
{  
    private Instance()
    {
    }

    internal Instance(
        Guid id,
        string flow,
        string flowVersion,
        string? key
    ) : base(id)
    {
        IsTransient = true;
        CreatedAt = DateTime.UtcNow;
        ModifiedAt = DateTime.UtcNow;
        Flow = Check.NotNullOrWhiteSpace(flow, nameof(Flow), WorkflowConstants.MaxKeyLength);
        FlowVersion = Check.NotNullOrWhiteSpace(flowVersion, nameof(FlowVersion), WorkflowConstants.MaxVersionLength);
        Key = Check.Length(key, nameof(Key), InstanceConstants.MaxKeyLength);
        Status = InstanceStatus.Active;

        Tags = [];

        ExtraProperties = new ExtraPropertyDictionary();

        _dataList = [];
        _incidents = [];
    }

    /// <summary>
    /// Creates a new instance with the given identity and flow. Optionally sets the flow version (used at start; null is treated as latest at runtime).
    /// </summary>
    public static Instance Create(
        Guid id,
        string flow,
        string flowVersion,
        string? key = null
    )
    {
        return new Instance(
            id,
            flow,
            flowVersion,
            key
        );
    }

    /// <summary>
    /// It is the key value for the heat flow.
    /// </summary>
    public string? Key { get; private set; }
    
    public bool HasKey => !string.IsNullOrWhiteSpace(Key);

    /// <summary>
    /// Flow key.
    /// </summary>
    public string Flow { get; private set; }

    /// <summary>
    /// Flow version at instance start. Null for legacy instances (resolved as latest at runtime).
    /// </summary>
    public string FlowVersion { get; private set; }

    /// <summary>
    /// Current state key - engine internal state (hidden from external world)
    /// </summary>
    public string? CurrentState { get; private set; }

    /// <summary>
    /// Type of the current (engine-internal) state.
    /// Updated together with CurrentState in <see cref="ChangeState"/>.
    /// </summary>
    public StateType? CurrentStateType { get; private set; }

    /// <summary>
    /// Subtype of the current (engine-internal) state.
    /// Updated together with CurrentState in <see cref="ChangeState"/>.
    /// </summary>
    public StateSubType? CurrentStateSubType { get; private set; }

    /// <summary>
    /// Effective state - the state exposed to the external world (persisted in DB)
    /// For parent: SubFlow's state if active SubFlow exists, otherwise own state
    /// For SubFlow: Own state
    /// </summary>
    public string? EffectiveState { get; private set; }

    /// <summary>
    /// Type of the effective state (Initial, Intermediate, Finish, SubFlow, Wizard)
    /// Tracked alongside EffectiveState for efficient filtering without state definition joins
    /// </summary>
    public StateType? EffectiveStateType { get; private set; }
    
    /// <summary>
    /// Subtype of the effective state (None, Success, Error, Terminated, Suspended, Busy, Human)
    /// Tracked alongside EffectiveState for efficient filtering and automated status handling
    /// </summary>
    public StateSubType? EffectiveStateSubType { get; private set; }

    /// <summary>
    /// Free-form stage label set by the caller at start or transition time.
    /// Enables lightweight categorization without workflow definition changes.
    /// </summary>
    public string? Stage { get; private set; }

    public string GetCurrentState => string.IsNullOrWhiteSpace(CurrentState) ? string.Empty : CurrentState;
    
    public string GetEffectiveState => string.IsNullOrWhiteSpace(EffectiveState) ? string.Empty : EffectiveState;

    /// <summary>
    /// Status
    /// </summary>
    public InstanceStatus Status { get; private set; }

    /// <summary>
    /// Long-poll acknowledge token. Set when the pipeline pauses on entering a state whose
    /// <c>interaction.longPoll.terminate</c> is true; the State (long-poll) function surfaces the
    /// termination signal while this is non-null, and the pipeline resumes when the client
    /// acknowledges (or the fallback schedule fires). The token guards against double-resume:
    /// acknowledge and fallback compare-and-clear it so only one wins. Null when no long-poll
    /// acknowledge is pending.
    /// </summary>
    public Guid? LongPollAckToken { get; private set; }

    /// <summary>
    /// True when the aggregate was materialized with only the IsLatest data row (latest-only
    /// loading). History-dependent members fail fast in this mode instead of silently returning
    /// wrong answers; use the repository full-history APIs
    /// (<c>FindByIdentifierWithFullHistoryAsync</c> / <c>FindByIdentifierWithFullDataAsync</c>)
    /// for version-line reads and line-targeted appends. Not persisted.
    /// </summary>
    public bool IsDataPartiallyLoaded { get; private set; }

    /// <summary>
    /// Marks the aggregate as latest-only loaded. Set by the repository right after
    /// materialization when latest-only instance loading is enabled.
    /// </summary>
    public void MarkDataPartiallyLoaded() => IsDataPartiallyLoaded = true;

    /// <summary>
    /// Completed at
    /// </summary>
    public DateTime? CompletedAt { get; private set; }

    public bool IsCompleted =>
        Status.Equals(InstanceStatus.Completed)
        || Status.Equals(InstanceStatus.Faulted)
        || Status.Equals(InstanceStatus.Passive);

    public bool IsBusy => Status.Equals(InstanceStatus.Busy);
    public bool IsActive => Status.Equals(InstanceStatus.Active);
    public bool IsSubFlow => this.ToFlowType() == WorkflowType.SubFlow;

    public bool IsSubItem => this.ToFlowType() == WorkflowType.SubFlow ||
                             this.ToFlowType() == WorkflowType.SubProcess;

    public bool HasActiveSubFlow =>
        _childCorrelations.Any(p => !p.IsCompleted && p.SubFlowType.Equals(SubFlowType.SubFlow));

    public TimeSpan? Duration { get; private set; }
    public List<string> Tags { get; private set; }

    /// <summary>
    /// Created at
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Modified at
    /// </summary>
    public DateTime? ModifiedAt { get; set; }
    
    /// <summary>
    /// Creator user identifier.
    /// </summary>
    public string? CreatedBy { get; set; }
    
    /// <summary>
    /// Creator behalf-of user identifier.
    /// </summary>
    public string? CreatedByBehalfOf { get; set; }
    
    /// <summary>
    /// Modifier user identifier.
    /// </summary>
    public string? ModifiedBy { get; set; }
    
    /// <summary>
    /// Modifier behalf-of user identifier.
    /// </summary>
    public string? ModifiedByBehalfOf { get; set; }

    public bool IsTransient { get; private set; }

    public ExtraPropertyDictionary ExtraProperties { get; private set; }

    private readonly List<InstanceIncident> _incidents = new();
    private readonly List<InstanceIncident> _pendingIncidents = new();
    private readonly List<InstanceIncident> _detachedIncidents = new();

    /// <summary>
    /// Coalescing state for <c>sub:state-changed</c>: the state this activation episode STARTED in,
    /// and whether it moved at all. In-memory only — never mapped, never persisted. Its lifetime is
    /// the tracked aggregate's, which is exactly one episode: the inline auto-chain reuses the same
    /// instance across its hops (<c>CreateFromPreloaded</c>), while every post-commit, retry or
    /// subflow-callback boundary loads a fresh one. See
    /// <see cref="PublishPendingSubStateChange"/>.
    /// </summary>
    private string? _pendingSubStateChangeFrom;
    private bool _hasPendingSubStateChange;

    /// <summary>
    /// Incidents materialized on this aggregate. A real child collection (table
    /// <c>InstanceIncidents</c>) that is <b>never</b> included by default — every load path leaves it
    /// empty and <see cref="IncidentsLoaded"/> false. Callers that need the unresolved incidents
    /// (resolve, upward fault payload, script context, state function) check
    /// <see cref="HasActiveIncident"/> first and load them through
    /// <c>IInstanceRepository.LoadActiveIncidentsAsync</c>. Kept internal so the collection is only
    /// reachable through the aggregate's own operations; EF binds it by name with field access.
    /// </summary>
    internal IReadOnlyCollection<InstanceIncident> Incidents => _incidents.AsReadOnly();

    /// <summary>
    /// Returns the incidents currently loaded on this aggregate — never the full history. Empty
    /// unless <c>LoadActiveIncidentsAsync</c> ran (unresolved rows) or incidents were added in this
    /// unit of work. Full history is served by <c>IInstanceIncidentRepository</c>.
    /// </summary>
    /// <remarks>
    /// Two sources, deliberately kept apart underneath: rows this unit of work owns (the EF
    /// navigation) and rows read back outside the change tracker (see
    /// <see cref="AcceptLoadedIncidents"/>). Readers do not care which is which; the aggregate does.
    /// </remarks>
    public IReadOnlyList<InstanceIncident> GetLoadedIncidents() =>
        _detachedIncidents.Count == 0
            ? _incidents.AsReadOnly()
            : _incidents.Concat(_detachedIncidents).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList();

    /// <summary>
    /// Incidents added through <see cref="AddIncident"/> that have not been persisted yet. Read by
    /// the repository so a new row is always inserted as <c>Added</c> — even when the aggregate
    /// itself is detached and EF's graph walk would otherwise mark a client-keyed child as
    /// <c>Modified</c>. A method, not a property: Aether's <c>TrackRelatedEntities</c> reflects over
    /// public <c>IEnumerable</c> properties at SaveChanges and would stamp the root's state onto
    /// these rows, and EF's convention discovery would turn a property into a second navigation.
    /// </summary>
    public IReadOnlyCollection<InstanceIncident> GetPendingIncidents() => _pendingIncidents.AsReadOnly();

    /// <summary>Forgets the pending incidents after the repository has persisted them.</summary>
    public void ClearPendingIncidents() => _pendingIncidents.Clear();

    /// <summary>
    /// Indicates whether the instance has at least one unresolved incident. A persisted,
    /// denormalized column on the instance row (partial index on <c>true</c>) maintained by
    /// <see cref="AddIncident"/> and <see cref="ResolveOpenIncidents"/>, so the hot path never has
    /// to touch the incidents table to answer it.
    /// </summary>
    public bool HasActiveIncident { get; private set; }

    /// <summary>
    /// True once the unresolved incidents have been materialized on this aggregate (or the flag says
    /// there are none). Runtime-only marker, never persisted; guards
    /// <see cref="ResolveOpenIncidents"/> against silently resolving nothing.
    /// </summary>
    public bool IncidentsLoaded { get; private set; }

    /// <summary>
    /// Marks the unresolved incidents as loaded. Called by the repository after a tracked
    /// collection load, or when <see cref="HasActiveIncident"/> is false and no query was needed.
    /// </summary>
    public void MarkIncidentsLoaded() => IncidentsLoaded = true;

    /// <summary>
    /// Accepts incidents materialized outside the change tracker (no-tracking loads) and marks the
    /// aggregate as loaded. Rows already present (e.g. added in this unit of work) are not duplicated.
    /// </summary>
    public void AcceptLoadedIncidents(IReadOnlyList<InstanceIncident> incidents)
    {
        foreach (var incident in incidents.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id))
        {
            if (_incidents.All(existing => existing.Id != incident.Id) &&
                _detachedIncidents.All(existing => existing.Id != incident.Id))
            {
                // NOT the EF navigation. These rows already exist in the database and are read
                // outside the change tracker, so putting them in `_incidents` would make every
                // DbContext that tracks this aggregate — including one that loaded it earlier, in
                // an outer unit of work — discover them as NEW children of a tracked root and
                // INSERT them again. That is a duplicate-key crash on the retry path, where the
                // aggregate is loaded by the ambient request scope and the incidents are loaded
                // inside a RequiresNew scope. Measured, then fixed.
                _detachedIncidents.Add(incident);
            }
        }

        IncidentsLoaded = true;
    }

    public void SetMetaData(ExtraPropertyDictionary data)
    {
        ExtraProperties = data;
    }

    /// <summary>
    /// Sets system-generated metadata for the instance.
    /// This method encapsulates the business logic for setting system metadata keys.
    /// </summary>
    /// <param name="isSync">Whether the instance is synchronous</param>
    /// <param name="callback">Callback URL for the instance</param>
    /// <param name="flowType">The workflow type code</param>
    /// <param name="userMetadata">Optional user-provided metadata to merge</param>
    public void SetInfoMetadata(bool isSync, string? callback, string flowType, ExtraPropertyDictionary? userMetadata = null)
    {
        var metadata = userMetadata ?? new ExtraPropertyDictionary();

        // Set system metadata - these are always set by the system
        metadata.TryAdd(DomainConsts.MetaDataKeys.Sync, isSync.ToString().ToLower());
        metadata.TryAdd(DomainConsts.MetaDataKeys.Callback, callback ?? string.Empty);
        metadata.TryAdd(DomainConsts.MetaDataKeys.FlowType, flowType);

        SetMetaData(metadata);
    }

    private readonly List<InstanceData> _dataList = new();
    private readonly Lock _dataListLock = new(); // Thread-safe lock for data operations

    /// <summary>
    /// Child Correlations
    /// </summary>
    public IReadOnlyCollection<InstanceData> DataList => _dataList.AsReadOnly();

    private int _dataMemoCount = -1;
    private InstanceData? _latestRowMemo;

    /// <summary>
    /// Latest data
    /// </summary>
    public dynamic? Data
    {
        get
        {
            lock (_dataListLock)
            {
                return LatestRowLocked()?.Attributes;
            }
        }
    }

    public InstanceData? LatestData
    {
        get
        {
            lock (_dataListLock)
            {
                return LatestRowLocked();
            }
        }
    }

    /// <summary>
    /// _dataList append-only'dir (Add yalnız ctor/CreateSnapshot/AcceptPersistedData'da; Remove yok —
    /// keşifle doğrulandı), bu yüzden liste SAYISI değişmediyse latest satır da değişmemiştir:
    /// sıralama+Attributes maliyeti erişim başına değil, append başına ödenir.
    /// </summary>
    private InstanceData? LatestRowLocked()
    {
        if (_dataMemoCount != _dataList.Count)
        {
            _latestRowMemo = _dataList.OrderByDescending(x => x, InstanceDataVersionComparer.Instance).FirstOrDefault();
            _dataMemoCount = _dataList.Count;
        }
        return _latestRowMemo;
    }

    private readonly List<InstanceCorrelation> _childCorrelations = new();

    /// <summary>
    /// Child Correlations
    /// </summary> 
    public IReadOnlyCollection<InstanceCorrelation> ChildCorrelations => _childCorrelations.AsReadOnly();

    public IReadOnlyCollection<InstanceCorrelation> ActiveCorrelations =>
        _childCorrelations.Where(p => !p.IsCompleted).OrderBy(o => o.CreatedAt).ToList();

    public InstanceCorrelation? Subflow =>
        ChildCorrelations.FirstOrDefault(p => !p.IsCompleted && p.SubFlowType.Equals(SubFlowType.SubFlow));

    public Instance CreateSnapshot()
    {
        var snapshot = new Instance
        {
            Id = Id,
            IsTransient = IsTransient,
            CreatedAt = CreatedAt,
            ModifiedAt = ModifiedAt,
            Flow = Flow,
            FlowVersion = FlowVersion,
            Key = Key,
            Status = Status,
            CompletedAt = CompletedAt,
            CurrentState = CurrentState,
            CurrentStateType = CurrentStateType,
            CurrentStateSubType = CurrentStateSubType,
            EffectiveState = EffectiveState,
            EffectiveStateType = EffectiveStateType,
            EffectiveStateSubType = EffectiveStateSubType,
            Stage = Stage,
            Duration = Duration,
            Tags = [.. Tags],
            CreatedBy = CreatedBy,
            CreatedByBehalfOf = CreatedByBehalfOf,
            ModifiedBy = ModifiedBy,
            ModifiedByBehalfOf = ModifiedByBehalfOf,
            ExtraProperties = new ExtraPropertyDictionary(ExtraProperties),
            HasActiveIncident = HasActiveIncident,
            IncidentsLoaded = IncidentsLoaded
        };

        foreach (var data in _dataList)
        {
            snapshot._dataList.Add(data.CreateSnapshot());
        }

        // Carry the partial-load marker: a latest-only-loaded aggregate copies a one-entry
        // history into the snapshot, and without the marker FindData/GetVersionHistory on the
        // snapshot would return silently-wrong answers instead of failing fast.
        if (IsDataPartiallyLoaded)
        {
            snapshot.MarkDataPartiallyLoaded();
        }

        foreach (var correlation in _childCorrelations)
        {
            snapshot._childCorrelations.Add(correlation.CreateSnapshot());
        }

        // Shallow copy on purpose: snapshots are read-only projections (script context) and are
        // never handed to UpdateAsync, so sharing the incident objects is safe. Both sources are
        // copied into the snapshot's DETACHED list: a snapshot must never look like an aggregate
        // with new children to insert.
        snapshot._detachedIncidents.AddRange(GetLoadedIncidents());

        return snapshot;
    }

    /// <summary>
    /// Completes the instance and publishes completion cleanup event.
    /// Sets the instance status to Completed and records the completion time.
    /// </summary>
    /// <param name="domain">The domain of the instance.</param>
    /// <param name="sync">Whether the completing pipeline chain runs with a synchronous caller (sync=true).</param>
    public void Complete(string domain, bool sync = false)
    {
        Status = InstanceStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        Duration = CompletedAt - CreatedAt;

        // Publish cleanup event to cancel all scheduled jobs
        var rootId = this.GetRootInstanceId();
        AddDistributedEvent(new InstanceCompletedCleanupEvent
        {
            InstanceId = Id,
            Domain = domain,
            Flow = Flow,
            Version =  FlowVersion,
            CompletedAt = CompletedAt.Value,
            RootInstanceId = rootId != Id ? rootId : (Guid?)null
        });

        // Publish completion event for SubItems (SubFlow or SubProcess)
        if (IsSubItem)
        {
            var latestData = LatestData;
            var contractInfo = ExtraProperties.ToSubFlowContractInfo();
            if (contractInfo.Id != Guid.Empty)
            {
                AddDistributedEvent(new InstanceSubCompletedEvent
                {
                    SubInstanceId = Id,
                    InstanceId = contractInfo.Id,
                    Domain = contractInfo.Domain,
                    Flow = contractInfo.Flow,
                    Version = contractInfo.Version,
                    CompletedState = GetCurrentState,
                    InstanceData = latestData?.Data.JsonElement,
                    CompletedAt = CompletedAt.Value,
                    Duration = Duration,
                    RootInstanceId = rootId != Id ? rootId : (Guid?)null,
                    Sync = sync
                });
            }
        }
    }

    /// <summary>
    /// Marks the instance as faulted and publishes fault cleanup event.
    /// Also propagates fault downward to active SubFlow children and upward to the parent for direct SubItem faults.
    /// Correlations are intentionally kept open (not completed) so retry can cascade through them.
    /// </summary>
    /// <param name="domain">The domain of the instance.</param>
    /// <param name="sync">Whether the faulting pipeline chain runs with a synchronous caller (sync=true).</param>
    /// <param name="termination">Typed context for direct or parent-cascaded termination.</param>
    public void Fault(string domain, bool sync = false, TerminationContext? termination = null)
    {
        var effectiveTermination = termination ?? TerminationContext.Direct(Id);
        var childTermination = effectiveTermination.AsParentCascade();
        Status = InstanceStatus.Faulted;
        CompletedAt = DateTime.UtcNow;
        Duration = CompletedAt - CreatedAt;

        var rootId = this.GetRootInstanceId();
        AddDistributedEvent(new InstanceFaultedCleanupEvent
        {
            InstanceId = Id,
            Domain = domain,
            Flow = Flow,
            Version = FlowVersion,
            FaultedAt = CompletedAt.Value,
            RootInstanceId = rootId != Id ? rootId : (Guid?)null
        });

        // Downward: notify active SubFlow children to fault themselves
        foreach (var correlation in ActiveCorrelations
            .Where(c => c.SubFlowType.Equals(SubFlowType.SubFlow)))
        {
            AddDistributedEvent(new ChildSubflowFaultRequestedEvent
            {
                InstanceId = correlation.SubFlowInstanceId,
                ParentInstanceId = Id,
                Domain = correlation.SubFlowDomain,
                Flow = correlation.SubFlowName,
                Version = correlation.SubFlowVersion,
                FaultedAt = CompletedAt.Value,
                RootInstanceId = rootId != Id ? rootId : (Guid?)null,
                Termination = childTermination
            });
        }

        // Upward: direct SubItem termination only. Parent cascades never bounce back upward.
        if (IsSubItem && effectiveTermination.Origin == TerminationOrigin.Direct)
        {
            var subItemType = IsSubFlow ? SubItemType.SubFlow : SubItemType.SubProcess;
            var activeIncident = IsSubFlow ? _incidents.LastOrDefault(i => !i.IsResolved) : null;
            var latestData = IsSubFlow ? LatestData : null;
            var contractInfo = ExtraProperties.ToSubFlowContractInfo();
            if (contractInfo.Id != Guid.Empty)
            {
                AddDistributedEvent(new InstanceSubFaultedEvent
                {
                    InstanceId = contractInfo.Id,
                    SubInstanceId = Id,
                    Domain = contractInfo.Domain,
                    Flow = contractInfo.Flow,
                    Version = contractInfo.Version,
                    FaultedState = GetCurrentState,
                    FaultedStateType = CurrentStateType.HasValue ? (int)CurrentStateType.Value : null,
                    FaultedStateSubType = CurrentStateSubType.HasValue ? (int)CurrentStateSubType.Value : null,
                    InstanceData = latestData?.Data.JsonElement,
                    FaultedAt = CompletedAt.Value,
                    SubFlowName = Flow,
                    IncidentMessage = activeIncident?.Message,
                    IncidentErrorCode = activeIncident?.ErrorCode,
                    IncidentErrorLayer = activeIncident?.ErrorLayer,
                    IncidentStackTrace = activeIncident?.StackTrace,
                    IncidentStatusCode = activeIncident?.StatusCode,
                    IncidentTraceId = activeIncident?.TraceId,
                    IncidentTaskKey = activeIncident?.Task,
                    IncidentTransition = activeIncident?.Transition,
                    IncidentState = activeIncident?.State,
                    IncidentBoundaryAction = activeIncident?.BoundaryAction,
                    IncidentBoundaryLevel = activeIncident?.BoundaryLevel,
                    RootInstanceId = rootId != Id ? rootId : (Guid?)null,
                    Sync = sync,
                    SubItemType = subItemType,
                    TerminationOrigin = effectiveTermination.Origin,
                    InitiatorInstanceId = effectiveTermination.InitiatorInstanceId,
                    CascadeId = effectiveTermination.CascadeId
                });
            }
        }
    }
    /// <summary>
    /// Unfaults the instance, allowing it to be retried.
    /// Changes the status from Faulted to Active, clears completion time,
    /// and resolves every open incident.
    /// </summary>
    /// <remarks>
    /// The incidents must be materialized first (<c>IInstanceRepository.LoadActiveIncidentsAsync</c>)
    /// or <see cref="ResolveOpenIncidents"/> throws. When the aggregate was loaded outside a change
    /// tracker — the retry path — the resolutions also have to be written explicitly through
    /// <c>IInstanceIncidentRepository.ResolveAllAsync</c>; the in-memory <c>Resolve()</c> alone
    /// persists nothing there.
    /// </remarks>
    /// <returns>True if the instance was successfully unfaulted, false if it was not in Faulted state.</returns>
    public bool Unfault()
    {
        if (!Status.Equals(InstanceStatus.Faulted))
            return false;
 
        Status = InstanceStatus.Active;
        CompletedAt = null;
        Duration = null;
        ResolveOpenIncidents();
        return true;
    }

    /// <summary>
    /// Records an error boundary incident on this instance. The incident becomes a pending child
    /// row (persisted by the repository on the next update) and, when unresolved, raises
    /// <see cref="HasActiveIncident"/>. History is unbounded — nothing is pruned.
    /// </summary>
    public void AddIncident(InstanceIncident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        incident.InstanceId = Id;
        _incidents.Add(incident);
        _pendingIncidents.Add(incident);

        if (!incident.IsResolved)
            HasActiveIncident = true;
    }

    /// <summary>
    /// Resolves EVERY unresolved incident materialized on this aggregate and recomputes
    /// <see cref="HasActiveIncident"/>. Called on successful retry (<see cref="Unfault"/>) and when
    /// an error-boundary transition completes without a new fault.
    /// </summary>
    /// <remarks>
    /// Set-based on purpose. One failure can leave more than one open row — a job-timeout recovery
    /// on top of a boundary incident, a boundary transition that faults on its own, a parent taking
    /// a subflow fault while it already carries one — and resolving only the newest left
    /// <see cref="HasActiveIncident"/> stuck true on an instance that had recovered and completed.
    /// A client rendering "why is this instance stuck?" then showed a stale reason on a healthy
    /// instance.
    /// </remarks>
    /// <returns>The incidents this call resolved, oldest first; empty when nothing was open.</returns>
    /// <exception cref="InvalidOperationException">
    /// The flag says an incident is active but the unresolved incidents were never loaded on this
    /// aggregate — resolving would silently do nothing and leave the flag stuck. Call
    /// <c>IInstanceRepository.LoadActiveIncidentsAsync</c> first.
    /// </exception>
    public IReadOnlyList<InstanceIncident> ResolveOpenIncidents()
    {
        var loaded = GetLoadedIncidents();

        // An empty list satisfies All(IsResolved), so a flag-only aggregate — which is what every
        // default load path produces — fails fast instead of quietly resolving nothing.
        if (HasActiveIncident && !IncidentsLoaded && loaded.All(i => i.IsResolved))
        {
            throw new InvalidOperationException(
                $"Instance {Id} has an active incident but the unresolved incidents are not loaded on this aggregate. " +
                "Call IInstanceRepository.LoadActiveIncidentsAsync before resolving.");
        }

        var open = loaded.Where(i => !i.IsResolved).ToList();
        foreach (var incident in open)
        {
            incident.Resolve();
        }

        // Recomputed from the same snapshot rather than hard-set to false, so a partially
        // materialized aggregate can never claim the flag is clear.
        HasActiveIncident = loaded.Any(i => !i.IsResolved);
        return open;
    }

    /// <summary>
    /// True when the incident is a row read outside the change tracker, so the in-memory
    /// <c>Resolve()</c> will not be written by the graph.
    /// </summary>
    /// <remarks>
    /// The retry path no longer asks: it persists resolutions unconditionally through
    /// <c>IInstanceIncidentRepository.ResolveAllAsync</c>, which is idempotent and correct for
    /// tracked and detached rows alike. Kept because it is the only way to tell the two sources
    /// apart from outside the aggregate.
    /// </remarks>
    public bool IsDetachedIncident(InstanceIncident incident) =>
        _detachedIncidents.Any(existing => ReferenceEquals(existing, incident));
    /// <summary>
    /// Cancels the instance and publishes a cancellation event.
    /// Sets the instance status to Canceled and records the completion time.
    /// </summary>
    /// <param name="domain">The domain of the instance.</param>
    /// <param name="sync">Whether the canceling pipeline chain runs with a synchronous caller.</param>
    /// <param name="termination">Typed context for direct or parent-cascaded termination.</param>
    public void Cancel(string domain, bool sync = false, TerminationContext? termination = null)
    {
        var effectiveTermination = termination ?? TerminationContext.Direct(Id);
        var childTermination = effectiveTermination.AsParentCascade();
        Status = InstanceStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        Duration = CompletedAt - CreatedAt;

        // Publish cancellation event - event handler will handle cleanup (jobs, correlations)
        var rootId = this.GetRootInstanceId();
        AddDistributedEvent(new InstanceCanceledEvent
        {
            InstanceId = Id,
            Domain = domain,
            Flow = Flow,
            Version =   FlowVersion,
            CanceledState = GetCurrentState,
            CanceledAt = CompletedAt.Value,
            Duration = Duration,
            RootInstanceId = rootId != Id ? rootId : (Guid?)null
        });

        if (IsSubItem && effectiveTermination.Origin == TerminationOrigin.Direct)
        {
            var contractInfo = ExtraProperties.ToSubFlowContractInfo();
            if (contractInfo.Id != Guid.Empty)
            {
                AddDistributedEvent(new InstanceSubCanceledEvent
                {
                    InstanceId = contractInfo.Id,
                    SubInstanceId = Id,
                    Domain = contractInfo.Domain,
                    Flow = contractInfo.Flow,
                    Version = contractInfo.Version,
                    CanceledState = GetCurrentState,
                    CanceledAt = CompletedAt.Value,
                    RootInstanceId = rootId != Id ? rootId : (Guid?)null,
                    SubItemType = IsSubFlow ? SubItemType.SubFlow : SubItemType.SubProcess,
                    Sync = sync,
                    TerminationOrigin = effectiveTermination.Origin,
                    InitiatorInstanceId = effectiveTermination.InitiatorInstanceId,
                    CascadeId = effectiveTermination.CascadeId
                });
            }
        }

        foreach (var correlation in ActiveCorrelations)
        {
            correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Canceled, CompletedAt.Value);
            AddDistributedEvent(new ChildSubflowCancelRequestedEvent
            {
                ParentInstanceId = correlation.ParentInstanceId,
                InstanceId = correlation.SubFlowInstanceId,
                Domain = correlation.SubFlowDomain,
                Flow = correlation.SubFlowName,
                CompletedAt = correlation.CompletedAt!.Value,
                Version = correlation.SubFlowVersion,
                RootInstanceId = rootId != Id ? rootId : (Guid?)null,
                Termination = childTermination
            });
        }
    }

    /// <summary>
    /// Sets the instance status to Busy.
    /// This is typically called when a transition is being processed to prevent concurrent modifications.
    /// </summary>
    public void Busy()
    {
        if (IsCompleted)
            return;

        Status = InstanceStatus.Busy;
    }

    /// <summary>
    /// Arms the long-poll acknowledge marker with the supplied token (pipeline paused on state entry).
    /// </summary>
    public void ArmLongPollAck(Guid token) => LongPollAckToken = token;

    /// <summary>
    /// Clears the long-poll acknowledge marker (acknowledge received or fallback resumed).
    /// </summary>
    public void ClearLongPollAck() => LongPollAckToken = null;

    /// <summary>
    /// True while a long-poll acknowledge is pending (the pipeline is paused on state entry).
    /// </summary>
    public bool IsAwaitingLongPollAck => LongPollAckToken.HasValue;

    /// <summary>
    /// Sets the instance status to Active.
    /// This is typically called when a transition processing is completed successfully.
    /// </summary>
    public void Active()
    {
        if (IsCompleted)
            return;

        Status = InstanceStatus.Active;
    }

    /// <summary>
    /// Determines whether this instance should publish a completion event.
    /// This is typically true for SubItems (SubFlow or SubProcess) that have completed.
    /// </summary>
    public bool ShouldPublishCompletionEvent()
    {
        return IsSubItem && IsCompleted;
    }

    public void AddCorrelation(InstanceCorrelation correlation)
    {
        _childCorrelations.Add(correlation);
        if (correlation.SubFlowType.Equals(SubFlowType.SubFlow))
        {
            Busy();
        }
    }

    /// <summary>
    /// Finds a correlation by SubFlow instance ID.
    /// </summary>
    /// <param name="subInstanceId">The SubFlow instance ID to find</param>
    /// <returns>The correlation if found, otherwise null</returns>
    public InstanceCorrelation? FindCorrelationBySubInstanceId(Guid subInstanceId)
    {
        return _childCorrelations.FirstOrDefault(c => c.SubFlowInstanceId == subInstanceId);
    }

    /// <summary>
    /// Completes a correlation for the given SubFlow instance ID.
    /// Marks the correlation as completed and returns it.
    /// If the correlation is a SubFlow type, sets the instance to Active status.
    /// </summary>
    /// <param name="subInstanceId">The SubFlow instance ID to complete</param>
    /// <returns>The completed correlation if found and not already completed, otherwise null</returns>
    public InstanceCorrelation? CompleteCorrelation(Guid subInstanceId)
    {
        return CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Completed);
    }

    /// <summary>
    /// Completes a correlation for the given SubFlow instance ID with a terminal outcome.
    /// </summary>
    /// <param name="subInstanceId">The SubFlow instance ID to complete</param>
    /// <param name="outcome">The terminal outcome to persist</param>
    /// <param name="completedAt">The completion timestamp, or the current UTC time when omitted</param>
    /// <returns>The completed correlation when the outcome is first applied, otherwise null</returns>
    public InstanceCorrelation? CompleteCorrelation(
        Guid subInstanceId,
        SubItemTerminalOutcome outcome,
        DateTime? completedAt = null)
    {
        var correlation = FindCorrelationBySubInstanceId(subInstanceId);
        if (correlation == null || correlation.IsCompleted)
        {
            return null;
        }

        correlation.ApplyTerminalOutcome(outcome, completedAt ?? DateTime.UtcNow);

        // NOTE: Do NOT call Active() here for SubFlow type.
        // The parent must remain Busy until ClearBusyOnResumeStep runs in ResumePipelineAsync.
        // Transitioning to Active here would cause the state endpoint to return Active
        // during the processing window between correlation completion and pipeline resume,
        // falsely signaling to clients that the flow is no longer busy.

        return correlation;
    }

    /// <summary>
    /// Reverts a previously completed correlation for the given SubFlow instance ID.
    /// Marks the correlation as incomplete and returns it.
    /// If the correlation is a SubFlow type, sets the instance back to Busy status.
    /// </summary>
    /// <param name="subInstanceId">The SubFlow instance ID to revert</param>
    /// <returns>The reverted correlation if found and was completed, otherwise null</returns>
    public InstanceCorrelation? RevertCorrelation(Guid subInstanceId)
    {
        var correlation = FindCorrelationBySubInstanceId(subInstanceId);
        if (correlation == null || !correlation.IsCompleted)
        {
            return null;
        }

        correlation.Revert();

        // If this is a SubFlow (blocking), set instance back to Busy
        if (correlation.SubFlowType.Equals(SubFlowType.SubFlow))
        {
            Busy();
        }

        return correlation;
    }

    public void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(key), InstanceConstants.MaxKeyLength);
    }

    /// <summary>
    /// Sets the instance stage label. Accepts null to clear.
    /// </summary>
    /// <param name="stage">Stage value (max <see cref="InstanceConstants.MaxStageLength"/> characters), or null.</param>
    public void SetStage(string? stage)
    {
        Stage = Check.Length(stage, nameof(stage), InstanceConstants.MaxStageLength);
    }

    private void SetState(string currentState)
    {
        CurrentState = Check.Length(currentState, nameof(currentState), StateConstants.MaxKeyLength);
    }

    /// <summary>
    /// Sets the effective state (external world state).
    /// Called when state changes or when SubFlow state is propagated to parent.
    /// </summary>
    /// <param name="effectiveState">The new effective state</param>
    public void SetEffectiveState(string effectiveState)
    {
        EffectiveState = Check.Length(effectiveState, nameof(effectiveState), StateConstants.MaxKeyLength);
    }

    /// <summary>
    /// Propagates the EffectiveState to parent instance.
    /// Updates this instance's EffectiveState and publishes an event to notify parent if this is a SubFlow.
    /// This enables recursive propagation of EffectiveState up the parent chain.
    /// </summary>
    /// <param name="effectiveState">The new effective state to propagate</param>
    /// <param name="stateType">The type of the new effective state</param>
    /// <param name="stateSubType">The subtype of the new effective state</param>
    /// <remarks>
    /// This method does NOT change CurrentState - only EffectiveState is updated.
    /// This method does NOT change Status - status management happens in ChangeState only.
    /// Used by parent instances to reflect the deepest active SubFlow's state.
    /// The recursion happens through event-driven propagation:
    /// 1. Child updates its EffectiveState
    /// 2. If child is also a SubFlow, it publishes event to its parent
    /// 3. Parent receives event and calls this method again
    /// 4. Chain continues until root parent is reached
    /// 
    /// Idempotency: If EffectiveState already matches the target state and types, no update or event is triggered.
    /// This prevents duplicate events and unnecessary processing.
    /// </remarks>
    public void PropagateEffectiveStateToParent(string effectiveState, StateType stateType, StateSubType stateSubType)
    {
        var currentEffectiveState = GetEffectiveState;
        
        // Idempotency: If already at this state with same type/subtype, skip update
        if (currentEffectiveState == effectiveState 
            && EffectiveStateType == stateType 
            && EffectiveStateSubType == stateSubType)
        {
            return;
        }
        
        // Update EffectiveState with type and subtype
        SetEffectiveState(effectiveState);
        EffectiveStateType = stateType;
        EffectiveStateSubType = stateSubType;
        
        // IMPORTANT: Do NOT modify Status here - status management happens in ChangeState only
        
        // If this instance is also a SubFlow, propagate upward to its parent
        if (IsSubFlow)
        {
            PublishSubStateChangedEvent(currentEffectiveState, effectiveState);
        }
    }

    /// <summary>
    /// Publishes an event to notify the parent instance about SubFlow state change.
    /// This enables cross-domain communication for state synchronization.
    /// </summary>
    /// <param name="previousState">The previous state before the change</param>
    /// <param name="newState">The new state after the change</param>
    private void PublishSubStateChangedEvent(string previousState, string newState)
    {
        var contractInfo = ExtraProperties.ToSubFlowContractInfo();
        if (contractInfo.Id != Guid.Empty)
        {
            var rootId = this.GetRootInstanceId();
            AddDistributedEvent(new InstanceSubStateChangedEvent
            {
                ParentInstanceId = contractInfo.Id,
                SubInstanceId = Id,
                Domain = contractInfo.Domain,
                Flow = contractInfo.Flow,
                Version = contractInfo.Version,
                NewState = newState,
                PreviousState = previousState,
                NewStateType = (int)(EffectiveStateType ?? StateType.Intermediate),
                NewStateSubType = (int)(EffectiveStateSubType ?? StateSubType.None),
                ChangedAt = DateTime.UtcNow,
                RootInstanceId = rootId != Id ? rootId : (Guid?)null
            });
        }
    }

    public void ChangeState(State state)
    {
        var previousState = GetCurrentState;
        SetState(state.Key);

        CurrentStateType = state.StateType;
        CurrentStateSubType = state.SubType;

        // Domain Logic: Update EffectiveState with type and subtype if no active SubFlow
        if (!HasActiveSubFlow)
        {
            SetEffectiveState(state.Key);
            EffectiveStateType = state.StateType;
            EffectiveStateSubType = state.SubType;
        }
        
        // Domain Logic: Automatically set Status to Busy for Busy subtype states
        if (state.SubType == StateSubType.Busy && !IsCompleted)
        {
            Status = InstanceStatus.Busy;
        }

        // Domain Logic: Arm the SubFlow state notification — do NOT publish it here.
        // A same-state change ($self target) is not a state change; arming it would emit a
        // sub:state-changed with previous == new on every self transition.
        //
        // The event is COALESCED to the activation episode's rest point (see
        // PublishPendingSubStateChange). An auto-chain crossing A→B→C→D is one episode with one
        // observable outcome, D; the parent can act on nothing in between, because the chain has
        // not stopped. Emitting per hop made this the runtime's highest-volume signal — measured at
        // 6 facts per 908 ms chain against a ~1.3 s delivery, with 7.5% of deliveries writing
        // nothing at the receiver — and moved the parent's state-function ETag on every hop, so a
        // long-polling client woke for states it could not use.
        if (IsSubFlow && !string.Equals(previousState, state.Key, StringComparison.Ordinal))
        {
            _pendingSubStateChangeFrom ??= previousState;
            _hasPendingSubStateChange = true;
        }
    }

    /// <summary>
    /// Publishes the coalesced <c>sub:state-changed</c> for the activation episode that just ended,
    /// if any state change happened during it. Called once, at the episode's REST POINT — the
    /// instance became Active, reached a finish state, or deliberately rests Busy (a parked
    /// auto-gate, a Busy-subtype state). Callers must invoke it inside the unit of work that
    /// persists the state, so the event and the state it describes commit together.
    /// <para>
    /// The instance's own creation is its own such point: it is pre-positioned into the initial
    /// state and committed in a unit of work that has no settlement, so
    /// <c>InstanceCommandAppService</c> flushes it there. That matters for a child whose start
    /// transition targets its own initial state — nothing moves afterwards, so this is the only
    /// notification the parent ever gets for it.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when an event was published.</returns>
    public bool PublishPendingSubStateChange()
    {
        if (!_hasPendingSubStateChange)
            return false;

        var previousState = _pendingSubStateChangeFrom ?? string.Empty;
        _pendingSubStateChangeFrom = null;
        _hasPendingSubStateChange = false;

        if (!IsSubFlow)
            return false;

        // The chain came back to where it started: nothing was published in between, so from the
        // parent's point of view nothing happened.
        var newState = GetCurrentState;
        if (string.Equals(previousState, newState, StringComparison.Ordinal))
            return false;

        PublishSubStateChangedEvent(previousState, newState);
        return true;
    }

    public void AddTags(string[]? tags)
    {
        tags ??= [];

        Tags.RemoveAll(existingTag => !tags.Contains(existingTag));

        foreach (var tag in tags)
        {
            if (!Tags.Contains(tag))
            {
                Tags.Add(tag);
            }
        }
    }

    /// <summary>
    /// Accepts a row the <see cref="IInstanceDataWriteService"/> just persisted (or found) into
    /// this aggregate's in-memory data list, keeping the single-latest invariant. Id-idempotent:
    /// EF relationship fixup may have already attached the row when the service shares this
    /// aggregate's DbContext — a second accept is a no-op. Snapshot aggregates receive a
    /// detached copy from the caller, never the tracked entity itself.
    /// </summary>
    internal void AcceptPersistedData(InstanceData row)
    {
        lock (_dataListLock)
        {
            if (_dataList.Any(d => d.Id == row.Id))
            {
                if (row.IsLatest)
                {
                    foreach (var other in _dataList.Where(d => d.IsLatest && d.Id != row.Id))
                    {
                        other.MarkAsNotLatest();
                    }
                }

                return;
            }

            if (row.IsLatest)
            {
                foreach (var other in _dataList.Where(d => d.IsLatest))
                {
                    other.MarkAsNotLatest();
                }
            }

            _dataList.Add(row);
        }
    }

    /// <summary>
    /// Finds instance data by version.
    /// Delegates version resolution to <see cref="InstanceDataVersionComparer.FindBestMatch"/> for consistency.
    /// </summary>
    /// <param name="version">Version string to search for (null, empty, or "latest" returns the highest version)</param>
    /// <returns>The matching InstanceData or null if not found</returns>
    /// <remarks>
    /// Supports multiple version formats:
    /// <list type="bullet">
    ///     <item><description>null/empty or "latest": Returns the highest available version</description></item>
    ///     <item><description>Exact match: "1.0.0-pkg.1.17.0+account" or "1.0.0-alpha.1-pkg.1.17.0+account"</description></item>
    ///     <item><description>Artifact version only: "1.0.0" or "1.0.0-alpha.1" → finds highest pkg version for that artifact</description></item>
    ///     <item><description>Partial version: "1.0" → finds highest version among all 1.0.x versions</description></item>
    ///     <item><description>Major-only version: "1" → finds highest version among all 1.x.x versions</description></item>
    /// </list>
    /// </remarks>
    public InstanceData? FindData(string? version)
    {
        lock (_dataListLock)
        {
            if (_dataList.Count == 0)
                return null;

            // Delegate version resolution to centralized FindBestMatch
            var availableVersions = _dataList.Select(d => d.Version);
            var bestVersion = InstanceDataVersionComparer.FindBestMatch(availableVersions, version);

            if (string.IsNullOrEmpty(bestVersion))
            {
                // Latest-only aggregates hold a single row: a miss for an explicit version is
                // ambiguous ("does not exist" vs "not loaded") — fail fast instead of lying.
                // A hit is always correct (the loaded row is the global highest version).
                if (IsDataPartiallyLoaded && !IsLatestRequest(version))
                {
                    throw new InvalidOperationException(
                        $"Cannot resolve data version '{version}' on instance '{Id}': the " +
                        "aggregate was loaded latest-only. Load the instance with full data " +
                        "history for explicit-version reads.");
                }

                return null;
            }

            // Resolve the selected version back to InstanceData
            // If multiple entries exist with the same version, return the highest by VersionNo
            return _dataList
                .Where(d => d.Version == bestVersion)
                .OrderByDescending(d => d, InstanceDataVersionComparer.Instance)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Returns whether a version request means "the latest" (null/empty or the literal
    /// <c>latest</c>), which a latest-only loaded aggregate can always answer.
    /// </summary>
    private static bool IsLatestRequest(string? version) =>
        string.IsNullOrWhiteSpace(version)
        || string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all history entries for a specific version
    /// </summary>
    public IEnumerable<InstanceData> GetVersionHistory(string version)
    {
        lock (_dataListLock)
        {
            if (IsDataPartiallyLoaded)
            {
                throw new InvalidOperationException(
                    $"Cannot enumerate version history on instance '{Id}': the aggregate was " +
                    "loaded latest-only. Use the repository full-history API instead.");
            }

            return _dataList
                .Where(d => d.Version == version)
                .OrderBy(d => d.VersionNo)
                .ToList();
        }
    }

    /// <summary>
    /// Gets the latest data for a specific version
    /// </summary>
    public InstanceData? GetLatestDataForVersion(string version)
    {
        lock (_dataListLock)
        {
            return _dataList
                .Where(d => d.Version == version)
                .OrderByDescending(d => d.VersionNo)
                .FirstOrDefault();
        }
    }
}
