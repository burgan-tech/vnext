using System.Text.RegularExpressions;
using BBT.Aether;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Policies;
using BBT.Workflow.Shared;
using DomainResult = BBT.Aether.Results.Result;

namespace BBT.Workflow.Instances;

/// <summary>
/// Instance
/// </summary>
public sealed class Instance : AggregateRoot<Guid>, IHasCreatedAt, IHasModifyTime, IHasExtraProperties
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
    /// Current state key
    /// </summary>
    public string? CurrentState { get; private set; }

    public string GetCurrentState => string.IsNullOrWhiteSpace(CurrentState) ? string.Empty : CurrentState;

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
            Duration = Duration,
            Tags = [.. Tags],
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

    public void Complete()
    {
        Status = InstanceStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        Duration = CompletedAt - CreatedAt;

        // Publish completion event for SubItems (SubFlow or SubProcess)
        if (IsSubItem)
        {
            var latestData = LatestData;
            var contractInfo = ExtraProperties.ToSubFlowContractInfo();
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

    public void Fault()
    {
        Status = InstanceStatus.Faulted;
        CompletedAt = DateTime.UtcNow;
        Duration = CompletedAt - CreatedAt;
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

    public void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(key), InstanceConstants.MaxKeyLength);
    }

    private void SetState(string currentState)
    {
        CurrentState = Check.Length(currentState, nameof(currentState), StateConstants.MaxKeyLength);
    }

    public void ChangeState(State state)
    {
        SetState(state.Key);
    }

    public void ChangeState(Transition transition)
    {
        if (WellKnownStateKeys.ReservedTargetKeys.Contains(transition.Target))
        {
            return;
        }

        SetState(transition.Target);
    }

    public void ChangeState(WorkflowTimeout timeout)
    {
        SetState(timeout.Target);
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
    /// Validates if a transition can be executed from the current state using Result Pattern.
    /// Delegates validation to the StateTransitionPolicy which checks transition rules and authorization.
    /// </summary>
    /// <param name="transition">The transition to validate</param>
    /// <param name="state">The current state</param>
    /// <param name="policy">The policy to use for validation</param>
    /// <param name="executionActor">The actor attempting to execute the transition</param>
    /// <returns>Result indicating whether the transition can be executed. On failure, contains detailed rule error information.</returns>
    public DomainResult CanExecuteTransition(Transition transition, State state, StateTransitionPolicy policy,
        ExecutionActor executionActor = ExecutionActor.User)
    {
        return policy.Validate(state, transition, executionActor);
    }

    public InstanceData AddDataWithVersion(Guid id, JsonData inputData, string version)
    {
        lock (_dataListLock)
        {
            var latestData = _dataList.OrderByDescending(x => x, InstanceDataVersionComparer.Instance).FirstOrDefault();
            if (latestData?.HasSameData(inputData) == true)
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
    /// Supports multiple version formats:
    /// <list type="bullet">
    ///     <item><description>Exact match: "1.0.0-pkg.1.17.0+account" or "1.0.0-alpha.1-pkg.1.17.0+account"</description></item>
    ///     <item><description>Artifact version only: "1.0.0" or "1.0.0-alpha.1" → finds highest pkg version for that artifact</description></item>
    ///     <item><description>Partial version: "1.0" → finds highest version among all 1.0.x versions</description></item>
    /// </list>
    /// </summary>
    /// <param name="version">Version string to search for</param>
    /// <returns>The matching InstanceData or null if not found</returns>
    public InstanceData? FindData(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        lock (_dataListLock)
        {
            // 1. Try exact match first
            var exactMatch = _dataList.FirstOrDefault(x => x.Version == version);
            if (exactMatch != null)
                return exactMatch;

            // 2. Check if this is a full artifact version (MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-PRERELEASE)
            //    Client sends only artifact version, we need to find the highest pkg version
            //    Supports: 1.0.0, 1.0.0-alpha.1, 1.0.0-beta, 1.0.0-rc.1, etc.
            var artifactMatch = Regex.Match(version, @"^(\d+\.\d+\.\d+(?:-[a-zA-Z0-9]+(?:\.[a-zA-Z0-9]+)*)?)$");
            if (artifactMatch.Success)
            {
                // Find all data entries that match this artifact version
                // They could be either:
                // - Simple format: "1.0.0" or "1.0.0-alpha.1" (exact match, already checked above)
                // - Extended format: "1.0.0-pkg.x.y.z+name" or "1.0.0-alpha.1-pkg.x.y.z+name"
                var artifactPrefix = $"{version}-pkg.";
                
                var matched = _dataList
                    .Where(d => d.Version.StartsWith(artifactPrefix, StringComparison.Ordinal))
                    .OrderByDescending(d => d, InstanceDataVersionComparer.Instance)
                    .FirstOrDefault();

                return matched;
            }

            // 3. Partial version: 1.0 → find the highest version among all 1.0.x versions
            var partialMatch = Regex.Match(version, @"^(\d+)\.(\d+)$");
            if (partialMatch.Success)
            {
                var prefix = $"{partialMatch.Groups[1].Value}.{partialMatch.Groups[2].Value}.";

                var matched = _dataList
                    .Where(d => d.Version.StartsWith(prefix, StringComparison.Ordinal) || 
                               InstanceDataVersionComparer.GetArtifactVersion(d.Version).StartsWith(prefix, StringComparison.Ordinal))
                    .OrderByDescending(d => d, InstanceDataVersionComparer.Instance)
                    .FirstOrDefault();

                return matched;
            }

            return null;
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