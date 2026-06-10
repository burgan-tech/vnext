using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Instances.DTOs;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Read-only aggregate query service for workflow instances, tailored for the vnext-forge monitoring UI.
/// All operations are non-tracking (AsNoTracking) and extension-free for maximum read performance.
/// </summary>
public sealed class MonitorInstanceQueryService(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceTaskRepository instanceTaskRepository,
    IComponentCacheStore componentCacheStore,
    IInstanceCorrelationRepository correlationRepository,
    ICurrentSchema currentSchema)
    : ApplicationService(serviceProvider), IMonitorInstanceQueryService
{
    /// <inheritdoc />
    public async Task<Result<MonitorInstanceResponse>> GetInstanceAsync(
        MonitorGetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        if (instance is null)
            return Result<MonitorInstanceResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        return Result<MonitorInstanceResponse>.Ok(MapToResponse(instance, input.Domain));
    }

    /// <inheritdoc />
    public async Task<Result<InstanceListWithGroupsResponse<MonitorInstanceResponse>>> GetInstancesAsync(
        MonitorGetInstancesInput input,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateQueryParameters(input);
        if (validationError is { } error)
            return Result<InstanceListWithGroupsResponse<MonitorInstanceResponse>>.Fail(error);

        return await ResultExtensions.TryAsync(async ct =>
        {
            var result = await instanceRepository.GetPagedResultsWithGroupsAsync(
                input.Page,
                input.PageSize,
                input.Filter,
                input.GroupBy,
                input.Aggregations,
                input.Sort,
                ct);

            if (result.Groups is { Count: > 0 })
                return InstanceListWithGroupsResponse<MonitorInstanceResponse>.FromGroups(result.Groups);

            var items = result.PagedList.Items
                .Select(i => MapToResponse(i, input.Domain))
                .ToList();

            var pagedList = new HateoasPagedList<MonitorInstanceResponse>(
                items,
                result.PagedList.CurrentPage,
                result.PagedList.PageSize,
                result.PagedList.HasNext);

            return InstanceListWithGroupsResponse<MonitorInstanceResponse>.FromPagedList(pagedList);
        }, cancellationToken);
    }

    /// <summary>
    /// Validates the GraphQL-style query parameters up front so that a malformed JSON filter
    /// returns HTTP 400 instead of being silently swallowed (which would return every instance).
    /// </summary>
    private static Error? ValidateQueryParameters(MonitorGetInstancesInput input)
    {
        return TryValidateJson(input.Filter, "filter", static f => GraphQLFilterParser.ParseFilter(f))
            ?? TryValidateJson(input.GroupBy, "groupBy", static g => GraphQLFilterParser.ParseGroupBy(g))
            ?? TryValidateJson(input.Aggregations, "aggregations", static a => GraphQLFilterParser.ParseAggregations(a));
    }

    private static Error? TryValidateJson(string? value, string parameterName, Action<string> parse)
    {
        if (string.IsNullOrWhiteSpace(value) || FilterFormatDetector.DetectFormat(value) != FilterFormat.GraphQL)
            return null;

        try
        {
            parse(value);
            return null;
        }
        catch (ArgumentException ex)
        {
            return Error.Validation(
                "instance.invalidFilter",
                $"The '{parameterName}' query parameter is not valid GraphQL JSON: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceDataResponse>> GetInstanceDataAsync(
        MonitorGetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        if (instance is null)
            return Result<MonitorInstanceDataResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var versionHistory = instance.DataList
            .OrderBy(d => d, InstanceDataVersionComparer.Instance)
            .Select(d => new MonitorDataVersion
            {
                Version = d.Version,
                EnteredAt = d.EnteredAt,
                Data = d.Data.JsonElement
            })
            .ToList();

        return Result<MonitorInstanceDataResponse>.Ok(new MonitorInstanceDataResponse
        {
            LatestData = instance.LatestData?.Data.JsonElement,
            VersionHistory = versionHistory
        });
    }

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceTimelineResponse>> GetInstanceTimelineAsync(
        MonitorGetInstanceTimelineInput input,
        CancellationToken cancellationToken = default)
    {
        if (input.TaskId is { } taskId)
        {
            if (taskId == Guid.Empty)
                return Result<MonitorInstanceTimelineResponse>.Fail(
                    Error.Validation("instance.invalidTaskId", "The 'taskId' query parameter must be a non-empty GUID."));

            var task = await instanceTaskRepository.GetByIdAsReadOnlyAsync(taskId, cancellationToken);
            if (task is null)
                return Result<MonitorInstanceTimelineResponse>.Fail(
                    Error.NotFound("task.notFound", $"Task '{taskId}' not found."));

            return Result<MonitorInstanceTimelineResponse>.Ok(new MonitorInstanceTimelineResponse
            {
                Task = MapTask(task)
            });
        }

        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        if (instance is null)
            return Result<MonitorInstanceTimelineResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var transitions = await instanceTransitionRepository.GetByInstanceIdAsReadOnlyAsync(
            instance.Id, cancellationToken);

        if (input.TransitionId is { } transitionId)
        {
            if (transitionId == Guid.Empty)
                return Result<MonitorInstanceTimelineResponse>.Fail(
                    Error.Validation("instance.invalidTransitionId", "The 'transitionId' query parameter must be a non-empty GUID."));

            var match = transitions.FirstOrDefault(t => t.Id == transitionId);
            if (match is null)
                return Result<MonitorInstanceTimelineResponse>.Fail(
                    Error.NotFound("transition.notFound", $"Transition '{transitionId}' not found for instance '{input.Instance}'."));

            var item = MapTransition(match);
            if (input.IncludeTasks)
            {
                var matchTasks = await instanceTaskRepository.GetByTransitionIdAsync(match.Id, cancellationToken);
                item.Tasks = matchTasks.Select(MapTask).ToList();
            }

            return Result<MonitorInstanceTimelineResponse>.Ok(new MonitorInstanceTimelineResponse
            {
                Transitions = [item]
            });
        }

        Dictionary<Guid, List<MonitorInstanceTaskResponse>> tasksByTransition = new();
        if (input.IncludeTasks)
        {
            foreach (var t in transitions)
            {
                var tasks = await instanceTaskRepository.GetByTransitionIdAsync(t.Id, cancellationToken);
                tasksByTransition[t.Id] = tasks.Select(MapTask).ToList();
            }
        }

        var items = transitions.Select(t =>
        {
            var item = MapTransition(t);
            item.Tasks = input.IncludeTasks && tasksByTransition.TryGetValue(t.Id, out var tl) ? tl : null;
            return item;
        }).ToList();

        return Result<MonitorInstanceTimelineResponse>.Ok(new MonitorInstanceTimelineResponse
        {
            Transitions = items
        });
    }

    private static MonitorTransitionItem MapTransition(InstanceTransition t) => new()
    {
        Id = t.Id,
        TransitionId = t.TransitionId,
        FromState = t.FromState,
        ToState = t.ToState,
        StartedAt = t.StartedAt,
        FinishedAt = t.FinishedAt,
        DurationSeconds = t.Duration?.TotalSeconds,
        TriggerType = t.TriggerType,
        CreatedBy = t.CreatedBy,
        CreatedByBehalfOf = t.CreatedByBehalfOf
    };

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceStateResponse>> GetInstanceStateAsync(
        MonitorGetInstanceStateInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorInstanceStateResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var available = new List<MonitorAvailableTransition>();
        StateType? stateType = instance.EffectiveStateType;
        StateSubType? stateSubType = instance.EffectiveStateSubType;

        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, instance.Flow!, instance.FlowVersion, cancellationToken);
        if (flowResult.IsSuccess && flowResult.Value is { } flow)
        {
            var currentStateDef = flow.States
                .FirstOrDefault(s => string.Equals(s.Key, instance.CurrentState, StringComparison.OrdinalIgnoreCase));
            if (currentStateDef is not null)
            {
                stateType = currentStateDef.StateType;
                stateSubType = currentStateDef.SubType;
            }

            var stateTransitions = currentStateDef?.Transitions ?? Enumerable.Empty<Transition>();
            available = stateTransitions
                .Concat(flow.SharedTransitions)
                .Select(t => new MonitorAvailableTransition
                {
                    Key = t.Key,
                    Target = t.Target,
                    TriggerType = t.TriggerType,
                    Roles = t.Roles.Count > 0 ? t.Roles.Select(r => r.Role).ToList() : null
                })
                .ToList();
        }

        return Result<MonitorInstanceStateResponse>.Ok(new MonitorInstanceStateResponse
        {
            CurrentState = instance.CurrentState,
            StateType = stateType,
            StateSubType = stateSubType,
            Status = instance.Status,
            EffectiveState = instance.EffectiveState,
            AvailableTransitions = available,
            ActiveCorrelations = instance.ActiveCorrelations
                .Select(c => new MonitorCorrelationInfo
                {
                    Id = c.Id,
                    ParentState = c.ParentState,
                    SubFlowInstanceId = c.SubFlowInstanceId,
                    SubFlowDomain = c.SubFlowDomain,
                    SubFlowName = c.SubFlowName,
                    SubFlowVersion = c.SubFlowVersion,
                    SubFlowType = c.SubFlowType.Code,
                    SubFlowCurrentState = c.SubFlowCurrentState
                })
                .ToList()
        });
    }

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceFaultResponse>> GetInstanceFaultsAsync(
        MonitorGetInstanceFaultsInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorInstanceFaultResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var transitions = await instanceTransitionRepository.GetByInstanceIdAsReadOnlyAsync(
            instance.Id, cancellationToken);

        var unfinished = transitions
            .Where(t => t.FinishedAt is null)
            .OrderByDescending(t => t.StartedAt)
            .FirstOrDefault();

        var faultedTasks = new List<MonitorInstanceTaskResponse>();
        if (unfinished is not null)
        {
            var tasks = await instanceTaskRepository.GetByTransitionIdAsync(unfinished.Id, cancellationToken);
            faultedTasks = tasks
                .Where(t => string.Equals(t.BusinessStatus.ToString(), "Failed", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(t.Status.ToString(), "Faulted", StringComparison.OrdinalIgnoreCase))
                .Select(MapTask)
                .ToList();
        }

        return Result<MonitorInstanceFaultResponse>.Ok(new MonitorInstanceFaultResponse
        {
            LastKnownState = instance.CurrentState,
            EffectiveState = instance.EffectiveState,
            Status = instance.Status,
            FaultedTransition = unfinished is null ? null : new MonitorFaultedTransition
            {
                Id = unfinished.Id,
                TransitionId = unfinished.TransitionId,
                FromState = unfinished.FromState,
                ToState = unfinished.ToState,
                StartedAt = unfinished.StartedAt,
                TriggerType = unfinished.TriggerType
            },
            FaultedTasks = faultedTasks
        });
    }

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceDataDiffResponse>> GetInstanceDataDiffAsync(
        MonitorGetInstanceDataDiffInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorInstanceDataDiffResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var fromData = instance.DataList.FirstOrDefault(d => d.Version == input.From);
        var toData = instance.DataList.FirstOrDefault(d => d.Version == input.To);
        if (fromData is null || toData is null)
            return Result<MonitorInstanceDataDiffResponse>.Fail(
                Error.NotFound("instance.dataVersionNotFound",
                    $"Data version '{(fromData is null ? input.From : input.To)}' not found."));

        var diff = JsonDataDiff.Compare(fromData.Data.JsonElement, toData.Data.JsonElement);

        return Result<MonitorInstanceDataDiffResponse>.Ok(new MonitorInstanceDataDiffResponse
        {
            FromVersion = input.From,
            ToVersion = input.To,
            Added = diff.Added.Select(f => new MonitorDiffField { Path = f.Path, Value = f.Value }).ToList(),
            Removed = diff.Removed.Select(f => new MonitorDiffField { Path = f.Path, Value = f.Value }).ToList(),
            Changed = diff.Changed.Select(c => new MonitorDiffChange { Path = c.Path, OldValue = c.OldValue, NewValue = c.NewValue }).ToList(),
            UnchangedCount = diff.UnchangedCount
        });
    }

    private const int HierarchyMaxDepth = 20;

    /// <inheritdoc />
    public async Task<Result<MonitorHierarchyNode>> GetInstanceHierarchyAsync(
        MonitorGetInstanceHierarchyInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorHierarchyNode>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var root = new MonitorHierarchyNode
        {
            InstanceId = instance.Id,
            Key = instance.Key,
            Flow = instance.Flow,
            Domain = input.Domain,
            FlowVersion = instance.FlowVersion,
            CurrentState = instance.CurrentState,
            Status = instance.Status,
            IsCompleted = instance.CompletedAt is not null,
            CompletedAt = instance.CompletedAt,
            Schema = currentSchema.Name // root lives in the request-resolved schema
        };

        var visited = new HashSet<Guid> { instance.Id };

        async Task<List<MonitorHierarchyNode>> FetchChildren(MonitorHierarchyNode parent, int depth)
        {
            // Correlations are read in the PARENT's schema (assumption — verify at runtime).
            List<InstanceCorrelation> correlations;
            IDisposable? parentScope = string.IsNullOrWhiteSpace(parent.Schema) ? null : currentSchema.Use(parent.Schema!);
            try
            {
                correlations = await correlationRepository.GetByParentAsync(parent.InstanceId, cancellationToken);
            }
            finally
            {
                parentScope?.Dispose();
            }

            var nodes = new List<MonitorHierarchyNode>();
            foreach (var c in correlations)
            {
                // Child may live in a different schema. Assumption: schema name == SubFlowName (verify at runtime).
                var childSchema = string.IsNullOrWhiteSpace(c.SubFlowName) ? parent.Schema : c.SubFlowName;

                Instance? child;
                IDisposable? childScope = string.IsNullOrWhiteSpace(childSchema) ? null : currentSchema.Use(childSchema!);
                try
                {
                    child = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
                        c.SubFlowInstanceId.ToString(), cancellationToken);
                }
                finally
                {
                    childScope?.Dispose();
                }
                if (child is null) continue;

                nodes.Add(new MonitorHierarchyNode
                {
                    InstanceId = child.Id,
                    Key = child.Key,
                    Flow = child.Flow,
                    Domain = c.SubFlowDomain,
                    FlowVersion = child.FlowVersion,
                    CurrentState = child.CurrentState,
                    Status = child.Status,
                    SubFlowType = c.SubFlowType.Code,
                    ParentState = c.ParentState,
                    IsCompleted = child.CompletedAt is not null,
                    CompletedAt = child.CompletedAt,
                    Schema = childSchema
                });
            }
            return nodes;
        }

        await InstanceHierarchyBuilder.PopulateAsync(root, FetchChildren, HierarchyMaxDepth, visited);
        return Result<MonitorHierarchyNode>.Ok(root);
    }

    private static MonitorInstanceTaskResponse MapTask(InstanceTask t) => new()
    {
        Id = t.Id,
        TransitionId = t.TransitionId,
        TaskId = t.TaskId,
        Status = t.Status.ToString(),
        BusinessStatus = t.BusinessStatus.ToString(),
        StartedAt = t.StartedAt,
        FinishedAt = t.FinishedAt,
        DurationSeconds = t.Duration?.TotalSeconds,
        Request = t.Request.JsonElement,
        Response = t.Response.JsonElement
    };

    private static MonitorInstanceResponse MapToResponse(Instance instance, string domain)
    {
        return new MonitorInstanceResponse
        {
            Id = instance.Id,
            Key = instance.Key,
            Flow = instance.Flow,
            FlowVersion = instance.FlowVersion,
            Domain = domain,
            Tags = instance.Tags,
            Metadata = new MonitorInstanceMetadata
            {
                CurrentState = instance.CurrentState,
                EffectiveState = instance.EffectiveState,
                Status = instance.Status,
                EffectiveStateType = instance.EffectiveStateType,
                EffectiveStateSubType = instance.EffectiveStateSubType,
                CompletedAt = instance.CompletedAt,
                Duration = instance.Duration?.TotalSeconds,
                CreatedAt = instance.CreatedAt,
                ModifiedAt = instance.ModifiedAt,
                CreatedBy = instance.CreatedBy,
                CreatedByBehalfOf = instance.CreatedByBehalfOf,
                ModifiedBy = instance.ModifiedBy,
                ModifiedByBehalfOf = instance.ModifiedByBehalfOf
            },
            ActiveCorrelations = instance.ActiveCorrelations
                .Select(c => new MonitorCorrelationInfo
                {
                    Id = c.Id,
                    ParentState = c.ParentState,
                    SubFlowInstanceId = c.SubFlowInstanceId,
                    SubFlowDomain = c.SubFlowDomain,
                    SubFlowName = c.SubFlowName,
                    SubFlowVersion = c.SubFlowVersion,
                    SubFlowType = c.SubFlowType.Code,
                    SubFlowCurrentState = c.SubFlowCurrentState
                })
                .ToList()
        };
    }
}
