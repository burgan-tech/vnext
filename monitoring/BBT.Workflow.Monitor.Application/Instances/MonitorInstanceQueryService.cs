using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.Results;
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
    IInstanceTaskRepository instanceTaskRepository)
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
    public async Task<Result<MonitorInstanceHistoryResponse>> GetInstanceHistoryAsync(
        MonitorGetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        if (instance is null)
            return Result<MonitorInstanceHistoryResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var transitions = await instanceTransitionRepository.GetByInstanceIdAsReadOnlyAsync(
            instance.Id, cancellationToken);

        var items = transitions.Select(t => new MonitorTransitionItem
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
        }).ToList();

        return Result<MonitorInstanceHistoryResponse>.Ok(new MonitorInstanceHistoryResponse
        {
            Transitions = items
        });
    }

    /// <inheritdoc />
    public async Task<Result<List<MonitorInstanceTaskResponse>>> GetInstanceTaskAsync(
        MonitorGetInstanceTaskInput input,
        CancellationToken cancellationToken = default)
    {
        var tasks = await instanceTaskRepository.GetByTransitionIdAsync(
            input.TransitionId, cancellationToken);

        var items = tasks.Select(t => new MonitorInstanceTaskResponse
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
        }).ToList();

        return Result<List<MonitorInstanceTaskResponse>>.Ok(items);
    }

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
