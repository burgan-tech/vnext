using System.Text.Json;
using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Instances.DTOs;
using WorkflowTaskStatus = BBT.Workflow.Definitions.TaskStatus;

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
    IInstanceActionRepository actionRepository,
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
    public async Task<Result<MonitorPagedResponse<object>>> GetInstancesAsync(
        MonitorGetInstancesInput input,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateQueryParameters(input);
        if (validationError is { } error)
            return Result<MonitorPagedResponse<object>>.Fail(error);

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
                return new MonitorPagedResponse<object>
                {
                    Items = result.Groups.Cast<object>().ToList()
                };

            var items = result.PagedList.Items
                .Select(i => (object)MapToResponse(i, input.Domain))
                .ToList();

            return new MonitorPagedResponse<object>
            {
                Pagination = new MonitorPaginationInfo
                {
                    Page = result.PagedList.CurrentPage,
                    PageSize = result.PagedList.PageSize,
                    HasNext = result.PagedList.HasNext
                },
                Items = items
            };
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

        if (!string.IsNullOrWhiteSpace(input.Version))
        {
            var match = instance.DataList
                .FirstOrDefault(d => string.Equals(d.Version, input.Version, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return Result<MonitorInstanceDataResponse>.Fail(
                    Error.NotFound("instance.dataVersionNotFound",
                        $"Data version '{input.Version}' not found for instance '{input.Instance}'."));

            return Result<MonitorInstanceDataResponse>.Ok(new MonitorInstanceDataResponse
            {
                Data = match.Data.JsonElement
            });
        }

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

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceViewResponse?>> GetInstanceViewAsync(
        MonitorGetInstanceViewInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorInstanceViewResponse?>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, instance.Flow!, input.Version ?? instance.FlowVersion, cancellationToken);
        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorInstanceViewResponse?>.Fail(
                Error.NotFound("workflow.notFound", $"Workflow '{instance.Flow}' definition not found."));

        ViewDefinition? viewDef;
        if (!string.IsNullOrWhiteSpace(input.TransitionKey))
        {
            var transition = flow.States.SelectMany(s => s.Transitions)
                .Concat(flow.SharedTransitions)
                .FirstOrDefault(t => string.Equals(t.Key, input.TransitionKey, StringComparison.OrdinalIgnoreCase));
            if (transition is null)
                return Result<MonitorInstanceViewResponse?>.Fail(
                    Error.NotFound("transition.notFound", $"Transition '{input.TransitionKey}' not found."));
            viewDef = transition.View;
        }
        else
        {
            var currentStateDef = flow.States
                .FirstOrDefault(s => string.Equals(s.Key, instance.CurrentState, StringComparison.OrdinalIgnoreCase));
            viewDef = currentStateDef?.View;
        }

        if (viewDef is null)
            return Result<MonitorInstanceViewResponse?>.Ok(null);

        var selection = ViewSelector.Select(viewDef);
        var response = new MonitorInstanceViewResponse();

        if (selection.Default is { } def)
        {
            var viewResult = await componentCacheStore.GetViewAsync(
                input.Domain, def.View.Key, def.View.Version, cancellationToken);
            if (viewResult.IsSuccess && viewResult.Value is { } view)
            {
                response.ViewKey = view.Key;
                response.ViewType = view.Type.ToString();
                response.Display = view.Display;
                response.Labels = (view.Labels ?? [])
                    .Select(l => new MonitorLabel { Language = l.Language, Label = l.Label })
                    .ToList();
                response.Content = ToJsonElement(view.GetContentAsTyped());
            }
            else
            {
                response.ViewKey = def.View.Key;
            }
        }

        return Result<MonitorInstanceViewResponse?>.Ok(response);
    }

    private static JsonElement? ToJsonElement(object? typedContent)
    {
        if (typedContent is null) return null;
        if (typedContent is JsonElement je) return je;
        return JsonSerializer.SerializeToElement(typedContent, JsonSerializerConstants.JsonOptions);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceTaskListResponse>> GetInstanceTaskListAsync(
        MonitorGetInstanceTasksInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorInstanceTaskListResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var rows = await instanceTaskRepository.GetByInstanceIdAsync(instance.Id, cancellationToken);

        var items = rows.Select(MapToTaskListItem).ToList();
        return Result<MonitorInstanceTaskListResponse>.Ok(new MonitorInstanceTaskListResponse
        {
            Items = items,
            Total = items.Count
        });
    }

    /// <inheritdoc />
    public async Task<Result<MonitorTaskDetailResponse>> GetInstanceTaskDetailAsync(
        MonitorGetInstanceTaskDetailInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorTaskDetailResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var rows = await instanceTaskRepository.GetByInstanceIdAsync(instance.Id, cancellationToken);
        var row = rows.FirstOrDefault(r => r.Task.Id == input.TaskId);
        if (row is null)
            return Result<MonitorTaskDetailResponse>.Fail(
                Error.NotFound("task.notFound", $"Task '{input.TaskId}' not found for instance '{input.Instance}'."));

        // Best-effort: look up definition and trigger context using the instance's flow version
        MonitorTaskDefinitionInfo? definitionInfo = null;
        MonitorTaskTriggerContext? triggerContext = null;

        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, instance.Flow!, instance.FlowVersion, cancellationToken);

        if (flowResult.IsSuccess && flowResult.Value is { } flow)
        {
            triggerContext = TaskTriggerContextResolver.Resolve(
                flow, row.TransitionKey, row.FromState, row.ToState, row.Task.TaskId);

            var taskResult = await componentCacheStore.GetTaskAsync(
                input.Domain, row.Task.TaskId, null, cancellationToken);

            if (taskResult.IsSuccess && taskResult.Value is { } taskDef)
            {
                definitionInfo = new MonitorTaskDefinitionInfo
                {
                    Key = taskDef.Key,
                    Type = taskDef.Type,
                    Version = taskDef.Version,
                    Config = taskDef.Config.ValueKind == System.Text.Json.JsonValueKind.Undefined
                        ? null
                        : taskDef.Config
                };
            }
        }

        var actions = await actionRepository.GetByTaskIdAsync(row.Task.Id, cancellationToken);
        return Result<MonitorTaskDetailResponse>.Ok(MapToTaskDetail(row, definitionInfo, triggerContext, actions));
    }

    private static MonitorTaskListItem MapToTaskListItem(InstanceTaskRow row)
    {
        var task = row.Task;
        long? durationMs = task.FinishedAt.HasValue
            ? (long)(task.FinishedAt.Value - task.StartedAt).TotalMilliseconds
            : null;

        return new MonitorTaskListItem
        {
            Id = task.Id,
            TaskDefinitionKey = task.TaskId,
            Status = task.Status.ToString(),
            BusinessStatus = task.BusinessStatus.ToString(),
            StartedAt = task.StartedAt,
            FinishedAt = task.FinishedAt,
            DurationMs = durationMs
        };
    }

    private static MonitorTaskDetailResponse MapToTaskDetail(
        InstanceTaskRow row,
        MonitorTaskDefinitionInfo? definitionInfo,
        MonitorTaskTriggerContext? triggerContext,
        List<InstanceAction> actions)
    {
        var task = row.Task;
        long? durationMs = task.FinishedAt.HasValue
            ? (long)(task.FinishedAt.Value - task.StartedAt).TotalMilliseconds
            : null;

        return new MonitorTaskDetailResponse
        {
            Id = task.Id,
            TaskDefinitionKey = task.TaskId,
            Status = task.Status.ToString(),
            BusinessStatus = task.BusinessStatus.ToString(),
            StartedAt = task.StartedAt,
            FinishedAt = task.FinishedAt,
            DurationMs = durationMs,
            TriggerContext = triggerContext,
            Definition = definitionInfo,
            Input = task.Request.JsonElement,
            Output = task.Response.JsonElement,
            FaultedByTaskId = task.FaultedTaskId,
            Error = BuildError(task),
            InvocationResult = BuildInvocationResult(task),
            Actions = actions.Select(MapToActionItem).ToList()
        };
    }

    private static MonitorTaskActionItem MapToActionItem(InstanceAction action)
    {
        double? durationMs = action.FinishedAt.HasValue
            ? (action.FinishedAt.Value - action.StartedAt).TotalMilliseconds
            : null;

        return new MonitorTaskActionItem
        {
            Id = action.Id,
            Status = action.Status,
            StartedAt = action.StartedAt,
            FinishedAt = action.FinishedAt,
            DurationMs = durationMs,
            Detail = action.Detail?.JsonElement
        };
    }

    private static MonitorTaskInvocationResult? BuildInvocationResult(InstanceTask task)
    {
        var el = task.InvocationResult?.JsonElement;
        if (el is not { ValueKind: JsonValueKind.Object })
            return null;

        var root = el.Value;

        return new MonitorTaskInvocationResult
        {
            IsSuccess = root.TryGetProperty("isSuccess", out var isSuccessProp)
                        && isSuccessProp.ValueKind == JsonValueKind.True,
            StatusCode = root.TryGetProperty("statusCode", out var scProp)
                         && scProp.ValueKind == JsonValueKind.Number
                ? scProp.GetInt32()
                : null,
            ExecutionDurationMs = root.TryGetProperty("executionDurationMs", out var durProp)
                                  && durProp.ValueKind == JsonValueKind.Number
                ? durProp.GetInt64()
                : null,
            Body = root.TryGetProperty("body", out var bodyProp)
                ? ParseBodyElement(bodyProp)
                : null,
            Headers = root.TryGetProperty("headers", out var headersProp)
                      && headersProp.ValueKind == JsonValueKind.Object
                ? headersProp.EnumerateObject()
                    .ToDictionary(
                        p => p.Name,
                        p => p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString() ?? string.Empty
                            : p.Value.GetRawText())
                : null
        };
    }

    private static JsonElement? ParseBodyElement(JsonElement prop)
    {
        if (prop.ValueKind == JsonValueKind.Undefined)
            return null;

        if (prop.ValueKind == JsonValueKind.String)
        {
            var raw = prop.GetString();
            if (raw != null)
            {
                try { return JsonDocument.Parse(raw).RootElement.Clone(); }
                catch (JsonException) { }
            }
        }

        return prop.Clone();
    }

    private static MonitorTaskErrorInfo BuildError(InstanceTask task)
    {
        var invEl = task.InvocationResult?.JsonElement;

        bool invocationFailed = invEl is { ValueKind: JsonValueKind.Object }
            && invEl.Value.TryGetProperty("isSuccess", out var isSuccessProp)
            && isSuccessProp.ValueKind == JsonValueKind.False;

        bool hasError = task.Status == WorkflowTaskStatus.Faulted
            || task.BusinessStatus == BusinessStatus.Failed
            || invocationFailed;

        if (!hasError)
            return new MonitorTaskErrorInfo();

        // Prefer InvocationResult metadata (invocation-level error: HTTP, Dapr, script)
        if (invEl is { ValueKind: JsonValueKind.Object })
        {
            var root = invEl.Value;
            string? message = root.TryGetProperty("errorMessage", out var msgProp)
                              && msgProp.ValueKind == JsonValueKind.String
                ? msgProp.GetString()
                : null;
            string? exceptionType = null;
            string? stackTrace = null;

            if (root.TryGetProperty("metadata", out var metaProp)
                && metaProp.ValueKind == JsonValueKind.Object)
            {
                if (metaProp.TryGetProperty("ExceptionType", out var etProp)
                    && etProp.ValueKind == JsonValueKind.String)
                    exceptionType = etProp.GetString();

                if (metaProp.TryGetProperty("StackTrace", out var stProp)
                    && stProp.ValueKind == JsonValueKind.String)
                    stackTrace = stProp.GetString();
            }

            if (message != null || exceptionType != null)
                return new MonitorTaskErrorInfo
                {
                    Message = message,
                    ExceptionType = exceptionType,
                    StackTrace = stackTrace
                };
        }

        // Fallback: mapping error — InvokeAsync never reached; Response holds {"error": "..."}
        var respEl = task.Response.JsonElement;
        if (respEl.ValueKind == JsonValueKind.Object
            && respEl.TryGetProperty("error", out var errProp)
            && errProp.ValueKind == JsonValueKind.String)
        {
            return new MonitorTaskErrorInfo { Message = errProp.GetString() };
        }

        // Status indicates failure but no detail available
        return new MonitorTaskErrorInfo
        {
            Message = task.Status == WorkflowTaskStatus.Faulted
                ? "Task execution faulted."
                : invocationFailed
                    ? "Task invocation failed."
                    : "Task business logic failed."
        };
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
            }
        };
    }

    /// <inheritdoc />
    public async Task<Result<MonitorParentResponse>> GetInstanceParentAsync(
        MonitorGetParentInput input, CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorParentResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var correlation = await correlationRepository.FindBySubInstanceIdAsReadOnlyAsync(instance.Id, cancellationToken);
        if (correlation is null)
            return Result<MonitorParentResponse>.Ok(new MonitorParentResponse { Parent = null });

        var parent = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            correlation.ParentInstanceId.ToString(), cancellationToken);

        return Result<MonitorParentResponse>.Ok(new MonitorParentResponse
        {
            Parent = new MonitorParentItem
            {
                ParentInstanceId = correlation.ParentInstanceId,
                Key              = parent?.Key,
                Flow             = parent?.Flow,
                Domain           = input.Domain,
                ParentState      = correlation.ParentState,
                CorrelationType  = correlation.SubFlowType?.Code
            }
        });
    }
}
