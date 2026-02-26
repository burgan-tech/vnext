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
        string? key
    ) : base(id)
    {
        IsTransient = true;
        CreatedAt = DateTime.UtcNow;
        Flow = Check.NotNullOrWhiteSpace(flow, nameof(Flow), WorkflowConstants.MaxKeyLength);
        Key = Check.Length(key, nameof(Key), InstanceConstants.MaxKeyLength);
        Status = InstanceStatus.Active;

        Tags = [];

        ExtraProperties = new ExtraPropertyDictionary();

        _dataList = [];
    }

    public static Instance Create(
        Guid id,
        string flow,
        string? key = null
    )
    {
        return new Instance(
            id,
            flow,
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
    /// Current state key - engine internal state (hidden from external world)
    /// </summary>
    public string? CurrentState { get; private set; }

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

    public string GetCurrentState => string.IsNullOrWhiteSpace(CurrentState) ? string.Empty : CurrentState;
    
    public string GetEffectiveState => string.IsNullOrWhiteSpace(EffectiveState) ? string.Empty : EffectiveState;

    /// <summary>
    /// Status
    /// </summary>
    public InstanceStatus Status { get; private set; }

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
        _childCorrelations.Where(p => !p.IsCompleted).ToList();

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
            Key = Key,
            Status = Status,
            CompletedAt = CompletedAt,
            CurrentState = CurrentState,
            EffectiveState = EffectiveState,
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
        CompletedAt = DateTime.UtcNow;
        Duration = CompletedAt - CreatedAt;

        // Publish cleanup event to cancel all scheduled jobs
        AddDistributedEvent(new InstanceCompletedCleanupEvent
        {
            InstanceId = Id,
            Domain = domain,
            Flow = Flow,
            CompletedAt = CompletedAt.Value
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
                    Duration = Duration
                });
            }
        }
    }

    /// <summary>
    /// Marks the instance as faulted and publishes fault cleanup event.
    /// Sets the instance status to Faulted and records the completion time.
    /// </summary>
    /// <param name="domain">The domain of the instance.</param>
    public void Fault(string domain)
    {
        Status = InstanceStatus.Faulted;
        CompletedAt = DateTime.UtcNow;
        Duration = CompletedAt - CreatedAt;
        
        // Publish cleanup event to cancel all scheduled jobs
        AddDistributedEvent(new InstanceFaultedCleanupEvent
        {
            InstanceId = Id,
            Domain = domain,
            Flow = Flow,
            FaultedAt = CompletedAt.Value
        });
    }
    /// <summary>
    /// Unfaults the instance, allowing it to be retried.
    /// Changes the status from Faulted to Active and clears completion time.
    /// </summary>
    /// <returns>True if the instance was successfully unfaulted, false if it was not in Faulted state.</returns>
    public bool Unfault()
    {
        if (!Status.Equals(InstanceStatus.Faulted))
            return false;
 
        Status = InstanceStatus.Active;
        CompletedAt = null;
        Duration = null;
        return true;
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
        AddDistributedEvent(new InstanceCanceledEvent
        {
            InstanceId = Id,
            Domain = domain,
            Flow = Flow,
            CanceledState = GetCurrentState,
            CanceledAt = CompletedAt.Value,
            Duration = Duration
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
                Version = correlation.SubFlowVersion
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
        var correlation = FindCorrelationBySubInstanceId(subInstanceId);
        if (correlation == null || correlation.IsCompleted)
        {
            return null;
        }

        correlation.Completed();
        
        // If this is a SubFlow (blocking), set instance to Active
        if (correlation.SubFlowType.Equals(SubFlowType.SubFlow))
        {
            Active();
        }

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
                ChangedAt = DateTime.UtcNow
            });
        }
    }

    public void ChangeState(State state)
    {
        var previousState = GetCurrentState;
        SetState(state.Key);

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
}