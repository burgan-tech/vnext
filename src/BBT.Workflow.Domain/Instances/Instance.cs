using BBT.Aether;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Aspects;
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
    /// Durable ownership token for an in-flight auto-chain. While set (and the instance is Busy),
    /// only transitions carrying the matching token (the chain's own continuations) are admitted;
    /// foreign transitions are rejected. Reserved transitions (cancel/timeout) are exempt.
    /// Replaces a long-held distributed lock for chain ownership (transition-per-job). Null when idle.
    /// </summary>
    public Guid? ChainToken { get; private set; }

    /// <summary>
    /// Last heartbeat of the in-flight auto-chain (UTC). Refreshed when the chain begins and on
    /// each per-transition commit. Used by the stuck-Busy reaper (S7) to detect chains that own a
    /// Busy instance but have no live/pending job. Null when idle.
    /// </summary>
    public DateTime? ChainHeartbeatAt { get; private set; }

    /// <summary>
    /// Durable resume point (S8): the last committed lifecycle step order within the in-flight
    /// transition. On crash-resume the pipeline restarts from the next step rather than the
    /// beginning, and already-committed remote task journal rows (InstanceTask) are bypassed,
    /// avoiding duplicate irreversible side effects. Null when no transition is mid-flight.
    /// </summary>
    public int? ResumePointStepOrder { get; private set; }

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

    private List<InstanceIncident> _incidents = new();

    /// <summary>
    /// Error boundary incidents recorded for this instance.
    /// Stored as JSONB; pruned to <see cref="InstanceIncident.MaxRetainedIncidents"/> entries.
    /// Internal to prevent Aether's TrackRelatedEntities from discovering via reflection.
    /// </summary>
    internal IReadOnlyCollection<InstanceIncident> Incidents => _incidents.AsReadOnly();

    /// <summary>
    /// Indicates whether the instance has at least one unresolved incident.
    /// Backed by a stored generated column with partial B-tree index for efficient querying.
    /// </summary>
    public bool HasActiveIncident => _incidents.Any(i => !i.IsResolved);

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

    /// <summary>
    /// Latest data
    /// </summary>
    public dynamic? Data
    {
        get
        {
            lock (_dataListLock)
            {
                return _dataList.OrderByDescending(x => x, InstanceDataVersionComparer.Instance).FirstOrDefault()
                    ?.Attributes;
            }
        }
    }

    public InstanceData? LatestData
    {
        get
        {
            lock (_dataListLock)
            {
                return _dataList.OrderByDescending(x => x, InstanceDataVersionComparer.Instance).FirstOrDefault();
            }
        }
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
            ExtraProperties = new ExtraPropertyDictionary(ExtraProperties)
        };

        foreach (var data in _dataList)
        {
            snapshot._dataList.Add(data.CreateSnapshot());
        }

        foreach (var correlation in _childCorrelations)
        {
            snapshot._childCorrelations.Add(correlation.CreateSnapshot());
        }

        snapshot._incidents = _incidents.ToList();

        return snapshot;
    }

    /// <summary>
    /// Completes the instance and publishes completion cleanup event.
    /// Sets the instance status to Completed and records the completion time.
    /// </summary>
    /// <param name="domain">The domain of the instance.</param>
    public void Complete(string domain)
    {
        Status = InstanceStatus.Completed;
        ChainToken = null;
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
                    RootInstanceId = rootId != Id ? rootId : (Guid?)null
                });
            }
        }
    }

    /// <summary>
    /// Marks the instance as faulted and publishes fault cleanup event.
    /// Also propagates fault downward to active SubFlow children and upward to parent (if this is a SubFlow).
    /// Correlations are intentionally kept open (not completed) so retry can cascade through them.
    /// </summary>
    /// <param name="domain">The domain of the instance.</param>
    public void Fault(string domain)
    {
        Status = InstanceStatus.Faulted;
        ChainToken = null;
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
                RootInstanceId = rootId != Id ? rootId : (Guid?)null
            });
        }

        // Upward: notify parent if this instance is a blocking SubFlow
        if (IsSubFlow)
        {
            var activeIncident = _incidents.LastOrDefault(i => !i.IsResolved);
            var latestData = LatestData;
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
                    RootInstanceId = rootId != Id ? rootId : (Guid?)null
                });
            }
        }
    }
    /// <summary>
    /// Unfaults the instance, allowing it to be retried.
    /// Changes the status from Faulted to Active, clears completion time,
    /// and resolves the active incident.
    /// </summary>
    /// <returns>True if the instance was successfully unfaulted, false if it was not in Faulted state.</returns>
    public bool Unfault()
    {
        if (!Status.Equals(InstanceStatus.Faulted))
            return false;
 
        Status = InstanceStatus.Active;
        CompletedAt = null;
        Duration = null;
        ResolveActiveIncident();
        return true;
    }

    /// <summary>
    /// Records an error boundary incident on this instance.
    /// Prunes oldest resolved incidents to stay within <see cref="InstanceIncident.MaxRetainedIncidents"/>.
    /// </summary>
    public void AddIncident(InstanceIncident incident)
    {
        _incidents.Add(incident);
        PruneIncidents();
    }

    /// <summary>
    /// Resolves the most recent unresolved incident (if any).
    /// Called on successful retry or when an error-boundary transition completes.
    /// </summary>
    public void ResolveActiveIncident()
    {
        var active = _incidents.LastOrDefault(i => !i.IsResolved);
        active?.Resolve();
    }
    /// <summary>
    /// Cancels the instance and publishes a cancellation event.
    /// Sets the instance status to Canceled and records the completion time.
    /// </summary>
    /// <param name="domain">The domain of the instance.</param>
    public void Cancel(string domain)
    {
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

        foreach (var correlation in ActiveCorrelations)
        {
            correlation.Completed();
            AddDistributedEvent(new ChildSubflowCancelRequestedEvent
            {
                ParentInstanceId = correlation.ParentInstanceId,
                InstanceId = correlation.SubFlowInstanceId,
                Domain = correlation.SubFlowDomain,
                Flow = correlation.SubFlowName,
                CompletedAt = correlation.CompletedAt!.Value,
                Version = correlation.SubFlowVersion,
                RootInstanceId = rootId != Id ? rootId : (Guid?)null
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
    /// Begins an auto-chain: marks the instance Busy and stamps the durable ownership token.
    /// No-op if the instance is already completed.
    /// </summary>
    /// <param name="token">The chain ownership token to stamp.</param>
    public void BeginChain(Guid token)
    {
        if (IsCompleted)
            return;

        Status = InstanceStatus.Busy;
        ChainToken = token;
        ChainHeartbeatAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Refreshes the chain heartbeat (called on each per-transition commit while the chain owns
    /// the instance). No-op when there is no active chain token.
    /// </summary>
    public void TouchChainHeartbeat()
    {
        if (ChainToken.HasValue)
            ChainHeartbeatAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Records the last committed lifecycle step order for crash-resume (S8).
    /// </summary>
    public void SetResumePoint(int stepOrder) => ResumePointStepOrder = stepOrder;

    /// <summary>
    /// Clears the durable resume point (called at transition finalize so it never leaks into the next transition).
    /// </summary>
    public void ClearResumePoint() => ResumePointStepOrder = null;

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
    /// Returns whether the supplied token matches the instance's current chain ownership token.
    /// </summary>
    public bool MatchesChain(Guid token) => ChainToken.HasValue && ChainToken.Value == token;

    /// <summary>
    /// Clears the chain ownership token + heartbeat (chain complete / instance returning to a resting state).
    /// </summary>
    public void EndChain()
    {
        ChainToken = null;
        ChainHeartbeatAt = null;
    }

    /// <summary>
    /// Sets the instance status to Active.
    /// This is typically called when a transition processing is completed successfully.
    /// </summary>
    public void Active()
    {
        if (IsCompleted)
            return;

        Status = InstanceStatus.Active;
        ChainToken = null;
        ChainHeartbeatAt = null;
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
        var correlation = FindCorrelationBySubInstanceId(subInstanceId);
        if (correlation == null || correlation.IsCompleted)
        {
            return null;
        }

        correlation.Completed();

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

        // Domain Logic: Publish state change event if this is a SubFlow
        if (IsSubFlow)
        {
            PublishSubStateChangedEvent(previousState, state.Key);
        }
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

    [SchemaValidation]
    public InstanceData AddDataWithVersion(Guid id, JsonData inputData, string version, bool ignoreSameData = true)
    {
        lock (_dataListLock)
        {
            var latestData = _dataList.OrderByDescending(x => x, InstanceDataVersionComparer.Instance).FirstOrDefault();
            if (ignoreSameData && latestData?.HasSameData(inputData) == true)
            {
                // Data hasn't changed, return the existing latest data
                return latestData;
            }

            // Mark previous latest as not latest
            if (latestData != null)
            {
                latestData.MarkAsNotLatest();
            }

            var newData = new InstanceData(
                id,
                Id,
                version,
                inputData,
                true,
                GetNextHistorySequence(version)
            );
            _dataList.Add(newData);
            return newData;
        }
    }

    [SchemaValidation]
    public InstanceData AddData(Guid id, JsonData inputData, VersionStrategy? versionStrategy = null)
    {
        lock (_dataListLock)
        {
            var lastData = _dataList.OrderByDescending(x => x, InstanceDataVersionComparer.Instance).FirstOrDefault();

            // If we have existing data, check if the new data is different
            if (lastData?.HasSameData(inputData) == true)
            {
                // Data hasn't changed, return the existing data
                return lastData;
            }

            InstanceData newData;
            if (lastData is null)
            {
                newData = new InstanceData(
                    id,
                    Id,
                    WorkflowConstants.DefaultVersion,
                    inputData,
                    true
                );
            }
            else
            {
                newData = lastData.NewVersion(
                    id,
                    inputData,
                    versionStrategy ?? VersionStrategy.None,
                    GetNextHistorySequence(lastData.Version)
                );
            }

            _dataList.Add(newData);
            return newData;
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
                return null;

            // Resolve the selected version back to InstanceData
            // If multiple entries exist with the same version, return the highest by HistorySequence
            return _dataList
                .Where(d => d.Version == bestVersion)
                .OrderByDescending(d => d, InstanceDataVersionComparer.Instance)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Gets the next history sequence for a specific version
    /// </summary>
    private int GetNextHistorySequence(string version)
    {
        return _dataList
            .Where(d => d.Version == version)
            .Select(d => d.HistorySequence)
            .DefaultIfEmpty(-1) //For an empty list, it returns -1, and by adding +1 it becomes 0"
            .Max() + 1;
    }

    /// <summary>
    /// Gets all history entries for a specific version
    /// </summary>
    public IEnumerable<InstanceData> GetVersionHistory(string version)
    {
        lock (_dataListLock)
        {
            return _dataList
                .Where(d => d.Version == version)
                .OrderBy(d => d.HistorySequence)
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
                .OrderByDescending(d => d.HistorySequence)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Removes oldest resolved incidents when the list exceeds <see cref="InstanceIncident.MaxRetainedIncidents"/>.
    /// Active (unresolved) incidents are never pruned.
    /// </summary>
    private void PruneIncidents()
    {
        while (_incidents.Count > InstanceIncident.MaxRetainedIncidents)
        {
            var oldestResolved = _incidents.FirstOrDefault(i => i.IsResolved);
            if (oldestResolved == null)
                break;
            _incidents.Remove(oldestResolved);
        }
    }
}
