using System.Diagnostics;
using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;
using BBT.Workflow.Shared;
using BBT.Workflow.Telemetry;
using System.Text.Json;
using BBT.Aether.Application.Pagination;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.GraphQL.Validation;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Aether.MultiSchema;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace BBT.Workflow.Instances;

public sealed class InstanceQueryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceCorrelationRepository instanceCorrelationRepository,
    IInstanceJobRepository instanceJobRepository,
    IInstanceIncidentRepository instanceIncidentRepository,
    IInstanceTaskRepository instanceTaskRepository,
    IInstanceActionRepository instanceActionRepository,
    Execution.LongPoll.ILongPollInteractionGate longPollInteractionGate,
    IInstanceExtensionService instanceExtensionService,
    IScriptContextFactory scriptContextFactory,
    IInstanceQueryGateway instanceQueryGateway,
    IViewContentResolutionService viewContentResolutionService,
    ITaskConditionService taskConditionService,
    IUrlTemplateBuilder urlTemplateBuilder,
    ICurrentSchema currentSchema,
    ITransitionAuthorizationManager transitionAuthorizationManager,
    IRepresentationEtagService representationEtagService,
    ISchemaFieldFilterService schemaFieldFilterService,
    ICallerRoleResolver callerRoleResolver,
    IPaginationLinkGenerator paginationLinkGenerator,
    IOptions<InstanceFilteringOptions> instanceFilteringOptions,
    IOptions<HumanTask.HumanTaskFunctionOptions> humanTaskOptions,
    IAttributeIndexCatalog attributeIndexCatalog,
    Caching.IStateFunctionCache stateFunctionCache,
    Caching.IDataFunctionCache dataFunctionCache,
    Caching.IInstanceSchemaFunctionCache instanceSchemaFunctionCache,
    Caching.IHumanTaskFunctionCache humanTaskFunctionCache,
    HumanTask.HumanTaskDescentLimiter descentLimiter,
    ILogger<InstanceQueryAppService> logger)
    : ApplicationService(serviceProvider), IInstanceQueryAppService
{
    private static readonly ConcurrentDictionary<string, BuildGate> ActiveSubflowBuildGates = new();

    /// <summary>
    /// Converts a failed query validation into a 400-mapping <see cref="Error"/>, carrying every
    /// rejection reason so the caller can fix them all in one round trip.
    /// </summary>
    private static Error ToValidationError(FilterValidationResult validation)
    {
        var validationErrors = validation.Errors
            .Select(error => new System.ComponentModel.DataAnnotations.ValidationResult(
                error.Message,
                [error.Target ?? "filter"]))
            .ToList();

        return Error.Validation(
            validation.PrimaryErrorCode,
            validation.ToMessage(),
            validationErrors,
            validation.Errors[0].Target ?? "filter");
    }

    private IDisposable? BeginRootIdScopeIfSubflow(Instance instance)
    {
        var rootId = instance.GetRootInstanceId();
        if (rootId == instance.Id)
            return null;

        Activity.Current?.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
        Activity.Current?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
        return logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.RootInstanceId] = rootId
        });
    }

    /// <summary>
    /// Opens the per-request instance log scope for the read/function path (state, view, schema,
    /// data, get, history), mirroring the keys <c>TransitionExecutor.BuildLogScope</c> uses on the
    /// write path — so every poll log line is queryable by the REAL instance id even when the
    /// client addressed the instance by business key. Also stamps the matching Activity tags and
    /// composes the subflow root-id scope so callers make a single call.
    /// </summary>
    private IDisposable BeginInstanceScope(Instance instance)
    {
        var activity = Activity.Current;
        activity?.SetTag(TelemetryConstants.TagNames.InstanceId, instance.Id.ToString());
        if (instance.HasKey)
            activity?.SetTag(TelemetryConstants.TagNames.InstanceKey, instance.Key);
        activity?.SetTag(TelemetryConstants.TagNames.Flow, instance.Flow);
        activity?.SetTag(TelemetryConstants.TagNames.Domain, runtimeInfoProvider.Domain);

        var scope = logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = runtimeInfoProvider.Domain,
            [TelemetryConstants.TagNames.Flow] = instance.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = instance.FlowVersion,
            [TelemetryConstants.TagNames.InstanceId] = instance.Id,
            [TelemetryConstants.TagNames.InstanceKey] = instance.Key ?? "N/A"
        });
        var rootScope = BeginRootIdScopeIfSubflow(instance);
        return new CompositeScope(scope, rootScope);
    }

    private sealed class CompositeScope(IDisposable? first, IDisposable? second) : IDisposable
    {
        public void Dispose()
        {
            second?.Dispose();
            first?.Dispose();
        }
    }

    public async Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.Instance, input.Domain, input.Workflow);

        return await GetInstanceByIdOrKeyAsync(input.Instance, input.Version, cancellationToken)
            .MatchAsync(
                onSuccess: async instance =>
                {
                    using var instanceScope = BeginInstanceScope(instance);
                    var instanceData = instance.FindData(input.Version);
                    
                    var result = await BuildInstanceOutputAsync(
                        input.Domain,
                        input.Extensions,
                        input.Workflow,
                        instance,
                        instanceData,
                        ExtensionScope.GetInstance,
                        input.Headers,
                        input.QueryParameters,
                        cancellationToken);

                    // Propagate extension errors - fail-fast behavior
                    if (!result.IsSuccess)
                    {
                        return ConditionalResult<GetInstanceOutput>.Fail(result.Error);
                    }

                    var response = result.Value!;
                    response.Metadata.Incident = BuildIncidentInfo(instance, input.Domain, input.Workflow);
                    var entityEtag = instance.LatestData?.ETag ?? string.Empty;
                    response.EntityEtag = entityEtag;
                    var representationEtag = representationEtagService.Generate(response);

                    if (!string.IsNullOrEmpty(input.IfNoneMatch) && representationEtag.MatchesIfNoneMatch(input.IfNoneMatch))
                    {
                        return ConditionalResult<GetInstanceOutput>.NotModified();
                    }

                    response.ETag = representationEtag;
                    return ConditionalResult<GetInstanceOutput>.Success(response);
                },
                onFailure: error => ConditionalResult<GetInstanceOutput>.Fail(error));
    }

    public async Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default)
    {
        using var listActivity = InstanceReadActivityHelper.StartListPhase("request");
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.List, input.Domain, input.Workflow);

        // Validate before any query is built. A filter the runtime cannot honor must be rejected,
        // never silently ignored — ignoring it widens the result set instead of narrowing it.
        var validation = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Filter = input.Filter,
            Sort = input.Sort,
            GroupBy = input.GroupBy,
            Aggregations = input.Aggregations
        });

        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                logger.InstanceQueryParameterRejected(
                    input.Domain, input.Workflow, error.Target ?? "filter", error.Code, error.Message);
            }

            return Result<InstanceListWithGroupsResponse<GetInstanceOutput>>.Fail(
                ToValidationError(validation));
        }

        return await ResultExtensions.TryAsync(
            async ct =>
            {

                SchemaFilterContext? schemaContext = null;
                using (InstanceReadActivityHelper.StartListPhase("metadata"))
                {
                    // Resolve schema-driven filter/sort metadata from workflow's master schema
                    var flowResult = await componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, null, ct);
                    if (flowResult.IsSuccess && flowResult.Value?.Schema is not null)
                    {
                        var schemaResult = await componentCacheStore.GetSchemaAsync(flowResult.Value.Schema, ct);
                        if (schemaResult.IsSuccess)
                            schemaContext = SchemaFilterMetadataResolver.Resolve(schemaResult.Value!.Schema);
                    }

                    if (schemaContext != null)
                    {
                        var ready = !schemaContext.Fields.Values.Any(f => f.Indexed)
                            ? new HashSet<string>()
                            : await attributeIndexCatalog.GetReadyAsync(currentSchema.Name ?? input.Workflow, ct);
                        schemaContext = new SchemaFilterContext(schemaContext.Fields)
                        {
                            EnforceFiltering = instanceFilteringOptions.Value.EnforceMasterSchemaFiltering,
                            ReadyIndexes = ready
                        };
                    }
                }

                // Parse filter parameter - check if it's in GraphQLFilterRequest format
                string? groupBy = input.GroupBy;
                string? aggregations = input.Aggregations;

                // If filter is provided, check if it's GraphQLFilterRequest format
                Definitions.GraphQL.GraphQLFilterRequest? parsedRequest = null;
                if (!string.IsNullOrWhiteSpace(input.Filter) && string.IsNullOrWhiteSpace(groupBy))
                {
                    var filterString = input.Filter;
                    if (GraphQLFilterParser.TryParseRequest(filterString, out var request) && request != null)
                    {
                        parsedRequest = request;
                        // Apply sort from query param (overrides envelope orderBy when provided)
                        if (!string.IsNullOrWhiteSpace(input.Sort) && GraphQLFilterParser.ParseOrderBy(input.Sort) is
                                { } orderBy)
                        {
                            parsedRequest.OrderBy = orderBy;
                        }

                        if (request.GroupBy != null)
                        {
                            groupBy = JsonSerializer.Serialize(request.GroupBy, new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                WriteIndented = false
                            });
                        }

                        if (request.Aggregations != null)
                        {
                            aggregations = JsonSerializer.Serialize(request.Aggregations, new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                WriteIndented = false
                            });
                        }
                    }
                }

                // Use optimized path if we have a parsed request (avoids parse-serialize-parse cycle)
                HateoasPagedList<Instance> pagedList;
                List<GroupSummary>? groups;
                try
                {
                    using var queryActivity = InstanceReadActivityHelper.StartListPhase("query");
                    if (parsedRequest != null)
                    {
                        parsedRequest.SchemaContext = schemaContext;
                        var result = await instanceRepository.GetPagedResultsWithGroupsAsync(
                            input.Page,
                            input.PageSize,
                            parsedRequest,
                            ct);
                        pagedList = result.PagedList;
                        groups = result.Groups;
                    }
                    else
                    {
                        var result = await instanceRepository.GetPagedResultsWithGroupsAsync(
                            input.Page,
                            input.PageSize,
                            input.Filter,
                            groupBy,
                            aggregations,
                            input.Sort,
                            schemaContext,
                            ct);
                        pagedList = result.PagedList;
                        groups = result.Groups;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or FilterCompilationException)
                {
                    // The filter passed boundary validation but still could not be compiled into
                    // SQL — either a value the operator cannot accept for that column (such as
                    // `createdAt eq "notadate"`, surfacing as ArgumentException/FormatException) or
                    // a fail-closed guard in the builders (FilterCompilationException). Both mean
                    // the validator's rules and the SQL builder's rules have drifted, which is why
                    // this logs at Error; the caller still gets a 400, not a 500, because the
                    // request is unservable either way.
                    //
                    // SchemaFilterValidationException is deliberately NOT caught here: it is a
                    // master-schema policy decision (field not filterable, operator not in
                    // x-filterOperators) that the boundary validator intentionally does not
                    // duplicate. Treating it as drift would fire this alarm on every routine
                    // policy rejection. It already carries its own 400-mapping code and passes
                    // through untouched.
                    logger.InstanceFilterCompilationFailed(ex, input.Domain, input.Workflow);

                    if (ex is FilterCompilationException)
                        throw; // Already a UserFriendlyException with InstanceFilterInvalid.

                    throw new UserFriendlyException(
                        WorkflowErrorCodes.InstanceFilterInvalid,
                        $"Filter could not be applied: {ex.Message}");
                }

                var route = urlTemplateBuilder.BuildInstanceListUrl(input.Domain, input.Workflow);
                var linkGen = paginationLinkGenerator.Relative();

                // If groups are present, populate items with groups instead of instances
                if (groups is { Count: > 0 })
                {
                    var groupPagedList = new HateoasPagedList<GroupSummary>(
                        groups,
                        input.Page,
                        input.PageSize,
                        hasNext: groups.Count == input.PageSize);
                    var groupedResponse = InstanceListWithGroupsResponse<GetInstanceOutput>.FromGroups(groups);
                    groupedResponse.Links = linkGen.GenerateLinks(groupPagedList, route);
                    return groupedResponse;
                }

                using var outputActivity = InstanceReadActivityHelper.StartListPhase("output");
                // Normal flow: build instance outputs
                var list = new List<GetInstanceOutput>();
                var preparedFlows = new Dictionary<string, Definitions.Workflow>(StringComparer.Ordinal);
                var listFieldFilter = schemaFieldFilterService is IListSchemaFieldFilterFactory filterFactory
                    ? filterFactory.CreateForList() : schemaFieldFilterService;
                var listExtensions = instanceExtensionService is IListExtensionServiceFactory extensionFactory
                    ? extensionFactory.CreateForList() : instanceExtensionService;
                foreach (var instance in pagedList.Items)
                {
                    var instanceOutputResult = await BuildInstanceOutputAsync(
                        input.Domain,
                        input.Extensions,
                        input.Workflow,
                        instance,
                        instance.LatestData,
                        ExtensionScope.GetAllInstances,
                        input.Headers,
                        input.QueryParameters,
                        ct,
                        preparedFlows,
                        listFieldFilter,
                        listExtensions);

                    // Propagate extension errors - fail-fast behavior
                    if (!instanceOutputResult.IsSuccess)
                    {
                        throw new UserFriendlyException(
                            instanceOutputResult.Error.Code,
                            instanceOutputResult.Error.Message,
                            instanceOutputResult.Error.Detail).WithData("Target",
                            instanceOutputResult.Error.Target ?? string.Empty);
                    }

                    var instanceOutput = instanceOutputResult.Value!;

                    // Free: the flag is already on the loaded instance and the links are formatted
                    // strings, so the list view no longer issues an incident query at all.
                    instanceOutput.Metadata.Incident = BuildIncidentInfo(instance, input.Domain, input.Workflow);

                    list.Add(instanceOutput);
                }

                var resultPagedList = new HateoasPagedList<GetInstanceOutput>(list, pagedList.CurrentPage,
                    pagedList.PageSize,
                    pagedList.HasNext);

                var response = InstanceListWithGroupsResponse<GetInstanceOutput>.FromPagedList(resultPagedList, null);
                response.Links = linkGen.GenerateLinks(resultPagedList, route);
                return response;
            },
            cancellationToken);
    }

    /// <summary>
    /// Fills <c>metadata.incident</c>: the denormalized flag plus the two links. Reads NOTHING — the
    /// flag is a column on the instance already in hand. This used to issue up to three queries per
    /// GET (newest five, the unresolved rows, a total count) to embed content the active-incident and
    /// history endpoints already serve.
    /// </summary>
    private IncidentInfoDto BuildIncidentInfo(Instance instance, string domain, string workflow)
        => IncidentInfoDto.FromInstance(
            instance,
            activeHref: urlTemplateBuilder.BuildActiveIncidentUrl(domain, workflow, instance.Id.ToString()),
            historyHref: urlTemplateBuilder.BuildIncidentsUrl(domain, workflow, instance.Id.ToString()));

    /// <summary>
    /// Returns the newest unresolved incident of an instance, or <c>NotFound</c> when none is open —
    /// the target of the <c>incident.active</c> link.
    /// </summary>
    /// <remarks>
    /// <b>404 is a normal answer here, not an error.</b> The link is emitted only while the flag says
    /// an incident is open, but the incident can be resolved between the poll and the follow-up (a
    /// successful retry does exactly that). A client seeing 404 should re-read the state rather than
    /// treat it as a failure. Gated by the same <c>queryRoles</c> check as the state function and the
    /// history endpoint, and like them it never returns a stack trace.
    /// </remarks>
    public async Task<Result<IncidentDetailDto>> GetActiveInstanceIncidentAsync(
        GetActiveInstanceIncidentInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.IncidentActive, input.Domain, input.Workflow);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .BindAsync(async data =>
            {
                using var instanceScope = BeginInstanceScope(data.instance);


                // Trust the row, not the flag: the two are written in one unit of work, but a resolve
                // that raced this read leaves the flag true for the moment it takes to commit.
                var active = await instanceIncidentRepository.GetActiveAsync(data.instance.Id, cancellationToken);

                return active is null
                    ? Result<IncidentDetailDto>.Fail(WorkflowErrors.ActiveIncidentNotFound(input.Instance))
                    : Result<IncidentDetailDto>.Ok(IncidentDetailDto.FromIncident(active));
            });
    }

    /// <summary>
    /// Returns the task execution history of an instance in execution order — the tasks system
    /// function. Only execution metadata and the fault reason leave this surface — the journaled
    /// request/response/invocation payloads are not exposed on any API, since mapping scripts
    /// write their built headers into them; they stay in the journal table.
    /// </summary>
    public async Task<Result<GetInstanceTasksOutput>> GetInstanceTasksAsync(
        GetInstanceTasksInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.TaskHistory, input.Domain, input.Workflow);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .BindAsync(async data =>
            {
                using var instanceScope = BeginInstanceScope(data.instance);


                var rows = await instanceTaskRepository.GetHistoryByInstanceIdAsync(
                    data.instance.Id, cancellationToken);

                return Result<GetInstanceTasksOutput>.Ok(new GetInstanceTasksOutput
                {
                    Items = rows.Select(InstanceTaskDto.FromRow).ToList()
                });
            });
    }

    /// <summary>
    /// Returns the recorded actions (execution sub-steps) of one task journal entry in execution
    /// order — the actions system function. The task must belong to the addressed instance —
    /// otherwise <c>NotFound</c> (<c>Instance:100038</c>), so a task can never be read through
    /// another instance's gate.
    /// </summary>
    public async Task<Result<GetInstanceTaskActionsOutput>> GetInstanceTaskActionsAsync(
        GetInstanceTaskActionsInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.ActionHistory, input.Domain, input.Workflow);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .BindAsync(async data =>
            {
                using var instanceScope = BeginInstanceScope(data.instance);


                var taskRef = await instanceTaskRepository.GetRefForInstanceAsync(
                    data.instance.Id, input.TaskId, cancellationToken);
                if (taskRef is null)
                    return Result<GetInstanceTaskActionsOutput>.Fail(
                        WorkflowErrors.InstanceTaskNotFound(input.TaskId, input.Instance));

                var actions = await instanceActionRepository.GetByTaskIdAsync(input.TaskId, cancellationToken);

                return Result<GetInstanceTaskActionsOutput>.Ok(new GetInstanceTaskActionsOutput
                {
                    TaskId = taskRef.Id,
                    TaskKey = taskRef.TaskKey,
                    Items = actions.Select(InstanceTaskActionDto.FromAction).ToList()
                });
            });
    }

    public async Task<Result<GetInstanceMetricsOutput>> GetTransitionMetricsAsync(
        GetTransitionMetricsInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.TransitionMetrics, input.Domain, input.Workflow);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(async instance =>
            {
                using var instanceScope = BeginInstanceScope(instance);

                // Slim rows only (no Body/Header jsonb). Every row whose transition key matches is one
                // firing — one attempt — and history firings are already 1:1 with these rows.
                var records = await instanceTransitionRepository
                    .GetByInstanceIdAsReadOnlyAsync(instance.Id, cancellationToken);

                var firings = records
                    .Where(r => r.TransitionId == input.TransitionKey)
                    .OrderBy(r => r.StartedAt)
                    .ToList();

                var tasksByRecord = await LoadMetricsTasksAsync(
                    firings.Select(r => r.Id), cancellationToken);

                var attempts = firings
                    .Select((record, index) => new MetricsAttemptDto
                    {
                        Seq = index + 1,
                        StartedAt = record.StartedAt,
                        FinishedAt = record.FinishedAt,
                        DurationMs = record.Duration?.TotalMilliseconds,
                        TriggerType = record.TriggerType,
                        TriggeredBy = record.CreatedBy,
                        // Every task journaled under this firing, any hook — the transition's own
                        // onExecute plus the adjacent states' onExit/onEntry that ran in the same
                        // record. Hook tells them apart; the client groups by it.
                        Tasks = OrderMetricsTasks(tasksByRecord[record.Id])
                    })
                    .ToList();

                return Result<GetInstanceMetricsOutput>.Ok(new GetInstanceMetricsOutput
                {
                    Element = new MetricsElementDto { Kind = "transition", Key = input.TransitionKey },
                    Count = attempts.Count,
                    Attempts = attempts
                });
            });
    }

    public async Task<Result<GetInstanceMetricsOutput>> GetStateMetricsAsync(
        GetStateMetricsInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.StateMetrics, input.Domain, input.Workflow);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(async instance =>
            {
                using var instanceScope = BeginInstanceScope(instance);

                var records = await instanceTransitionRepository
                    .GetByInstanceIdAsReadOnlyAsync(instance.Id, cancellationToken);

                // Pair the timeline into visits: the transition that ENTERED the state (ToState==key)
                // carries the onEntry tasks; the next transition that LEFT it (FromState==key) carries
                // the onExit tasks. A visit still open (entered, not yet left) has no leaving record.
                var visits = PairStateVisits(records, input.StateKey);

                var relevantRecordIds = visits
                    .SelectMany(v => v.Leaving is null
                        ? new[] { v.Entering.Id }
                        : new[] { v.Entering.Id, v.Leaving.Id });

                var tasksByRecord = await LoadMetricsTasksAsync(relevantRecordIds, cancellationToken);

                var attempts = visits
                    .Select((visit, index) =>
                    {
                        // onEntry from the entering record; onExit from the leaving record. Filtering by
                        // hook is what separates them from the other tasks those same records also ran
                        // (the transition's onExecute, the other state's lifecycle). Legacy rows with a
                        // null hook cannot be classified into a phase and are omitted here.
                        var entryTasks = tasksByRecord[visit.Entering.Id]
                            .Where(t => t.Hook == Definitions.TaskTrigger.OnEntry);
                        var exitTasks = visit.Leaving is null
                            ? Enumerable.Empty<InstanceTaskMetricsRow>()
                            : tasksByRecord[visit.Leaving.Id]
                                .Where(t => t.Hook == Definitions.TaskTrigger.OnExit);

                        // Entry into the state is when the entering transition finished (onEntry ran as
                        // part of it); leaving is when the leaving transition started.
                        var enteredAt = visit.Entering.FinishedAt ?? visit.Entering.StartedAt;
                        var leftAt = visit.Leaving?.StartedAt;

                        return new MetricsAttemptDto
                        {
                            Seq = index + 1,
                            StartedAt = enteredAt,
                            FinishedAt = leftAt,
                            DurationMs = leftAt is { } left ? (left - enteredAt).TotalMilliseconds : null,
                            TriggerType = visit.Entering.TriggerType,
                            TriggeredBy = visit.Entering.CreatedBy,
                            Tasks = OrderMetricsTasks(entryTasks.Concat(exitTasks))
                        };
                    })
                    .ToList();

                return Result<GetInstanceMetricsOutput>.Ok(new GetInstanceMetricsOutput
                {
                    Element = new MetricsElementDto { Kind = "state", Key = input.StateKey },
                    Count = attempts.Count,
                    Attempts = attempts
                });
            });
    }

    /// <summary>
    /// Loads the metrics task rows for a set of transition records and groups them by owning record.
    /// A column projection — the jsonb payloads never leave the database. The lookup answers empty for
    /// a record with no tasks, so callers can index it without a guard.
    /// </summary>
    private async Task<ILookup<Guid, InstanceTaskMetricsRow>> LoadMetricsTasksAsync(
        IEnumerable<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        var ids = recordIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            // No attempts ⇒ no tasks; skip the query entirely.
            return Enumerable.Empty<InstanceTaskMetricsRow>().ToLookup(t => t.TransitionId);
        }

        var rows = await instanceTaskRepository.GetMetricsRowsByTransitionIdsAsync(ids, cancellationToken);
        return rows.ToLookup(t => t.TransitionId);
    }

    /// <summary>Execution order within an attempt: start time, then declared order, then a stable id tiebreak.</summary>
    private static List<MetricsTaskDto> OrderMetricsTasks(IEnumerable<InstanceTaskMetricsRow> tasks) =>
        tasks
            .OrderBy(t => t.StartedAt)
            .ThenBy(t => t.Order)
            .ThenBy(t => t.Id)
            .Select(MetricsTaskDto.FromRow)
            .ToList();

    /// <summary>
    /// Walks the instance's transition records (already ordered by StartedAt) and pairs them into
    /// visits of one state: a record with <c>ToState == stateKey</c> opens a visit (its onEntry tasks),
    /// the next record with <c>FromState == stateKey</c> closes it (its onExit tasks). A self-loop
    /// (<c>FromState == ToState == stateKey</c>) closes the open visit and opens a new one in the same
    /// record. A visit left open at the end (entered, not yet left) has a null leaving record.
    /// </summary>
    private static List<(InstanceTransitionSlim Entering, InstanceTransitionSlim? Leaving)> PairStateVisits(
        IReadOnlyList<InstanceTransitionSlim> records,
        string stateKey)
    {
        var visits = new List<(InstanceTransitionSlim Entering, InstanceTransitionSlim? Leaving)>();
        InstanceTransitionSlim? open = null;

        foreach (var record in records)
        {
            if (open is not null && record.FromState == stateKey)
            {
                visits.Add((open, record));
                open = null;
            }

            if (record.ToState == stateKey)
            {
                // Re-entry without an intervening exit shouldn't happen for a well-formed history, but
                // if it does the earlier visit is closed half-open rather than silently dropped.
                if (open is not null)
                {
                    visits.Add((open, null));
                }

                open = record;
            }
        }

        if (open is not null)
        {
            visits.Add((open, null));
        }

        return visits;
    }

    public async Task<Result<GetInstanceIncidentsOutput>> GetInstanceIncidentsAsync(
        GetInstanceIncidentsInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.IncidentHistory, input.Domain, input.Workflow);

        var page = input.Page < 1 ? 1 : input.Page;
        var pageSize = Math.Clamp(input.PageSize, 1, GetInstanceIncidentsInput.MaxPageSize);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .BindAsync(async data =>
            {
                using var instanceScope = BeginInstanceScope(data.instance);


                var paged = await instanceIncidentRepository.GetHistoryPagedAsync(
                    data.instance.Id, page, pageSize, cancellationToken);

                return Result<GetInstanceIncidentsOutput>.Ok(new GetInstanceIncidentsOutput
                {
                    HasActiveIncident = data.instance.HasActiveIncident,
                    Items = paged.Items.Select(IncidentDetailDto.FromIncident).ToList(),
                    Page = paged.CurrentPage,
                    PageSize = paged.PageSize,
                    HasNext = paged.HasNext
                });
            });
    }

    public async Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.History, input.Domain, input.Workflow);

        return await GetInstanceWithFullHistoryAsync(input.Instance, cancellationToken)
            .ThenAsync(async instance =>
            {
                using var instanceScope = BeginInstanceScope(instance);
                var transitions = await instanceTransitionRepository.GetByInstanceIdAsync(instance.Id, cancellationToken);

                var dtoList = transitions
                    .Select(t => new InstanceTransitionDto
                    {
                        Id = t.Id,
                        TransitionId = t.TransitionId,
                        FromState = t.FromState,
                        ToState = t.ToState,
                        EffectiveState = t.EffectiveState,
                        EffectiveStateType = t.EffectiveStateType,
                        EffectiveStateSubType = t.EffectiveStateSubType,
                        Stage = t.Stage,
                        StartedAt = t.StartedAt,
                        FinishedAt = t.FinishedAt,
                        DurationSeconds = t.Duration?.TotalSeconds,
                        TriggerType = t.TriggerType,
                        Body = t.Body.JsonElement,
                        Header = t.Header.JsonElement,
                        CreatedAt = t.CreatedAt,
                        CreatedBy = t.CreatedBy,
                        CreatedByBehalfOf = t.CreatedByBehalfOf
                    })
                    .ToList();

                return Result<GetInstanceHistoryOutput>.Ok(new GetInstanceHistoryOutput
                {
                    Transitions = dtoList
                });
            });
    }

    /// <summary>
    /// Builds instance transition information including status, current state, and correlations.
    /// This method consolidates the logic for determining instance information based on instance status.
    /// Uses instance.ActiveCorrelations directly to avoid extra database call.
    /// </summary>
    /// <param name="instance">The workflow instance</param>
    /// <returns>A tuple containing status, current state, and correlations</returns>
    private (InstanceStatus Status, string? CurrentState, List<InstanceCorrelationInfo> ActiveCorrelations)
        BuildInstanceTransitionInfo(Instance instance)
    {
        // Map active correlations from entity to DTO
        var activeCorrelations = instance.ActiveCorrelations
            .Select(c => new InstanceCorrelationInfo
            {
                CorrelationId = c.Id,
                ParentState = c.ParentState,
                SubFlowInstanceId = c.SubFlowInstanceId,
                SubFlowType = c.SubFlowType,
                SubFlowDomain = c.SubFlowDomain,
                SubFlowName = c.SubFlowName,
                SubFlowVersion = c.SubFlowVersion,
                IsCompleted = c.IsCompleted
            })
            .ToList();

        return (instance.Status, instance.CurrentState, activeCorrelations);
    }

    /// <summary>
    /// Data-function href for a correlation's sub item, carrying the caller's extensions when present.
    /// </summary>
    private string BuildCorrelationDataHref(
        string subFlowDomain, string subFlowName, Guid subFlowInstanceId, string[] allExtensions) =>
        allExtensions.Length > 0
            ? urlTemplateBuilder.BuildDataWithExtensionsUrl(
                subFlowDomain, subFlowName, subFlowInstanceId.ToString(), allExtensions)
            : urlTemplateBuilder.BuildDataUrl(
                subFlowDomain, subFlowName, subFlowInstanceId.ToString());

    /// <summary>
    /// Maps an active-correlation projection onto its response entry — the <c>activeCorrelations</c>
    /// list. Terminal and state-tracking details are absent from the projection and stay unset; they
    /// are meaningless for an active correlation anyway.
    /// </summary>
    private ActiveCorrelationHref BuildCorrelationHref(InstanceCorrelationInfo correlation, string[] allExtensions) =>
        new()
        {
            CorrelationId = correlation.CorrelationId,
            ParentState = correlation.ParentState,
            SubFlowInstanceId = correlation.SubFlowInstanceId,
            SubFlowType = correlation.SubFlowType,
            SubFlowDomain = correlation.SubFlowDomain,
            SubFlowName = correlation.SubFlowName,
            SubFlowVersion = correlation.SubFlowVersion,
            IsCompleted = correlation.IsCompleted,
            Href = BuildCorrelationDataHref(
                correlation.SubFlowDomain, correlation.SubFlowName, correlation.SubFlowInstanceId, allExtensions)
        };

    /// <summary>
    /// Maps a correlation entity onto its response entry — the full <c>correlations</c> list. Carries the
    /// terminal details (<c>completedAt</c>, <c>terminalOutcome</c>) and the tracked sub-item state that
    /// let a client reconstruct which sub items ran and how each one ended.
    /// </summary>
    private ActiveCorrelationHref BuildCorrelationHref(InstanceCorrelation correlation, string[] allExtensions) =>
        new()
        {
            CorrelationId = correlation.Id,
            ParentState = correlation.ParentState,
            SubFlowInstanceId = correlation.SubFlowInstanceId,
            SubFlowType = correlation.SubFlowType,
            SubFlowDomain = correlation.SubFlowDomain,
            SubFlowName = correlation.SubFlowName,
            SubFlowVersion = correlation.SubFlowVersion,
            IsCompleted = correlation.IsCompleted,
            CompletedAt = correlation.CompletedAt,
            TerminalOutcome = correlation.TerminalOutcome,
            CreatedAt = correlation.CreatedAt,
            CurrentState = correlation.SubFlowCurrentState,
            StateChangedAt = correlation.SubFlowStateChangedAt,
            Href = BuildCorrelationDataHref(
                correlation.SubFlowDomain, correlation.SubFlowName, correlation.SubFlowInstanceId, allExtensions)
        };

    /// <summary>
    /// Opens the descent span for one level. Thin forwarder over
    /// <see cref="InstanceReadActivityHelper.StartDescendScope"/> that captures this service's
    /// <c>IRuntimeInfoProvider</c>, so the five call sites below stay readable.
    /// </summary>
    private SubflowDescentScope StartDescend(
        string targetDomain,
        string targetFlow,
        string targetInstanceId,
        string parentInstanceId,
        string function)
    {
        return InstanceReadActivityHelper.StartDescendScope(
            runtimeInfoProvider, targetDomain, targetFlow, targetInstanceId, parentInstanceId, function);
    }

    /// <summary>
    /// Gets available transitions and state information from a remote SubFlow instance.
    /// Includes view extensions and active correlations from the SubFlow.
    /// </summary>
    /// <param name="activeSubFlowCorrelation">The active SubFlow correlation</param>
    /// <param name="mainInstance">The main workflow instance</param>
    /// <param name="currentWorkflow">The current workflow definition</param>
    /// <param name="extensions">Extensions to pass to the SubFlow for data href building</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>SubFlowStateInfo containing transitions, state, view extensions, and active correlations from SubFlow</returns>
    private async Task<SubFlowStateInfo> GetSubFlowTransitionsAsync(
        InstanceCorrelationInfo activeSubFlowCorrelation,
        Instance mainInstance,
        BBT.Workflow.Definitions.Workflow currentWorkflow,
        string[]? extensions,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        string? role,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Unresolvable roles fall through to the main-flow transitions below rather than forwarding
            // with an unknown role set — the subflow would then filter its transitions against nothing.
            var callerRoles = await callerRoleResolver.ResolveRolesAsync(headers, cancellationToken);
            if (!callerRoles.IsSuccess)
                return GetMainFlowTransitions(mainInstance, currentWorkflow);

            var subFlowInput = new GetFunctionWithInstanceInput
            {
                Domain = activeSubFlowCorrelation.SubFlowDomain,
                Workflow = activeSubFlowCorrelation.SubFlowName,
                Version = activeSubFlowCorrelation.SubFlowVersion,
                Instance = activeSubFlowCorrelation.SubFlowInstanceId.ToString(),
                Extensions = extensions,
                Headers = headers,
                QueryParams = queryParams,
                Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
                Roles = callerRoles.Value
            };

            using var descent = StartDescend(
                activeSubFlowCorrelation.SubFlowDomain,
                activeSubFlowCorrelation.SubFlowName,
                activeSubFlowCorrelation.SubFlowInstanceId.ToString(),
                mainInstance.Id.ToString(),
                TelemetryConstants.DescentFunctions.State);

            var subFlowResult = await instanceQueryGateway.GetFunctionWithStateAsync(
                subFlowInput,
                cancellationToken);

            if (subFlowResult.Result is { IsSuccess: true, Value: not null })
            {
                var subFlowValue = subFlowResult.Result.Value;

                // Extract transition names from TransitionItem list
                var transitionNames = subFlowValue.Transitions?
                    .Select(t => t.Name)
                    .ToList() ?? new List<string>();

                // Include parent's shared transitions (for current state) so clients can discover and call them while in subflow
                var availableTransitions = MergeWithParentAvailableTransitions(
                    transitionNames,
                    mainInstance,
                    currentWorkflow);
                    
                // Return complete SubFlow state including view extensions, active correlations, and transition items (with HasView)
                return new SubFlowStateInfo(
                    AvailableTransitions: availableTransitions,
                    CurrentState: subFlowValue.State,
                    StateType: subFlowValue.StateType,
                    Status: subFlowValue.Status,
                    SubFlowData: subFlowValue.Data,
                    SubFlowView: subFlowValue.View,
                    SubFlowActiveCorrelations: subFlowValue.ActiveCorrelations,
                    SubFlowCorrelations: subFlowValue.Correlations,
                    SubFlowTransitionItems: subFlowValue.Transitions,
                    // Bubble the (possibly deeper) subflow's long-poll termination signal up the chain.
                    Interaction: subFlowValue.Interaction,
                    // The client observes the leaf: its incident is what explains a stalled chain.
                    Incident: subFlowValue.Incident);
            }
        }
        catch (Exception ex)
        {
            // Log the exception and fall back to main flow transitions
            logger.SubFlowTransitionsQueryFailed(
                ex,
                activeSubFlowCorrelation.SubFlowDomain,
                activeSubFlowCorrelation.SubFlowName,
                activeSubFlowCorrelation.SubFlowInstanceId);
        }

        // Fallback to main flow transitions
        return GetMainFlowTransitions(mainInstance, currentWorkflow);
    }

    /// <summary>
    /// Merges subflow transition names with the parent workflow's shared transitions and its well-known
    /// workflow-level transitions — cancel, updateData and exit (manual/event, available in current state).
    /// When in active subflow, clients see subflow transitions plus those parent transitions; state-level
    /// parent transitions are not included. updateData in particular only does work while the parent sits
    /// in a SubFlow state (updateData executes on the parent and is never forwarded to the subflow), so this merge is its primary surface.
    /// </summary>
    private static List<string> MergeWithParentAvailableTransitions(
        List<string> subflowTransitionNames,
        Instance mainInstance,
        BBT.Workflow.Definitions.Workflow currentWorkflow)
    {
        var stateResult = currentWorkflow.GetState(mainInstance.GetCurrentState);
        if (!stateResult.IsSuccess)
            return subflowTransitionNames;

        var currentState = stateResult.Value!;
        var parentSharedOnly = currentWorkflow.GetAvailableSharedTransitionKeysOnly(currentState);
        var merged = subflowTransitionNames.Union(parentSharedOnly);

        string?[] wellKnownKeys =
        [
            currentWorkflow.GetCancelTransitionKey(currentState),
            currentWorkflow.GetUpdateDataTransitionKey(currentState),
            currentWorkflow.GetExitTransitionKey(currentState)
        ];

        foreach (var key in wellKnownKeys)
        {
            if (key != null)
                merged = merged.Union([key]);
        }

        return merged.ToList();
    }

    /// <summary>
    /// Gets available transitions from the main workflow instance.
    /// </summary>
    /// <param name="instance">The workflow instance</param>
    /// <param name="currentWorkflow">The current workflow definition</param>
    /// <param name="transitionInfo">Optional transition info (used when called from main method)</param>
    /// <returns>SubFlowStateInfo containing available transitions, current state, and status from main flow (no SubFlow-specific data)</returns>
    private SubFlowStateInfo GetMainFlowTransitions(
        Instance instance,
        BBT.Workflow.Definitions.Workflow currentWorkflow,
        (InstanceStatus Status, string? CurrentState, List<InstanceCorrelationInfo> ActiveCorrelations)?
            transitionInfo = null)
    {
        var availableTransitions = new List<string>();

        if (instance.Status.Equals(InstanceStatus.Active))
        {
            var stateResult = currentWorkflow.GetState(instance.GetCurrentState);
            if (stateResult.IsSuccess)
            {
                availableTransitions = currentWorkflow.GetAvailableUserTransitionKeys(stateResult.Value!);
            }
        }

        var currentState = transitionInfo?.CurrentState ?? instance.CurrentState;
        var status = transitionInfo?.Status ?? instance.Status;

        return new SubFlowStateInfo(
            AvailableTransitions: availableTransitions,
            CurrentState: currentState,
            StateType: null,
            Status: status);
    }

    /// <summary>
    /// Retrieves an instance by ID or key using Railway pattern.
    /// Returns Result.Fail if instance is not found instead of throwing.
    /// </summary>
    private async Task<Result<Instance>> GetInstanceByIdOrKeyAsync(
        string instanceIdentifier,
        CancellationToken cancellationToken)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(instanceIdentifier, cancellationToken);
        var result = instance.EnsureNotNull(WorkflowErrors.InstanceNotFound(instanceIdentifier));
        if (result.IsSuccess)
        {
            var inst = result.Value!;
            var rootId = inst.GetRootInstanceId();
            if (rootId != inst.Id)
            {
                Activity.Current?.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
                Activity.Current?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
            }
        }

        return result;
    }

    /// <summary>
    /// Version-aware instance loading. When a specific (non-latest) version is requested,
    /// loads the full DataList so <see cref="Instance.FindData"/> can resolve any version.
    /// For null/empty/"latest" requests, uses the optimized path that only loads IsLatest rows.
    /// </summary>
    private async Task<Result<Instance>> GetInstanceByIdOrKeyAsync(
        string instanceIdentifier,
        string? version,
        CancellationToken cancellationToken)
    {
        if (InstanceDataVersionComparer.IsRequestingLatest(version))
        {
            return await GetInstanceByIdOrKeyAsync(instanceIdentifier, cancellationToken);
        }

        return await GetInstanceWithFullHistoryAsync(instanceIdentifier, cancellationToken);
    }

    /// <summary>
    /// Loads an instance with the full DataList history (no IsLatest filter). Dedicated to
    /// <see cref="GetInstanceHistoryAsync"/>; runtime hot-paths must keep using
    /// <see cref="GetInstanceByIdOrKeyAsync(string, CancellationToken)"/> which loads only the latest snapshot.
    /// </summary>
    private async Task<Result<Instance>> GetInstanceWithFullHistoryAsync(
        string instanceIdentifier,
        CancellationToken cancellationToken)
    {
        var instance = await instanceRepository.FindByIdentifierWithFullHistoryAsync(instanceIdentifier, cancellationToken);
        return instance.EnsureNotNull(WorkflowErrors.InstanceNotFound(instanceIdentifier));
    }

    private async Task<Result<GetInstanceOutput>> BuildInstanceOutputAsync(
        string domain,
        string[]? extensionRequested,
        string workflow,
        Instance instance,
        InstanceData? instanceData,
        ExtensionScope currentScope,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken,
        Dictionary<string, Definitions.Workflow>? preparedFlows = null,
        ISchemaFieldFilterService? preparedFieldFilter = null,
        IInstanceExtensionService? preparedExtensions = null)
    {
        Definitions.Workflow? flow = null;
        var version = instance.FlowVersion ?? string.Empty;
        if (preparedFlows?.TryGetValue(version, out flow) != true)
        {
            var flowResult = await componentCacheStore.GetFlowAsync(domain, workflow, instance.FlowVersion, cancellationToken);
            flow = flowResult.IsSuccess ? flowResult.Value : null;
            if (flow != null) preparedFlows?.Add(version, flow);
        }

        var response = new GetInstanceOutput
        {
            Id = instance.Id,
            Flow = instance.Flow,
            FlowVersion = instance.FlowVersion,
            EntityEtag = instanceData?.ETag ?? string.Empty,
            Domain = domain,
            Key = instance.Key!,
            Tags = instance.Tags,
            Attributes = instanceData?.Data.JsonElement,
            Metadata = new InstanceMetadataDto(instance)
        };

        if (flow == null)
        {
            return Result<GetInstanceOutput>.Ok(response);
        }

        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(flow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithTransition(string.Empty)
            .WithBody(instanceData?.Data ?? new JsonData("{}"))
            .WithHeaders(headers)
            .WithQueryParameters(queryParameters)
            .BuildAsync(cancellationToken);

        // Execute extensions with fail-fast behavior
        var extensionsResult = await (preparedExtensions ?? instanceExtensionService).ProcessExtensionsAsync(
            extensionRequested,
            scriptContext,
            flow,
            currentScope,
            cancellationToken);

        // Propagate extension errors - fail-fast behavior
        if (!extensionsResult.IsSuccess)
        {
            return Result<GetInstanceOutput>.Fail(extensionsResult.Error);
        }

        response.Extensions = extensionsResult.Value!;

        response.Attributes =
            await (preparedFieldFilter ?? schemaFieldFilterService).ApplyAsync(
                flow, response.Attributes, instance,
                new AuthorizationRequestContext(headers, queryParameters), cancellationToken) ??
            response.Attributes;

        return Result<GetInstanceOutput>.Ok(response);
    }

    public async Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Fast path for latest-data requests only: the ETag is a deterministic hash of the data
        // fingerprint (instance id + latest data ETag + flow version) plus the caller scope, so
        // an If-None-Match match is answered with 304 from a single projection query — no
        // aggregate load, no extension run, no response build. Pinned-version requests stay on
        // the full path: an older-line write changes their body without moving the latest ETag.
        //
        // A validated cache entry does NOT short-circuit the build: it supplies the DATA portion
        // (skipping the x-roles field filtering) while extensions are ALWAYS computed fresh —
        // the cache holds pure instance data, never extension output.
        var transaction = System.Diagnostics.Activity.Current;

        var isLatestRequest = InstanceDataVersionComparer.IsRequestingLatest(input.Version);
        string? dataCacheKey = null;
        Caching.DataFunctionCacheEntry? validatedEntry = null;
        if (dataFunctionCache.Enabled && isLatestRequest)
        {
            var fastPath = await TryServeDataFromFingerprintAsync(input, cancellationToken);
            if (fastPath.NotModified.HasValue)
            {
                // 304 from the fingerprint projection alone — no span created.
                InstanceReadActivityHelper.SetReadOutcome(
                    transaction, InstanceReadKinds.Data, InstanceReadActivityHelper.FastPathNotModified);
                return fastPath.NotModified.Value;
            }

            validatedEntry = fastPath.ValidatedEntry;
            dataCacheKey = dataFunctionCache.BuildKey(input);
        }

        // A validated entry does NOT short-circuit: it supplies the data portion while extensions
        // still run, so this is a build that reused the cached body, not a cache hit.
        InstanceReadActivityHelper.SetReadOutcome(
            transaction,
            InstanceReadKinds.Data,
            validatedEntry is not null
                ? InstanceReadActivityHelper.FastPathCacheHit
                : dataFunctionCache.Enabled && isLatestRequest
                    ? InstanceReadActivityHelper.FastPathBuild
                    : InstanceReadActivityHelper.FastPathDisabled);

        using var readEnvelope = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.Data, input.Domain, input.Workflow);

        // Railway chain: Get Instance → Load Flow (using instance.FlowVersion) → Match to ConditionalResult
        return await GetInstanceByIdOrKeyAsync(input.Instance, input.Version, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken)
                    .MapAsync(workflow => (flow: workflow, instance)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    var (flow, instance) = data;
                    using var instanceScope = BeginInstanceScope(instance);


                    var instanceData = instance.FindData(input.Version);
                    var entityEtag = instanceData?.ETag ?? string.Empty;

                    var result = new GetInstanceDataOutput();

                    // Data portion: reuse the validated cache entry (already field-filtered for
                    // this caller scope) when available; otherwise filter the resolved row.
                    // Extensions below always run fresh against the RAW instance data.
                    if (validatedEntry is not null)
                    {
                        result.Data = validatedEntry.Data;
                    }
                    else
                    {
                        result.Data = instanceData?.Data.JsonElement;
                        result.Data = await schemaFieldFilterService.ApplyAsync(
                                          flow, result.Data, instance,
                                          new AuthorizationRequestContext(input.Headers, input.QueryParameters),
                                          cancellationToken) ??
                                      result.Data;
                    }

                    // If there's an active SubFlow and extensions are requested, fetch from SubFlow
                    if (instance.Subflow != null)
                    {
                        var subFlowExtensionsResult = await GetSubFlowExtensionsAsync(
                            instance.Subflow,
                            input.Extensions,
                            cancellationToken);

                        result.Extensions = subFlowExtensionsResult.Value?.Extensions ??
                                            new Dictionary<string, object>();
                    }
                    else
                    {
                        // No active SubFlow - process extensions locally
                        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                            .WithWorkflow(flow)
                            .WithInstance(instance)
                            .WithRuntime(runtimeInfoProvider)
                            .WithTransition(string.Empty)
                            .WithBody(instanceData?.Data ?? new JsonData("{}"))
                            .WithHeaders(input.Headers)
                            .WithQueryParameters(input.QueryParameters)
                            .BuildAsync(cancellationToken);

                        var extensionsResult = await instanceExtensionService.ProcessExtensionsAsync(
                            input.Extensions,
                            scriptContext,
                            flow,
                            ExtensionScope.GetInstance,
                            cancellationToken);

                        if (!extensionsResult.IsSuccess)
                        {
                            return ConditionalResult<GetInstanceDataOutput>.Fail(extensionsResult.Error);
                        }

                        result.Extensions = extensionsResult.Value!;
                    }

                    result.EntityEtag = entityEtag;

                    // Same fingerprint-ETag the fast path computes from the projection query.
                    // Pinned-version requests hash the RESOLVED row's ETag instead of the latest
                    // one (a write into that version line creates a new row with a new ULID).
                    var etag = dataFunctionCache.ComputeEtag(input, isLatestRequest
                        ? InstanceDataFingerprint.FromInstance(instance)
                        : new InstanceDataFingerprint(instance.Id, instance.Key, instanceData?.ETag,
                            instance.FlowVersion, instance.EffectiveState, instance.HasActiveSubFlow));

                    // Warm the cache before the 304 decision so a Not-Modified outcome still
                    // stores the entry. Only latest-data responses with an existing data row are
                    // cacheable; the entry holds ONLY the field-filtered data — extension output
                    // is never cached. No re-write when the data came from a validated entry.
                    // TTL is workflow-author-controlled with a host default.
                    if (dataCacheKey is not null && instanceData is not null && validatedEntry is null)
                    {
                        await dataFunctionCache.SetAsync(dataCacheKey, new Caching.DataFunctionCacheEntry
                        {
                            Etag = etag,
                            EntityEtag = entityEtag,
                            Data = result.Data
                        }, dataFunctionCache.ResolveTtlSeconds(flow.Config?.FunctionCache), cancellationToken);
                    }

                    if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
                    {
                        return ConditionalResult<GetInstanceDataOutput>.NotModified();
                    }

                    result.ETag = etag;
                    return ConditionalResult<GetInstanceDataOutput>.Success(result);
                },
                onFailure: ConditionalResult<GetInstanceDataOutput>.Fail);
    }

    /// <summary>
    /// Fast path for latest-data requests over the data fingerprint. Loads the lightweight
    /// projection, computes the deterministic fingerprint ETag and answers 304 when the
    /// caller's If-None-Match matches (no cache access, no extension run, no build).
    /// Otherwise consults the body cache and returns the entry whose ETag matches the current
    /// fingerprint ETag — the build path then reuses its DATA portion (skipping field
    /// filtering) while always computing extensions fresh. Both results are null when the
    /// instance was not found (the full path produces the proper error), on a cache miss,
    /// or when the stored entry is stale.
    /// </summary>
    private async Task<(ConditionalResult<GetInstanceDataOutput>? NotModified, Caching.DataFunctionCacheEntry? ValidatedEntry)>
        TryServeDataFromFingerprintAsync(
            GetInstanceDataInput input,
            CancellationToken cancellationToken)
    {
        var fingerprint = await instanceRepository.GetDataFingerprintAsync(input.Instance, cancellationToken);
        if (fingerprint is null)
            return (null, null);

        var etag = dataFunctionCache.ComputeEtag(input, fingerprint);

        if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
        {
            logger.DataFunctionEtagNotModified(input.Instance);
            return (ConditionalResult<GetInstanceDataOutput>.NotModified(), null);
        }

        var entry = await dataFunctionCache.GetAsync(dataFunctionCache.BuildKey(input), cancellationToken);
        if (entry is null)
        {
            logger.DataFunctionCacheMiss(input.Instance);
            return (null, null);
        }

        if (!string.Equals(entry.Etag, etag, StringComparison.Ordinal))
        {
            logger.DataFunctionCacheInvalidated(input.Instance, entry.Etag, etag);
            return (null, null);
        }

        logger.DataFunctionCacheHit(input.Instance);
        return (null, entry);
    }

    /// Gets the view definition for rule-based view selection.
    /// Returns the view definition from the transition (if transitionKey is provided) or from the state.
    /// </summary>
    private static ViewDefinition? GetViewDefinition(
        Definitions.Workflow currentWorkflow,
        State currentState,
        string? transitionKey)
    {
        if (!transitionKey.IsNullOrWhiteSpace())
        {
            var transition = currentWorkflow.ResolveTransition(transitionKey, currentState);
            return transition?.View;
        }

        return currentState.View;
    }

    private static string ToCamelCaseName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return string.IsNullOrEmpty(name)
            ? string.Empty
            : char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Client-facing <see cref="TransitionItem.Kind"/> value for the updateData transition.
    /// Deliberately not <see cref="WellKnownTransitionKeys.UpdateData"/> ("update-parent-data"):
    /// the kind vocabulary mirrors the workflow-definition field names (cancel, exit, updateData),
    /// while the request-side well-known alias stays unchanged.
    /// </summary>
    private const string UpdateDataTransitionKind = "updateData";

    /// <summary>
    /// <see cref="TransitionItem.Kind"/> value for runtime-armed scheduled transitions listed in
    /// <c>transitions</c>. Never produced by <see cref="ResolveTransitionKind"/> — scheduled
    /// transitions are excluded from the caller-triggerable candidates — so the two vocabularies
    /// cannot collide on an entry.
    /// </summary>
    private const string ScheduledTransitionKind = "scheduled";

    private static string ResolveTransitionKind(
        Definitions.Workflow workflow,
        State currentState,
        string transitionKey)
    {
        if (IsTransitionKey(workflow.Cancel, transitionKey) ||
            transitionKey.Equals(WellKnownTransitionKeys.Cancel, StringComparison.OrdinalIgnoreCase))
            return WellKnownTransitionKeys.Cancel;

        if (IsTransitionKey(workflow.Exit, transitionKey) ||
            transitionKey.Equals(WellKnownTransitionKeys.Exit, StringComparison.OrdinalIgnoreCase))
            return WellKnownTransitionKeys.Exit;

        if (IsTransitionKey(workflow.UpdateData, transitionKey) ||
            transitionKey.Equals(WellKnownTransitionKeys.UpdateData, StringComparison.OrdinalIgnoreCase))
            return UpdateDataTransitionKind;

        if (workflow.Timeout?.Key.Equals(transitionKey, StringComparison.OrdinalIgnoreCase) == true ||
            transitionKey.Equals(WellKnownTransitionKeys.Timeout, StringComparison.OrdinalIgnoreCase))
            return WellKnownTransitionKeys.Timeout;

        if (currentState.FindTransition(transitionKey) != null)
            return "stateTransition";

        if (workflow.FindSharedTransition(transitionKey) != null)
            return "sharedTransition";

        return "stateTransition";
    }

    private static bool IsTransitionKey(Transition? transition, string transitionKey) =>
        transition?.Key.Equals(transitionKey, StringComparison.OrdinalIgnoreCase) == true;

    public async Task<ConditionalResult<GetInstanceStateOutput>> GetInstanceStateAsync(
        GetInstanceStateInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Long-poll fast path: the ETag is a deterministic hash of the state fingerprint
        // (instance id + effective state + status + flow version) plus the caller scope, so an
        // If-None-Match match can be answered with 304 from a single projection query — no cache
        // entry, aggregate load or response build needed. The body cache only serves callers
        // without a current ETag.
        // The transaction, captured before anything of ours opens a span. Every branch below stamps
        // its outcome here — at zero span documents, which is the point on the runtime's
        // highest-QPS route.
        var transaction = System.Diagnostics.Activity.Current;

        string? stateCacheKey = null;
        IDisposable? activeSubflowBuildLease = null;
        if (stateFunctionCache.Enabled)
        {
            stateCacheKey = stateFunctionCache.BuildKey(input);
            var fastResult = await TryServeStateFromFingerprintAsync(
                input,
                stateCacheKey,
                cancellationToken);
            if (fastResult.Result.HasValue)
            {
                // Answered without building. NO span is created on this branch — not an envelope,
                // not a child — so a long-poll that returns 304 forever adds nothing to the trace.
                InstanceReadActivityHelper.SetReadOutcome(
                    transaction,
                    InstanceReadKinds.State,
                    fastResult.Result.Value.IsNotModified
                        ? InstanceReadActivityHelper.FastPathNotModified
                        : InstanceReadActivityHelper.FastPathCacheHit);
                return fastResult.Result.Value;
            }

            activeSubflowBuildLease = fastResult.BuildLease;
        }

        InstanceReadActivityHelper.SetReadOutcome(
            transaction,
            InstanceReadKinds.State,
            stateFunctionCache.Enabled
                ? InstanceReadActivityHelper.FastPathBuild
                : InstanceReadActivityHelper.FastPathDisabled);

        try
        {
            // Opened only here, after the fast path has declined: the envelope measures a build.
            using var read = InstanceReadActivityHelper.StartRead(
                InstanceReadKinds.State, input.Domain, input.Workflow);

            return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    using var instanceScope = BeginInstanceScope(data.instance);

                    // Full correlation set (active + completed), ordered by creation time. The aggregate's
                    // own ChildCorrelations collection is loaded with an active-only filtered include, so
                    // the completed rows the response exposes require this dedicated read. Ordering is
                    // applied here rather than in the shared repository method, whose ParentState ordering
                    // the hierarchy and monitor consumers already depend on.
                    var allCorrelations = (await instanceCorrelationRepository
                            .GetByParentAsync(data.instance.Id, cancellationToken))
                        .OrderBy(c => c.CreatedAt)
                        .ToList();

                    var buildResult = await BuildInstanceStateOutputAsync(
                        data.instance, data.workflow, input, allCorrelations, cancellationToken);
                    if (!buildResult.IsSuccess)
                        return ConditionalResult<GetInstanceStateOutput>.Fail(buildResult.Error);

                    var output = buildResult.Value!;
                    var entityEtag = data.instance.LatestData?.ETag ?? string.Empty;
                    output.EntityEtag = entityEtag;

                    // Same fingerprint-ETag the fast path computes from the projection query. The
                    // correlation members must come from allCorrelations, not from the aggregate, so both
                    // paths hash the same set — see InstanceStateFingerprint.FromInstance.
                    // Active-subflow responses fold the live displayed state/status into the hash:
                    // the parent row alone cannot see subflow-internal Busy/Active flips.
                    var fingerprint = InstanceStateFingerprint.FromInstance(data.instance, allCorrelations);
                    var etag = data.instance.HasActiveSubFlow
                        ? stateFunctionCache.ComputeEtag(input, fingerprint, output)
                        : stateFunctionCache.ComputeEtag(input, fingerprint);

                    // Drift detector: this is the one place that holds BOTH the live descent's status
                    // and the stored projection, so the comparison is free. The projection is
                    // fingerprint material only — the body above carries the live value — and this
                    // measures whether it is ever trustworthy enough to be served instead.
                    if (data.instance.HasActiveSubFlow
                        && output.Status is not null
                        && !data.instance.EffectiveStatus.Equals(output.Status))
                    {
                        logger.EffectiveStatusDrift(
                            data.instance.Id, data.instance.EffectiveStatus.Code, output.Status.Code);
                    }

                    // A rule-gated interaction verdict depends on request inputs (headers, query
                    // parameters, instance data) that CallerScopeHash does not cover, so a body
                    // carrying one must never enter the shared cache: two callers with the same
                    // scope hash could legitimately receive different interactions. Own-state rules
                    // are visible here; a bubbled subflow interaction's gating arm is not (the
                    // child evaluated it), so any bubbled interaction skips caching conservatively.
                    // The fingerprint 304 path is untouched — the accepted staleness gap is
                    // documented in docs/domain/long-poll-termination.md.
                    var interactionIsRuleGated =
                        data.workflow.FindState(data.instance.GetCurrentState)?.LongPollRule is not null
                        || (data.instance.HasActiveSubFlow && output.Interaction is not null);

                    // Active-child state cannot be validated from the parent row, so keep it only
                    // for a short freshness window. Parent changes still invalidate immediately;
                    // concurrent misses are coalesced by the per-key build lease.
                    if (stateCacheKey is not null && !interactionIsRuleGated)
                    {
                        var cacheEntry = new Caching.StateFunctionCacheEntry
                        {
                            Etag = etag,
                            ParentEtag = stateFunctionCache.ComputeEtag(input, fingerprint),
                            IsActiveSubflowSnapshot = data.instance.HasActiveSubFlow,
                            EntityEtag = entityEtag,
                            Output = output
                        };
                        if (data.instance.HasActiveSubFlow)
                            await stateFunctionCache.SetAsync(
                                stateCacheKey, cacheEntry, stateFunctionCache.ActiveSubflowTtl, cancellationToken);
                        else
                            await stateFunctionCache.SetAsync(stateCacheKey, cacheEntry, cancellationToken);
                    }

                    if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
                        return ConditionalResult<GetInstanceStateOutput>.NotModified();

                    output.ETag = etag;
                    return ConditionalResult<GetInstanceStateOutput>.Success(output);
                },
                onFailure: error => ConditionalResult<GetInstanceStateOutput>.Fail(error));
        }
        finally
        {
            activeSubflowBuildLease?.Dispose();
        }
    }

    /// <summary>
    /// Long-poll fast path over the state fingerprint. Loads the lightweight projection, computes
    /// the deterministic fingerprint ETag and answers: 304 when the caller's If-None-Match matches
    /// (no cache access, no build), or the cached response when the stored entry carries the same
    /// ETag. Returns null when the full build path must run: instance not found (proper error
    /// comes from the full path), active subflow (live evaluation required), cache miss, or a
    /// stale cache entry.
    /// </summary>
    private async Task<StateFingerprintFastPath> TryServeStateFromFingerprintAsync(
        GetInstanceStateInput input,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var fingerprint = await instanceRepository.GetStateFingerprintAsync(input.Instance, cancellationToken);
        if (fingerprint is null)
            return default;

        if (fingerprint.HasActiveSubFlow)
        {
            var parentEtag = stateFunctionCache.ComputeEtag(input, fingerprint);
            var cached = await TryServeActiveSubflowSnapshotAsync(
                input, cacheKey, parentEtag, fingerprint, cancellationToken);
            if (cached.HasValue)
                return new(cached, null);

            // The gate span covers the wait AND the double-check, because the double-check is what
            // the wait was for: a request that queued behind another descent and then served that
            // descent's cache entry is the coalescing working, and it is only legible as one span.
            using var gate = InstanceReadActivityHelper.StartBuildGate();
            var lease = await AcquireBuildGateAsync(cacheKey, cancellationToken);

            // Double-check after entering the gate: another request may have populated the short
            // cache while this one was waiting.
            cached = await TryServeActiveSubflowSnapshotAsync(
                input, cacheKey, parentEtag, fingerprint, cancellationToken);
            if (cached.HasValue)
            {
                InstanceReadActivityHelper.SetBuildGateOutcome(
                    gate, lease.Contended, InstanceReadActivityHelper.BuildGateCoalesced);
                lease.Dispose();
                return new(cached, null);
            }

            InstanceReadActivityHelper.SetBuildGateOutcome(
                gate, lease.Contended, InstanceReadActivityHelper.BuildGateBuild);
            logger.StateFunctionCacheBypassedForSubFlow(input.Instance);
            return new(null, lease);
        }

        var etag = stateFunctionCache.ComputeEtag(input, fingerprint);

        if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
        {
            logger.StateFunctionEtagNotModified(input.Instance, fingerprint.EffectiveState, fingerprint.Status.Code);
            return new(ConditionalResult<GetInstanceStateOutput>.NotModified(), null);
        }

        var entry = await stateFunctionCache.GetAsync(cacheKey, cancellationToken);
        if (entry is null)
        {
            logger.StateFunctionCacheMiss(input.Instance);
            return default;
        }

        if (!string.Equals(entry.Etag, etag, StringComparison.Ordinal))
        {
            logger.StateFunctionCacheInvalidated(input.Instance, entry.Etag, etag);
            return default;
        }

        logger.StateFunctionCacheHit(input.Instance, fingerprint.EffectiveState, fingerprint.Status.Code);

        var output = entry.Output;
        output.EntityEtag = entry.EntityEtag;
        output.ETag = entry.Etag;
        return new(ConditionalResult<GetInstanceStateOutput>.Success(output), null);
    }

    private async Task<ConditionalResult<GetInstanceStateOutput>?> TryServeActiveSubflowSnapshotAsync(
        GetInstanceStateInput input,
        string cacheKey,
        string parentEtag,
        InstanceStateFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var entry = await stateFunctionCache.GetAsync(cacheKey, cancellationToken);
        if (entry is null
            || !entry.IsActiveSubflowSnapshot
            || !string.Equals(entry.ParentEtag, parentEtag, StringComparison.Ordinal))
            return null;

        logger.StateFunctionCacheHit(input.Instance, fingerprint.EffectiveState, fingerprint.Status.Code);
        if (!string.IsNullOrEmpty(input.IfNoneMatch) && entry.Etag.MatchesIfNoneMatch(input.IfNoneMatch))
            return ConditionalResult<GetInstanceStateOutput>.NotModified();

        entry.Output.EntityEtag = entry.EntityEtag;
        entry.Output.ETag = entry.Etag;
        return ConditionalResult<GetInstanceStateOutput>.Success(entry.Output);
    }

    private readonly record struct StateFingerprintFastPath(
        ConditionalResult<GetInstanceStateOutput>? Result,
        IDisposable? BuildLease);

    private static async Task<BuildGateLease> AcquireBuildGateAsync(
        string key,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = ActiveSubflowBuildGates.GetOrAdd(key, _ => new BuildGate());
            Interlocked.Increment(ref gate.Users);

            if (ActiveSubflowBuildGates.TryGetValue(key, out var current)
                && ReferenceEquals(current, gate))
            {
                try
                {
                    // Non-blocking attempt first, purely to learn whether anyone else held the gate.
                    // Without it the span's duration cannot be read: an uncontended acquisition and a
                    // wait that happened to be short are the same number.
                    var contended = !await gate.Semaphore.WaitAsync(0, CancellationToken.None);
                    if (contended)
                        await gate.Semaphore.WaitAsync(cancellationToken);

                    return new BuildGateLease(key, gate, contended);
                }
                catch
                {
                    ReleaseBuildGateReference(key, gate, releaseSemaphore: false);
                    throw;
                }
            }

            ReleaseBuildGateReference(key, gate, releaseSemaphore: false);
        }
    }

    private static void ReleaseBuildGateReference(string key, BuildGate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
            gate.Semaphore.Release();

        if (Interlocked.Decrement(ref gate.Users) == 0)
            ActiveSubflowBuildGates.TryRemove(new KeyValuePair<string, BuildGate>(key, gate));
    }

    private sealed class BuildGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }

    private sealed class BuildGateLease(string key, BuildGate gate, bool contended) : IDisposable
    {
        private int _disposed;

        /// <summary>True when the gate was already held on arrival, so this request really waited.</summary>
        internal bool Contended { get; } = contended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            ReleaseBuildGateReference(key, gate, releaseSemaphore: true);
        }
    }

    /// <summary>
    /// Builds the complete instance state output including transitions, correlations, and view information.
    /// When there's an active SubFlow, includes the SubFlow's view extensions in data href and merges active correlations.
    /// </summary>
    /// <param name="instance">The loaded instance aggregate (child correlations active-only).</param>
    /// <param name="currentWorkflow">The workflow definition bound to the instance.</param>
    /// <param name="input">The state request.</param>
    /// <param name="allCorrelations">Full child correlation set (active + completed), CreatedAt ascending.
    /// Feeds the <c>correlations</c> response list only — the active-subflow detection below deliberately
    /// keeps using the aggregate, whose active set drives Busy/settlement semantics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<Result<GetInstanceStateOutput>> BuildInstanceStateOutputAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetInstanceStateInput input,
        IReadOnlyCollection<InstanceCorrelation> allCorrelations,
        CancellationToken cancellationToken)
    {
        // ONE read, two projections. GetListActiveAsync filters on InstanceId + IsActive with no
        // JobType predicate, so every active job — scheduled transitions AND the workflow timeout —
        // is already selected and materialized here; splitting them below costs a pass over a list
        // that is a handful of rows per instance, never a second query. Loaded after the fingerprint
        // 304 path has declined, so the fast path still pays nothing.
        //
        // Scheduled-transition jobs feed the kind:"scheduled" entries of the transitions response
        // list only — deliberately NOT the fingerprint ETag (team decision, issue #864; known
        // staleness gap, see the ETag doc). The timeout job feeds the `timeout` block, which has no
        // such gap: its instant is immutable after arm and its presence tracks the instance status.
        var activeJobs = await instanceJobRepository.GetListActiveAsync(instance.Id, cancellationToken);

        var activeScheduledTransitionJobs = activeJobs
            .Where(j => j.JobType == JobType.ScheduledTransition)
            .ToList();

        // Build instance transition information using shared logic (no DB call - uses instance.ActiveCorrelations)
        var transitionInfo = BuildInstanceTransitionInfo(instance);

        // Check if there are any active SubFlow correlations
        var activeSubFlowCorrelation = transitionInfo.ActiveCorrelations
            .Where(c => c.SubFlowType.Equals(SubFlowType.SubFlow) && !c.IsCompleted)
            .OrderByDescending(c => c.CorrelationId)
            .FirstOrDefault();

        SubFlowStateInfo subFlowStateInfo;
        if (activeSubFlowCorrelation != null)
        {
            subFlowStateInfo = await GetSubFlowTransitionsAsync(
                activeSubFlowCorrelation, instance, currentWorkflow,
                input.Extensions, input.Headers, input.QueryParams,
                input.Role, cancellationToken);

            // Guard: SubFlow has reached a terminal status but the parent correlation is still
            // open (IsCompleted=false) — we are in the propagation window.
            // The parent is Busy handling the SubFlow completion; returning the SubFlow's terminal
            // status would falsely signal to clients that the whole flow is done.
            // Fall back to the parent's own state so the client receives Status=Busy and retries.
            // InstanceStatus.IsTerminal is the single definition; Instance.GetEffectiveStatus
            // clamps on the same predicate so the served metadata.effectiveStatus agrees with the
            // body this branch produces.
            var subFlowIsTerminal = subFlowStateInfo.Status?.IsTerminal == true;

            if (subFlowIsTerminal)
            {
                subFlowStateInfo = GetMainFlowTransitions(instance, currentWorkflow, transitionInfo);
            }
        }
        else
        {
            subFlowStateInfo = GetMainFlowTransitions(instance, currentWorkflow, transitionInfo);
        }

        var stateResult = currentWorkflow.GetState(instance.CurrentState!)
            .Ensure(
                state => state != null,
                Error.NotFound("notfound", $"State {instance.CurrentState} not found in workflow {input.Workflow}"));
        if (!stateResult.IsSuccess)
            return Result<GetInstanceStateOutput>.Fail(stateResult.Error);
        
        var currentStateValue = stateResult.Value!;
        var keysForTransitions = subFlowStateInfo.AvailableTransitions;
        {
            // Always evaluate authorization: predefined roles ($InstanceStarter, $PreviousUser) are checked via
            // ICurrentUser.ActorUserName regardless of whether a role parameter was supplied.
            // When this instance was started as a SubFlow with parent-defined transition overrides,
            // apply combined filtering: parent override grants for overridden transitions,
            // own role filtering for non-overridden transitions.
            // The same request context the authorize function passes, so a transition guarded by a dynamic
            // grant reading $.context.Headers/QueryParameters is listed here exactly when authorize would
            // allow it. Without it those namespaces are empty and the grant silently never matches.
            var authRequestContext = new AuthorizationRequestContext(input.Headers, input.QueryParams);

            // One span for the pass, opened only when there is something to filter. Counts rather
            // than per-key spans: the parent-override branch below evaluates key by key and builds
            // a fresh evaluator each time, and each evaluator serializes the instance's full latest
            // data — so "how many evaluators did this cost" is the number worth reading, and a span
            // per key would bury it.
            var keysToFilter = keysForTransitions.Count;
            using var filterPass = keysToFilter > 0
                ? AuthorizationActivityHelper.StartFilterTransitions()
                : null;
            var evaluatorCreations = 0;

            // The parent's stamped transition overrides are NOT resolved here any more. They are
            // resolved inside TransitionAuthorizationManager, together with the transition's own
            // grants and the availableIn narrowing, so the state function, `authorize` and the
            // authorization matrix cannot answer differently about the same transition. Doing it
            // here was how they diverged: this surface honoured a parent's narrowing at a leaf while
            // `authorize`, resolving overrides from the parent's definition, did not.
            if (activeSubFlowCorrelation != null)
            {
                // Parent context with active SubFlow:
                // SubFlow transitions are already correctly role-filtered by the SubFlow itself.
                // Only apply parent-level filtering to parent-added shared transitions.
                var subFlowTransitionKeys = subFlowStateInfo.SubFlowTransitionItems?
                    .Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
                var parentSharedKeys = keysForTransitions
                    .Where(k => !subFlowTransitionKeys.Contains(k))
                    .ToList();
                evaluatorCreations += parentSharedKeys.Count > 0 ? 1 : 0;
                var filteredParentSharedKeys = parentSharedKeys.Count > 0
                    ? (await transitionAuthorizationManager.FilterAuthorizedTransitionKeysAsync(
                            currentWorkflow, currentStateValue, instance, parentSharedKeys, input.Roles, authRequestContext, cancellationToken))
                      .ToList()
                    : parentSharedKeys;
                keysForTransitions = keysForTransitions
                    .Where(k => subFlowTransitionKeys.Contains(k))
                    .Concat(filteredParentSharedKeys)
                    .ToList();
            }
            else
            {
                evaluatorCreations++;
                keysForTransitions = (await transitionAuthorizationManager.FilterAuthorizedTransitionKeysAsync(
                        currentWorkflow, currentStateValue, instance, keysForTransitions, input.Roles, authRequestContext, cancellationToken))
                    .ToList();
            }

            AuthorizationActivityHelper.SetFilterResult(
                filterPass, keysToFilter, keysForTransitions.Count, evaluatorCreations);
        }

        List<TransitionItem> transitionItems;
        if (subFlowStateInfo.SubFlowTransitionItems != null)
        {
            var subFlowItemsByName =
                subFlowStateInfo.SubFlowTransitionItems.ToDictionary(t => t.Name, StringComparer.Ordinal);
            transitionItems = keysForTransitions
                .Select(key =>
                {
                    var subFlowItem = subFlowItemsByName.GetValueOrDefault(key);
                    bool hasView, loadData, hasSchema;
                    Dictionary<string, string>? annotations;
                    if (subFlowItem != null)
                    {
                        hasView = subFlowItem.View?.HasView ?? false;
                        loadData = subFlowItem.View?.LoadData ?? false;
                        hasSchema = subFlowItem.Schema?.HasSchema ?? false;
                        annotations = subFlowItem.Annotations;
                    }
                    else
                    {
                        var transition = currentWorkflow.ResolveTransition(key, currentStateValue);
                        hasView = transition?.View is { Views.Count: > 0 };
                        loadData = false;
                        hasSchema = transition?.Schema != null;
                        annotations = transition?.Annotations;
                    }

                    return new TransitionItem
                    {
                        Name = key,
                        Kind = !string.IsNullOrWhiteSpace(subFlowItem?.Kind)
                            ? subFlowItem.Kind
                            : ResolveTransitionKind(currentWorkflow, currentStateValue, key),
                        Href = urlTemplateBuilder.BuildTransitionUrl(input.Domain, input.Workflow,
                            instance.Id.ToString(), key),
                        View = new ViewHref
                        {
                            Href = urlTemplateBuilder.BuildViewUrl(input.Domain, input.Workflow,
                                instance.Id.ToString(), key),
                            HasView = hasView,
                            LoadData = loadData,
                        },
                        Schema = new SchemaHref
                        {
                            Href = urlTemplateBuilder.BuildSchemaUrl(input.Domain, input.Workflow,
                                instance.Id.ToString(), key),
                            HasSchema = hasSchema
                        },
                        Annotations = annotations
                    };
                })
                .ToList();
        }
        else
        {
            transitionItems = keysForTransitions.Select(transitionKey =>
            {
                var transition = currentWorkflow.ResolveTransition(transitionKey, currentStateValue);
                var hasView = transition?.View is { Views.Count: > 0 };
                var hasSchema = transition?.Schema != null;
                return new TransitionItem
                {
                    Name = transitionKey,
                    Kind = ResolveTransitionKind(currentWorkflow, currentStateValue, transitionKey),
                    Href = urlTemplateBuilder.BuildTransitionUrl(input.Domain, input.Workflow, instance.Id.ToString(),
                        transitionKey),
                    View = new ViewHref
                    {
                        Href = urlTemplateBuilder.BuildViewUrl(input.Domain, input.Workflow, instance.Id.ToString(),
                            transitionKey),
                        HasView = hasView
                    },
                    Schema = new SchemaHref
                    {
                        Href = urlTemplateBuilder.BuildSchemaUrl(input.Domain, input.Workflow, instance.Id.ToString(),
                            transitionKey),
                        HasSchema = hasSchema
                    },
                    Annotations = transition?.Annotations
                };
            }).ToList();
        }

        var viewDefinition = currentStateValue.View;
        var firstViewEntry = viewDefinition?.Views.FirstOrDefault();
        var viewExtensions = firstViewEntry?.Extensions ?? [];
        var viewLoadData = firstViewEntry?.LoadData ?? false;
        var stateHasView = viewDefinition is { Views.Count: > 0 };
        var allExtensions = subFlowStateInfo.SubFlowData != null
            ? ExtractExtensionsFromDataHref(subFlowStateInfo.SubFlowData.Href)
            : (input.Extensions ?? []).Concat(viewExtensions).ToArray();
        var dataHref = new DataHref
        {
            Href = allExtensions.Length > 0
                ? urlTemplateBuilder.BuildDataWithExtensionsUrl(input.Domain, input.Workflow, instance.Id.ToString(),
                    allExtensions)
                : urlTemplateBuilder.BuildDataUrl(input.Domain, input.Workflow, instance.Id.ToString())
        };
        var viewHref = new ViewHref
        {
            Href = urlTemplateBuilder.BuildViewUrl(input.Domain, input.Workflow, instance.Id.ToString()),
            HasView = subFlowStateInfo.SubFlowView?.HasView ?? stateHasView,
            LoadData = subFlowStateInfo.SubFlowView?.LoadData ?? viewLoadData
        };
        // Master schema endpoint href. The endpoint itself forwards to the active subflow when present,
        // so the link always points to this instance regardless of subflow state.
        var masterHref = new MasterHref
        {
            Href = urlTemplateBuilder.BuildMasterUrl(input.Domain, input.Workflow, instance.Id.ToString())
        };
        var mainFlowCorrelationHrefs = transitionInfo.ActiveCorrelations
            .Select(correlation => BuildCorrelationHref(correlation, allExtensions))
            .ToList();
        var allActiveCorrelations = subFlowStateInfo.SubFlowActiveCorrelations != null
            ? mainFlowCorrelationHrefs.Concat(subFlowStateInfo.SubFlowActiveCorrelations).ToList()
            : mainFlowCorrelationHrefs;

        // Full set (active + completed) for clients that need the sub item history. Merged with the
        // subflow's own full set exactly as the active list is, so a nested chain stays consistent.
        var fullCorrelationHrefs = allCorrelations
            .Select(correlation => BuildCorrelationHref(correlation, allExtensions))
            .ToList();
        var allCorrelationHrefs = subFlowStateInfo.SubFlowCorrelations != null
            ? fullCorrelationHrefs.Concat(subFlowStateInfo.SubFlowCorrelations).ToList()
            : fullCorrelationHrefs;

        // Role-aware state alias: when the displayed state is the main-flow current state and that
        // state defines aliases, return the role-resolved alias (localized label, else name) instead
        // of the raw state key. Internal workflow logic is unaffected — it always uses instance.CurrentState.
        var displayedState = subFlowStateInfo.CurrentState;
        if (currentStateValue.Aliases.Count > 0 &&
            string.Equals(displayedState, instance.CurrentState, StringComparison.Ordinal))
        {
            var requestContext = new AuthorizationRequestContext(input.Headers, input.QueryParams);
            var culture = LanguageResolver.ResolveCulture(input.Headers);
            var aliasDisplay = await ResolveStateAliasDisplayAsync(
                currentStateValue, instance, input.Roles, culture, requestContext, cancellationToken);
            if (!string.IsNullOrEmpty(aliasDisplay))
                displayedState = aliasDisplay;
        }

        // Declarative long-poll interaction: emitted whenever the current state declares
        // interaction.longPoll (subject to role grants), carrying the terminate flag and fallback window.
        // When terminate is true the client is told to stop polling, render the entered-state screen,
        // and acknowledge via the ack href. Role-filtered so only the intended roles receive it. The
        // interaction may originate at THIS instance (leaf) or at a nested subflow whose signal bubbled
        // up via SubFlowStateInfo — in the subflow case the ack href is rewritten to THIS level so the
        // client always acknowledges the instance it polls; the acknowledge endpoint then descends the
        // chain. The two are mutually exclusive (a parent in a SubFlow state does not itself declare
        // long-poll interaction). Role filtering for the bubbled case was already applied at the child
        // level (caller role forwarded via headers).
        var interaction = subFlowStateInfo.Interaction is { } childInteraction
            ? new InstanceInteractionOutput
            {
                TerminateLongPoll = childInteraction.TerminateLongPoll,
                FallbackTimeoutSeconds = childInteraction.FallbackTimeoutSeconds,
                Ack = childInteraction.Ack is not null
                    ? new AckHref
                    {
                        Href = urlTemplateBuilder.BuildLongPollAckUrl(
                            input.Domain, input.Workflow, instance.Id.ToString())
                    }
                    : null
            }
            : await ResolveInteractionAsync(
                input, instance, currentWorkflow, currentStateValue, displayedState, cancellationToken);

        // Incident block: the leaf's when an active subflow reported one (a stalled chain is explained
        // by the deepest incident), otherwise this instance's own. The history link always points to the
        // polled instance — a client follows it on the instance it is polling. Never a stack trace.
        var incidentHref = BuildIncidentHref(
            instance, subFlowStateInfo.Incident, input.Domain, input.Workflow);

        // Only the flag and the link — never the list. Enumerating the functions means one component
        // read per declared function plus a role evaluation each, which this response cannot afford.
        var functionsHref = new FunctionsHref
        {
            HasFunctions = currentWorkflow.Functions.Count > 0,
            Href = urlTemplateBuilder.BuildFunctionCatalogUrl(
                input.Domain, input.Workflow, instance.Id.ToString())
        };

        // Scheduled entries ride in the same transitions list, appended after the caller-triggerable
        // ones; clients discriminate on kind ("scheduled" ⇒ executeAtUtc present).
        transitionItems.AddRange(BuildScheduledTransitionEntries(
            activeScheduledTransitionJobs, input.Domain, input.Workflow, instance.Id.ToString()));

        // The workflow-level deadline, as its own block rather than a transitions[] entry — it is
        // not a transition (see InstanceTimeoutOutput).
        var timeout = BuildTimeoutBlock(instance, currentWorkflow, activeJobs);

        return Result<GetInstanceStateOutput>.Ok(new GetInstanceStateOutput
        {
            Data = dataHref,
            View = viewHref,
            Master = masterHref,
            State = displayedState ?? string.Empty,
            StateType = subFlowStateInfo.StateType.IsNullOrWhiteSpace()
                ? ToCamelCaseName(currentStateValue.StateType)
                : subFlowStateInfo.StateType!,
            Status = subFlowStateInfo.Status,
            ActiveCorrelations = allActiveCorrelations,
            Correlations = allCorrelationHrefs,
            Transitions = transitionItems,
            Functions = functionsHref,
            Interaction = interaction,
            Incident = incidentHref,
            Timeout = timeout
        });
    }

    /// <summary>
    /// Builds the state body's <c>incident</c> block: the flag plus links, never content. A
    /// leaf-reported block wins when it carries an active incident; otherwise the polled instance's
    /// own flag decides.
    /// </summary>
    /// <remarks>
    /// <b>Reads nothing.</b> <c>HasActiveIncident</c> is a column on the instance already in hand, so
    /// the state function — the runtime's hottest read, polled continuously by every waiting client —
    /// touches the incident table zero times. It used to load the unresolved rows purely to embed a
    /// summary here.
    /// <para>
    /// When an active subflow reports an incident, its block is taken as-is, so <c>active</c>
    /// addresses the SUBFLOW that owns the incident. <c>history</c> is always re-pointed at the polled
    /// instance: that link answers "what has gone wrong with the thing I asked about", and the client
    /// polling an ancestor did not ask about the leaf's history.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>internal</c> rather than private so the subflow-lifting rule — which instance each link
    /// addresses — can be pinned directly (<c>InternalsVisibleTo</c>). Reaching it through
    /// <c>GetInstanceStateAsync</c> would need a whole active-subflow gateway setup to assert four
    /// lines.
    /// </remarks>
    internal IncidentHref BuildIncidentHref(
        Instance instance,
        IncidentHref? leafIncident,
        string domain,
        string workflow)
    {
        var history = new IncidentHistoryHref
        {
            Href = urlTemplateBuilder.BuildIncidentsUrl(domain, workflow, instance.Id.ToString())
        };

        if (leafIncident is { HasActiveIncident: true })
        {
            return new IncidentHref
            {
                HasActiveIncident = true,
                Active = leafIncident.Active,
                History = history
            };
        }

        return new IncidentHref
        {
            HasActiveIncident = instance.HasActiveIncident,
            Active = instance.HasActiveIncident
                ? new ActiveIncidentHref
                {
                    Href = urlTemplateBuilder.BuildActiveIncidentUrl(domain, workflow, instance.Id.ToString())
                }
                : null,
            History = history
        };
    }

    /// <summary>
    /// Maps the instance's active scheduled-transition jobs to <c>kind: "scheduled"</c> entries of the
    /// response's <c>transitions</c> list, ordered by execution time ascending. Rows without an
    /// <see cref="InstanceJob.ExecuteAt"/> (persisted before the column existed) are omitted rather
    /// than emitted without a time — every scheduled entry carries an execution instant, and such rows
    /// age out as their jobs fire or are cancelled. Not role-filtered: a scheduled transition fires
    /// regardless of the caller, so the entries are facts about the instance, not caller capabilities.
    /// <para>
    /// href/view/schema are emitted with the SAME url shapes as the caller-triggerable entries but
    /// with <c>hasView</c>/<c>loadData</c>/<c>hasSchema</c> hardcoded false — a TEMPORARY uniformity
    /// concession so existing domain clients that assume every transitions[] item carries the three
    /// link objects do not break on scheduled entries; domains will adapt and the links may then be
    /// dropped again. The href is not an invitation to call: scheduled transitions stay
    /// System-actor-gated at execution (<c>ActorAuthorizationSpecification</c>), so a client PATCHing
    /// it is rejected exactly as before.
    /// </para>
    /// </summary>
    private IEnumerable<TransitionItem> BuildScheduledTransitionEntries(
        IReadOnlyCollection<InstanceJob> activeScheduledTransitionJobs,
        string domain,
        string workflow,
        string instanceId) =>
        activeScheduledTransitionJobs
            .Where(j => j.ExecuteAt.HasValue && !string.IsNullOrEmpty(j.TransitionKey))
            .OrderBy(j => j.ExecuteAt!.Value)
            .Select(j => new TransitionItem
            {
                Name = j.TransitionKey!,
                Kind = ScheduledTransitionKind,
                ExecuteAtUtc = j.ExecuteAt!.Value,
                Href = urlTemplateBuilder.BuildTransitionUrl(domain, workflow, instanceId, j.TransitionKey!),
                View = new ViewHref
                {
                    Href = urlTemplateBuilder.BuildViewUrl(domain, workflow, instanceId, j.TransitionKey!),
                    HasView = false,
                    LoadData = false
                },
                Schema = new SchemaHref
                {
                    Href = urlTemplateBuilder.BuildSchemaUrl(domain, workflow, instanceId, j.TransitionKey!),
                    HasSchema = false
                }
            });

    /// <summary>
    /// Builds the state body's <c>timeout</c> block, or null when the polled instance has no
    /// pending workflow deadline. Never reads: the job rows are the ones already fetched for the
    /// scheduled entries, and the effective timeout comes from the instance and definition in hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions, all required. <b>(1) The instance's own status is not terminal.</b>
    /// <see cref="InstanceStatus.IsTerminal"/> is the same set the fire path's
    /// <c>Instance.IsCompleted</c> guard uses, so the block disappears exactly when the deadline
    /// stops being able to fire — and it does so without waiting for the asynchronous
    /// <c>cancel-cleanup</c> chain to close the job row, which can lag or, if that delivery is
    /// degraded, never arrive. The polled instance's OWN status is the right one: the row belongs to
    /// it, and a parent is Busy for its child's whole lifetime anyway.
    /// <b>(2) An active timeout job with a resolvable instant exists.</b> Rows written before the
    /// <c>ExecuteAt</c> column existed are skipped rather than emitted without a time, matching the
    /// scheduled entries.
    /// <b>(3) The effective timeout resolves.</b> Same resolver the arm and the fire path call, so
    /// the published <c>target</c> is the state the runtime will actually move to.
    /// </para>
    /// <para>
    /// A past <c>executeAtUtc</c> is deliberately NOT suppressed: between the timeout firing and its
    /// pipeline settling the instance is Busy and the past instant is the honest answer. Filtering on
    /// wall clock would also make the body a function of time while its ETag is a function of state,
    /// leaving two different bodies under one validator.
    /// </para>
    /// </remarks>
    private InstanceTimeoutOutput? BuildTimeoutBlock(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        IReadOnlyCollection<InstanceJob> activeJobs)
    {
        if (instance.Status.IsTerminal)
            return null;

        var timeoutJob = activeJobs
            .Where(j => j.JobType == JobType.Timeout && j.ExecuteAt.HasValue)
            .OrderBy(j => j.ExecuteAt!.Value)
            .FirstOrDefault();

        if (timeoutJob is null)
            return null;

        var effectiveTimeout = instance.ResolveEffectiveTimeout(currentWorkflow, out var overrideMalformed);

        if (overrideMalformed)
            logger.TimeoutOverrideMalformed(instance.Id, instance.Flow);

        if (effectiveTimeout is null)
            return null;

        return new InstanceTimeoutOutput
        {
            Key = effectiveTimeout.Key,
            Target = effectiveTimeout.Target,
            ExecuteAtUtc = timeoutJob.ExecuteAt!.Value
        };
    }

    /// <summary>
    /// Resolves the client-workflow-manager interaction directives for the response, or null when none
    /// apply. Today this is the long-poll directive: emitted on the main-flow current state whenever the
    /// state declares <c>interaction.longPoll</c> and the caller is admitted by its authorization arm —
    /// either the <c>roles</c> grants (default-allow when no roles configured) or the <c>rule</c>
    /// condition script; the two are alternatives, the validator rejects both. A failed rule evaluation
    /// denies (fail-closed), same as notification rules. The <c>terminate</c> flag and
    /// <c>fallbackTimeoutSeconds</c> are surfaced as configured; the ack href is included only when
    /// <c>terminate</c> is true (the pipeline pauses awaiting acknowledge in that case).
    /// </summary>
    private async Task<InstanceInteractionOutput?> ResolveInteractionAsync(
        GetInstanceStateInput input,
        Instance instance,
        Definitions.Workflow currentWorkflow,
        State currentStateValue,
        string? displayedState,
        CancellationToken cancellationToken)
    {
        if (currentStateValue.Interaction?.LongPoll is null)
            return null;

        // The block describes an acknowledgement that is ACTUALLY OUTSTANDING, not one the state
        // declares it may one day arm. Before this check it was emitted from the DEFINITION alone:
        // measured on the bench, a leaf reported status A with a full interaction block including
        // ack.href while `LongPollAckToken` was already cleared — the fallback had resumed the
        // pipeline — and the acknowledge endpoint answered 200 idempotently. A client could not tell
        // "there is an ack waiting for you" from "this state can pause", which is the only question
        // the block exists to answer.
        //
        // ETag safety: the token's lifetime is bracketed by status changes on both paths that clear
        // it — an acknowledge and the fallback job both resume the pipeline — and Status IS a
        // fingerprint member, so the block's disappearance always rides a fingerprint change. If a
        // future path ever clears the token WITHOUT a status change, the block would go stale behind
        // a 304; that path does not exist today and adding one would need a fingerprint member.
        if (!instance.IsAwaitingLongPollAck)
            return null;

        // Only signal on the main-flow current state view, not a subflow terminal view.
        if (!string.Equals(displayedState, instance.CurrentState, StringComparison.Ordinal)
            && displayedState is not null)
        {
            // displayedState may be a role alias of the current state; still allow when it aliases it.
            if (currentStateValue.Aliases.Count == 0)
                return null;
        }

        // The gate owns the arm selection (rule, else roles, else allow). This surface's roles are
        // already provider-resolved on the input, so the factory just hands them over.
        var admitted = await longPollInteractionGate.IsAdmittedAsync(
            instance, currentWorkflow, currentStateValue, input.Headers, input.QueryParams,
            _ => Task.FromResult(Result<IReadOnlyCollection<string>>.Ok(
                input.Roles ?? (string.IsNullOrWhiteSpace(input.Role) ? [] : [input.Role]))),
            surface: "state", cancellationToken);
        if (admitted is not { IsSuccess: true, Value: true })
            return null;

        var terminate = currentStateValue.TerminatesLongPollOnEntry;
        return new InstanceInteractionOutput
        {
            TerminateLongPoll = terminate,
            FallbackTimeoutSeconds = currentStateValue.LongPollFallbackTimeoutSeconds,
            Ack = terminate
                ? new AckHref
                {
                    Href = urlTemplateBuilder.BuildLongPollAckUrl(
                        input.Domain, input.Workflow, instance.Id.ToString())
                }
                : null
        };
    }

    /// <summary>
    /// Resolves the role-appropriate display value for a state's aliases. Aliases are evaluated in
    /// declaration order; the first whose role grants resolve to the caller wins. An alias with no
    /// role grants matches everyone (default/fallback). For the winning alias the localized label for
    /// <paramref name="culture"/> is returned (exact → neutral → English → first), falling back to the
    /// alias name when it has no labels. Returns null when no alias matches, so the caller falls back
    /// to the raw state key.
    /// </summary>
    private async Task<string?> ResolveStateAliasDisplayAsync(
        State state,
        Instance instance,
        IReadOnlyCollection<string>? callerRoles,
        string culture,
        AuthorizationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        foreach (var alias in state.Aliases)
        {
            var allowed = await transitionAuthorizationManager.IsRoleAllowedForGrantsAsync(
                callerRoles, alias.Roles, instance, requestContext, cancellationToken);
            if (allowed)
                return alias.Labels.ResolveLabel(culture) ?? alias.Name;
        }

        return null;
    }

    /// <summary>
    /// Extracts extension parameters from a data href URL.
    /// </summary>
    /// <param name="dataHref">The data href URL potentially containing extensions</param>
    /// <returns>Array of extension names extracted from the URL</returns>
    private static string[] ExtractExtensionsFromDataHref(string? dataHref)
    {
        if (string.IsNullOrEmpty(dataHref))
        {
            return [];
        }

        var queryIndex = dataHref.IndexOf('?');
        if (queryIndex == -1)
        {
            return [];
        }

        var query = dataHref.Substring(queryIndex);
        var queryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);

        if (queryParams.TryGetValue("extensions", out var extensionsValues))
        {
            return extensionsValues.SelectMany(v => v?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
                .ToArray();
        }

        return [];
    }

    public async Task<Result<GetViewOutput>> GetViewAsync(
        GetViewInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.View, input.Domain, input.Workflow);

        // Railway chain: Get Instance → Get Workflow → Resolve State → Get View
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .ThenAsync(async data =>
            {
                using var instanceScope = BeginInstanceScope(data.instance);
                return await ResolveViewAsync(data.instance, data.workflow, input, transitionKey, cancellationToken);
            });
    }

    /// <summary>
    /// Gets the schema definition for a specific transition in the workflow instance.
    /// </summary>
    /// <param name="input">The schema request input containing domain, workflow, and instance information</param>
    /// <param name="transitionKey">Optional transition key to get schema for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the schema output or error information</returns>
    public async Task<ConditionalResult<GetSchemaOutput>> GetSchemaAsync(
        GetSchemaInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.Schema, input.Domain, input.Workflow);

        // Fast path: the ETag is a deterministic hash of the data fingerprint (instance id +
        // latest data ETag + effective state + flow version) plus caller scope and transition
        // key — transition resolution is state-dependent. An If-None-Match match is answered
        // with 304 from a single projection query.
        string? schemaCacheKey = null;
        if (instanceSchemaFunctionCache.Enabled && !string.IsNullOrEmpty(transitionKey))
        {
            var fastResult = await TryServeSchemaFromFingerprintAsync(input, transitionKey, cancellationToken);
            if (fastResult.HasValue)
                return fastResult.Value;
            schemaCacheKey = instanceSchemaFunctionCache.BuildKey(input, transitionKey);
        }

        // Railway chain: Get Instance → Get Workflow → Build Schema Output
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    using var instanceScope = BeginInstanceScope(data.instance);
                    var buildResult = await BuildSchemaOutputAsync(data.instance, data.workflow, input, transitionKey, cancellationToken);
                    if (!buildResult.IsSuccess)
                        return ConditionalResult<GetSchemaOutput>.Fail(buildResult.Error);

                    return await FinalizeSchemaFunctionResultAsync(
                        "schema",
                        buildResult.Value!,
                        data.instance,
                        data.workflow,
                        schemaCacheKey,
                        instanceSchemaFunctionCache.ComputeEtag(input, InstanceDataFingerprint.FromInstance(data.instance), transitionKey!),
                        input.IfNoneMatch,
                        cancellationToken);
                },
                onFailure: error => ConditionalResult<GetSchemaOutput>.Fail(error));
    }

    /// <summary>
    /// Shared epilogue for the master/schema full paths: subflow-forwarded responses are
    /// returned as-is (never cached, never 304 — the body came from a live subflow call and
    /// its embedded ETag must not leak into the parent response); locally resolved responses
    /// are cached under the fingerprint ETag (warm before the 304 decision) and answered
    /// conditionally.
    /// </summary>
    private async Task<ConditionalResult<GetSchemaOutput>> FinalizeSchemaFunctionResultAsync(
        string function,
        GetSchemaOutput output,
        Instance instance,
        Definitions.Workflow flow,
        string? cacheKey,
        string etag,
        string? ifNoneMatch,
        CancellationToken cancellationToken)
    {
        if (instance.HasActiveSubFlow)
        {
            output.ETag = null;
            return ConditionalResult<GetSchemaOutput>.Success(output);
        }

        if (cacheKey is not null)
        {
            await instanceSchemaFunctionCache.SetAsync(cacheKey, new Caching.SchemaFunctionCacheEntry
            {
                Etag = etag,
                Output = output
            }, instanceSchemaFunctionCache.ResolveTtlSeconds(flow.Config?.FunctionCache), cancellationToken);
        }

        if (!string.IsNullOrEmpty(ifNoneMatch) && etag.MatchesIfNoneMatch(ifNoneMatch))
            return ConditionalResult<GetSchemaOutput>.NotModified();

        output.ETag = etag;
        return ConditionalResult<GetSchemaOutput>.Success(output);
    }

    /// <summary>
    /// Fast path for the schema function over the data fingerprint: 304 when If-None-Match
    /// matches the fingerprint ETag, cached response when the stored entry carries the same
    /// ETag. Bypassed entirely when the instance has an active SubFlow (live evaluation).
    /// Returns null when the full build path must run.
    /// </summary>
    private async Task<ConditionalResult<GetSchemaOutput>?> TryServeSchemaFromFingerprintAsync(
        GetSchemaInput input,
        string transitionKey,
        CancellationToken cancellationToken)
    {
        var fingerprint = await instanceRepository.GetDataFingerprintAsync(input.Instance, cancellationToken);
        if (fingerprint is null)
            return null;

        if (fingerprint.HasActiveSubFlow)
        {
            logger.InstanceSchemaFunctionCacheBypassedForSubFlow("schema", input.Instance);
            return null;
        }

        var etag = instanceSchemaFunctionCache.ComputeEtag(input, fingerprint, transitionKey);

        if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
        {
            logger.InstanceSchemaFunctionEtagNotModified("schema", input.Instance);
            return ConditionalResult<GetSchemaOutput>.NotModified();
        }

        var entry = await instanceSchemaFunctionCache.GetAsync(
            instanceSchemaFunctionCache.BuildKey(input, transitionKey), cancellationToken);
        if (entry is null)
        {
            logger.InstanceSchemaFunctionCacheMiss("schema", input.Instance);
            return null;
        }

        if (!string.Equals(entry.Etag, etag, StringComparison.Ordinal))
        {
            logger.InstanceSchemaFunctionCacheInvalidated("schema", input.Instance, entry.Etag, etag);
            return null;
        }

        logger.InstanceSchemaFunctionCacheHit("schema", input.Instance);

        var output = entry.Output;
        output.ETag = entry.Etag;
        return ConditionalResult<GetSchemaOutput>.Success(output);
    }

    /// <summary>
    /// Retrieves and executes extensions for an instance.
    /// </summary>
    /// <param name="input">The extensions request input containing domain, workflow, instance, and extensions to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the executed extension results or error information</returns>
    public async Task<Result<GetExtensionsOutput>> GetExtensionsAsync(
        GetExtensionsInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.Extensions, input.Domain, input.Workflow);

        // Railway chain: Get Instance → Get Workflow → Build Extensions Output
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .ThenAsync(async data =>
            {
                using var instanceScope = BeginInstanceScope(data.instance);
                return await BuildExtensionsOutputAsync(data.instance, data.workflow, input, cancellationToken);
            });
    }

    /// <summary>
    /// Retrieves the flow-level master schema an instance is bound to.
    /// If the instance has an active SubFlow, forwards the request to the SubFlow instance.
    /// </summary>
    public async Task<ConditionalResult<GetSchemaOutput>> GetMasterAsync(
        GetMasterInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.Master, input.Domain, input.Workflow);

        // Fast path: the master ETag is a deterministic hash of the data fingerprint
        // (instance id + latest data ETag + flow version) plus the caller scope — the
        // flow-level master schema is state-independent. An If-None-Match match is answered
        // with 304 from a single projection query.
        string? masterCacheKey = null;
        if (instanceSchemaFunctionCache.Enabled)
        {
            var fastResult = await TryServeMasterFromFingerprintAsync(input, cancellationToken);
            if (fastResult.HasValue)
                return fastResult.Value;
            masterCacheKey = instanceSchemaFunctionCache.BuildKey(input);
        }

        // Railway chain: Get Instance → Get Workflow → Build Master Schema Output
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    using var instanceScope = BeginInstanceScope(data.instance);
                    var buildResult = await BuildMasterOutputAsync(data.instance, data.workflow, input, cancellationToken);
                    if (!buildResult.IsSuccess)
                        return ConditionalResult<GetSchemaOutput>.Fail(buildResult.Error);

                    return await FinalizeSchemaFunctionResultAsync(
                        "master",
                        buildResult.Value!,
                        data.instance,
                        data.workflow,
                        masterCacheKey,
                        instanceSchemaFunctionCache.ComputeEtag(input, InstanceDataFingerprint.FromInstance(data.instance)),
                        input.IfNoneMatch,
                        cancellationToken);
                },
                onFailure: error => ConditionalResult<GetSchemaOutput>.Fail(error));
    }

    /// <summary>
    /// Fast path for the master function over the data fingerprint: 304 when If-None-Match
    /// matches the fingerprint ETag, cached response when the stored entry carries the same
    /// ETag. Bypassed entirely when the instance has an active SubFlow (live evaluation).
    /// Returns null when the full build path must run.
    /// </summary>
    private async Task<ConditionalResult<GetSchemaOutput>?> TryServeMasterFromFingerprintAsync(
        GetMasterInput input,
        CancellationToken cancellationToken)
    {
        var fingerprint = await instanceRepository.GetDataFingerprintAsync(input.Instance, cancellationToken);
        if (fingerprint is null)
            return null;

        if (fingerprint.HasActiveSubFlow)
        {
            logger.InstanceSchemaFunctionCacheBypassedForSubFlow("master", input.Instance);
            return null;
        }

        var etag = instanceSchemaFunctionCache.ComputeEtag(input, fingerprint);

        if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
        {
            logger.InstanceSchemaFunctionEtagNotModified("master", input.Instance);
            return ConditionalResult<GetSchemaOutput>.NotModified();
        }

        var entry = await instanceSchemaFunctionCache.GetAsync(
            instanceSchemaFunctionCache.BuildKey(input), cancellationToken);
        if (entry is null)
        {
            logger.InstanceSchemaFunctionCacheMiss("master", input.Instance);
            return null;
        }

        if (!string.Equals(entry.Etag, etag, StringComparison.Ordinal))
        {
            logger.InstanceSchemaFunctionCacheInvalidated("master", input.Instance, entry.Etag, etag);
            return null;
        }

        logger.InstanceSchemaFunctionCacheHit("master", input.Instance);

        var output = entry.Output;
        output.ETag = entry.Etag;
        return ConditionalResult<GetSchemaOutput>.Success(output);
    }

    /// <summary>
    /// Builds the flow-level master schema output.
    /// If the instance has an active SubFlow, forwards the request to the SubFlow instance.
    /// </summary>
    private async Task<Result<GetSchemaOutput>> BuildMasterOutputAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetMasterInput input,
        CancellationToken cancellationToken)
    {

        // Check if there's an active SubFlow - if so, forward the request to SubFlow
        // instance.Subflow returns the first active subflow (Type: S and not completed)
        if (instance.Subflow != null)
        {
            return await GetSubFlowMasterAsync(instance.Subflow, input, cancellationToken);
        }

        // No active SubFlow - resolve the flow-level master schema reference
        if (currentWorkflow.Schema == null)
        {
            return Result<GetSchemaOutput>.Fail(
                Error.NotFound("notfound",
                    $"Master schema not found for workflow {input.Workflow}"));
        }

        return await componentCacheStore.GetSchemaAsync(currentWorkflow.Schema, cancellationToken)
            .MapAsync(schema => new GetSchemaOutput
            {
                Key = schema.Key,
                Type = schema.Type,
                Schema = schema.Schema
            });
    }

    /// <summary>
    /// Gets the master schema from a remote SubFlow instance.
    /// </summary>
    private async Task<Result<GetSchemaOutput>> GetSubFlowMasterAsync(
        InstanceCorrelation subflow,
        GetMasterInput input,
        CancellationToken cancellationToken)
    {
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(input.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result<GetSchemaOutput>.Fail(callerRoles.Error);

        var subFlowInput = new GetFunctionWithInstanceInput
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            Version = subflow.SubFlowVersion,
            Instance = subflow.SubFlowInstanceId.ToString(),
            Headers = input.Headers ?? new Dictionary<string, string?>(),
            QueryParams = input.QueryParameters ?? new Dictionary<string, string?>(),
            Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
            Roles = callerRoles.Value
        };

        using var descent = StartDescend(
            subflow.SubFlowDomain,
            subflow.SubFlowName,
            subflow.SubFlowInstanceId.ToString(),
            subflow.ParentInstanceId.ToString(),
            TelemetryConstants.DescentFunctions.Master);

        return await instanceQueryGateway.GetFunctionWithMasterAsync(subFlowInput, cancellationToken);
    }

    /// <summary>
    /// Builds the extensions output by executing the requested extensions.
    /// If the instance has an active SubFlow, forwards the request to the SubFlow.
    /// </summary>
    private async Task<Result<GetExtensionsOutput>> BuildExtensionsOutputAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetExtensionsInput input,
        CancellationToken cancellationToken)
    {
        // Check if there's an active SubFlow - if so, forward the request to SubFlow
        // instance.Subflow returns the first active subflow (Type: S and not completed)
        if (instance.Subflow != null)
        {
            return await GetSubFlowExtensionsAsync(instance.Subflow, input.Extensions, cancellationToken);
        }

        // No active SubFlow - handle locally
        var instanceData = instance.LatestData;

        // Build script context for extension execution
        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(currentWorkflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithTransition(string.Empty)
            .WithBody(instanceData?.Data ?? new JsonData("{}"))
            .WithHeaders(input.Headers)
            .WithQueryParameters(input.QueryParameters)
            .BuildAsync(cancellationToken);

        // Execute extensions with fail-fast behavior
        var extensionsResult = await instanceExtensionService.ProcessExtensionsAsync(
            input.Extensions ?? [],
            scriptContext,
            currentWorkflow,
            ExtensionScope.GetInstance,
            cancellationToken);

        // Propagate extension errors - fail-fast behavior
        if (!extensionsResult.IsSuccess)
        {
            return Result<GetExtensionsOutput>.Fail(extensionsResult.Error);
        }

        // Return extension results
        return Result<GetExtensionsOutput>.Ok(new GetExtensionsOutput
        {
            Extensions = extensionsResult.Value!
        });
    }

    /// <summary>
    /// Gets extensions from a remote SubFlow instance.
    /// </summary>
    private async Task<Result<GetExtensionsOutput>> GetSubFlowExtensionsAsync(
        InstanceCorrelation subflow,
        string[]? extensions,
        CancellationToken cancellationToken)
    {
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(null, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result<GetExtensionsOutput>.Fail(callerRoles.Error);

        var subFlowInput = new GetFunctionWithInstanceInput
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            Version = subflow.SubFlowVersion,
            Instance = subflow.SubFlowInstanceId.ToString(),
            Extensions = extensions,
            Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
            Roles = callerRoles.Value
        };

        using var descent = StartDescend(
            subflow.SubFlowDomain,
            subflow.SubFlowName,
            subflow.SubFlowInstanceId.ToString(),
            subflow.ParentInstanceId.ToString(),
            TelemetryConstants.DescentFunctions.Extensions);

        return await instanceQueryGateway.GetFunctionWithExtensionsAsync(
            subFlowInput,
            cancellationToken);
    }

    /// <summary>
    /// Builds the schema output for a specific transition.
    /// If the instance has an active SubFlow, forwards the request to the SubFlow.
    /// Handles state resolution and schema lookup using Railway pattern.
    /// </summary>
    private async Task<Result<GetSchemaOutput>> BuildSchemaOutputAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetSchemaInput input,
        string? transitionKey,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrEmpty(transitionKey))
        {
            return Result<GetSchemaOutput>.Fail(
                Error.Validation("validation", "Transition key is required to get schema"));
        }

        // Check if there's an active SubFlow - if so, forward the request to SubFlow
        // instance.Subflow returns the first active subflow (Type: S and not completed)
        if (instance.Subflow != null)
        {
            return await GetSubFlowSchemaAsync(instance.Subflow, transitionKey, cancellationToken);
        }

        // No active SubFlow - handle locally
        // Get current state using Railway pattern
        var currentStateResult = currentWorkflow.GetState(instance.GetCurrentState);
        if (!currentStateResult.IsSuccess || currentStateResult.Value == null)
        {
            return Result<GetSchemaOutput>.Fail(
                Error.NotFound("notfound", $"State {instance.CurrentState} not found in workflow {input.Workflow}"));
        }

        var currentState = currentStateResult.Value;

        var transition = currentWorkflow.ResolveTransition(transitionKey, currentState);

        if (transition?.Schema == null)
        {
            return Result<GetSchemaOutput>.Fail(
                Error.NotFound("notfound",
                    $"Schema not found for transition {transitionKey} in state {instance.CurrentState}"));
        }

        // Fetch and return the schema using Railway pattern
        return await componentCacheStore.GetSchemaAsync(
                transition.Schema.Domain,
                transition.Schema.Key,
                transition.Schema.Version,
                cancellationToken)
            .MapAsync(schema => new GetSchemaOutput
            {
                Key = schema.Key,
                Type = schema.Type,
                Schema = schema.Schema
            });
    }

    /// <summary>
    /// Gets schema from a remote SubFlow instance.
    /// </summary>
    private async Task<Result<GetSchemaOutput>> GetSubFlowSchemaAsync(
        InstanceCorrelation subflow,
        string transitionKey,
        CancellationToken cancellationToken)
    {
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(null, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result<GetSchemaOutput>.Fail(callerRoles.Error);

        var subFlowInput = new GetFunctionWithInstanceInput
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            Version = subflow.SubFlowVersion,
            Instance = subflow.SubFlowInstanceId.ToString(),
            Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
            Roles = callerRoles.Value
        };

        using var descent = StartDescend(
            subflow.SubFlowDomain,
            subflow.SubFlowName,
            subflow.SubFlowInstanceId.ToString(),
            subflow.ParentInstanceId.ToString(),
            TelemetryConstants.DescentFunctions.Schema);

        return await instanceQueryGateway.GetFunctionWithSchemaAsync(
            subFlowInput,
            transitionKey,
            cancellationToken);
    }

    /// <summary>
    /// Resolves and returns the appropriate view for the instance.
    /// Handles subflow view overrides and platform-specific content.
    /// </summary>
    /// <summary>
    /// Resolves and returns the appropriate view for the instance using rule-based view selection.
    /// Iterates through view entries and evaluates rules to select the matching view.
    /// </summary>
    private async Task<Result<GetViewOutput>> ResolveViewAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetViewInput input,
        string? transitionKey,
        CancellationToken cancellationToken)
    {

        // Get current state using Railway pattern
        var currentStateResult = currentWorkflow.GetState(instance.CurrentState!);
        if (!currentStateResult.IsSuccess || currentStateResult.Value == null)
        {
            return Result<GetViewOutput>.Fail(
                Error.NotFound("notfound", $"State {instance.CurrentState} not found in workflow {input.Workflow}"));
        }

        var currentState = currentStateResult.Value;

        // If instance has active subflow, handle subflow view logic
        if (instance.HasActiveSubFlow)
        {
            var subFlowViewResult = await GetSubFlowViewWithOverrideAsync(
                instance,
                currentState,
                input.Domain,
                transitionKey,
                input.Role,
                input.Headers,
                input.QueryParameters,
                cancellationToken);

            if (subFlowViewResult != null)
            {
                return Result<GetViewOutput>.Ok(subFlowViewResult);
            }
        }

        // Get view definition
        var viewDefinition = GetViewDefinition(
            currentWorkflow,
            currentState,
            transitionKey);

        if (viewDefinition == null || viewDefinition.Views.Count == 0)
        {
            return Result<GetViewOutput>.Fail(
                Error.NotFound("notfound",
                    $"View definition not found for state {instance.CurrentState} in workflow {currentWorkflow.Key}"));
        }

        // Build script context for rule evaluation
        var instanceData = instance.LatestData;
        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(currentWorkflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithTransition(transitionKey ?? string.Empty)
            .WithBody(instanceData?.Data ?? new JsonData("{}"))
            .WithHeaders(input.Headers)
            .WithQueryParameters(input.QueryParameters)
            .BuildAsync(cancellationToken);

        // One span for the whole ordered walk, not one per rule: the question is "how many rules ran
        // and which won", and a span per rule turns a slow view definition into a wide trace rather
        // than a readable number. Warm rule evaluation is otherwise invisible — Script.Compile only
        // appears on a cold compile, so four evaluated rules and none look identical today.
        using var viewResolve = InstanceReadActivityHelper.StartViewResolve();
        var rulesEvaluated = 0;

        // Iterate through views array and evaluate rules
        ViewEntry? selectedViewEntry = null;
        foreach (var viewEntry in viewDefinition.Views)
        {
            // If no rule, treat as fallback and return immediately
            if (viewEntry.Rule == null)
            {
                selectedViewEntry = viewEntry;
                break;
            }

            // Evaluate rule using condition service
            rulesEvaluated++;
            var ruleResult = await taskConditionService.ExecuteConditionAsync(
                viewEntry.Rule,
                scriptContext,
                cancellationToken);

            if (ruleResult is { IsSuccess: true, Value: true })
            {
                selectedViewEntry = viewEntry;
                break;
            }

            // If rule evaluation failed, log and continue to next entry
            if (!ruleResult.IsSuccess)
            {
                logger.LogWarning(
                    "View rule evaluation failed for view {ViewKey} in state {StateKey}: {Error}",
                    viewEntry.View.Key,
                    instance.CurrentState,
                    ruleResult.Error.Message);
            }
        }

        InstanceReadActivityHelper.SetViewResolution(
            viewResolve, rulesEvaluated, selectedViewEntry?.View.Key);

        // If no matching view found, return error
        if (selectedViewEntry == null)
        {
            return Result<GetViewOutput>.Fail(
                Error.NotFound("notfound",
                    $"No matching view found for state {instance.CurrentState} in workflow {currentWorkflow.Key}"));
        }

        return await viewContentResolutionService.ResolveViewContentAsync(
            selectedViewEntry.View,
            input.Domain,
            input.Headers,
            input.QueryParameters,
            cancellationToken);
    }

    /// <summary>
    /// Gets the subflow view with override handling if applicable.
    /// Returns the subflow view if no override is needed, or the overridden view if override exists.
    /// </summary>
    /// <param name="instance">The workflow instance</param>
    /// <param name="currentState">The current state of the workflow</param>
    /// <param name="requestDomain">The request domain (for remote override resolution).</param>
    /// <param name="transitionKey"></param>
    /// <param name="headers">Request headers</param>
    /// <param name="queryParams">Request query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>GetViewOutput if subflow view is handled, null if should fall back to main flow view</returns>
    private async Task<GetViewOutput?> GetSubFlowViewWithOverrideAsync(
        Instance instance,
        State currentState,
        string requestDomain,
        string? transitionKey = null,
        string? role = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParams = null,
        CancellationToken cancellationToken = default)
    {
        // No failure channel here — the method already signals "no subflow view" with null, which is
        // also the closed answer when the caller's roles cannot be established.
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return null;

        using var descent = StartDescend(
            instance.Subflow!.SubFlowDomain,
            instance.Subflow!.SubFlowName,
            instance.Subflow!.SubFlowInstanceId.ToString(),
            instance.Id.ToString(),
            TelemetryConstants.DescentFunctions.View);

        var subFlowViewResult = await instanceQueryGateway.GetFunctionWithViewAsync(
            new GetFunctionWithInstanceInput
            {
                Instance = instance.Subflow!.SubFlowInstanceId.ToString(),
                Domain = instance.Subflow!.SubFlowDomain,
                Workflow = instance.Subflow!.SubFlowName,
                Version = instance.Subflow!.SubFlowVersion,
                Headers = headers ?? new Dictionary<string, string?>(),
                QueryParams = queryParams ?? new Dictionary<string, string?>(),
                Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
                Roles = callerRoles.Value
            },
            transitionKey,
            cancellationToken);

        if (!subFlowViewResult.IsSuccess)
        {
            // Falling back to the main-flow view is a normal outcome here, not an error — but an
            // unmarked fallback is indistinguishable from a descent that succeeded and returned
            // nothing, which is the exact confusion this span exists to remove.
            InstanceReadActivityHelper.SetUnresolved(descent.Activity, "subflow-view-unavailable");
            return null;
        }

        // If current state has view overrides, resolve override view (local or remote) via service
        // EffectiveViewOverrides: overrides.views takes precedence over legacy viewOverrides
        if (currentState.SubFlow?.HasViewOverrides == true)
        {
            var overrideViewRef = currentState.SubFlow!.EffectiveViewOverrides!.GetOrDefault(subFlowViewResult.Value!.Key);

            if (overrideViewRef != null)
            {
                var overrideResult = await viewContentResolutionService.ResolveViewContentAsync(
                    overrideViewRef,
                    requestDomain,
                    headers,
                    queryParams,
                    cancellationToken);
                if (overrideResult.IsSuccess)
                {
                    return overrideResult.Value!;
                }
                // Override resolution failed; fall back to subflow view below
            }
        }

        // Return subflow view directly (remote call already handled view selection)
        return subFlowViewResult.Value!;
    }

    /// <inheritdoc />
    public async Task<Result<GetInstanceHierarchyOutput>> GetInstanceHierarchyAsync(
        GetInstanceHierarchyInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.Hierarchy, input.Domain, input.Workflow);

        var instanceResult = await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken);
        if (!instanceResult.IsSuccess)
        {
            return Result<GetInstanceHierarchyOutput>.Fail(instanceResult.Error);
        }

        var instance = instanceResult.Value!;
        var flowResult = await componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken);
        var flowVersion = flowResult.IsSuccess ? flowResult.Value?.Version : null;

        var rootNode = new InstanceHierarchyNode
        {
            Id = instance.Id,
            Key = instance.Key,
            Flow = instance.Flow,
            Domain = input.Domain,
            FlowVersion = flowVersion ?? string.Empty,
            CurrentState = instance.CurrentState,
            Status = instance.Status,
            SubFlowType = null,
            IsCompleted = instance.Status == InstanceStatus.Completed,
            CompletedAt = instance.CompletedAt,
            ParentState = null
        };

        rootNode.Children = await BuildHierarchyTreeAsync(
            instance.Id,
            input.Workflow,
            input.Domain,
            cancellationToken);

        return Result<GetInstanceHierarchyOutput>.Ok(new GetInstanceHierarchyOutput { Root = rootNode });
    }

    /// <inheritdoc />
    public async Task<Result<HumanTask.HumanTaskListOutput>> GetHumanTaskInstancesAsync(
        string domain,
        IReadOnlyDictionary<string, string?>? headers = null,
        bool cacheOverride = false,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        var transaction = Activity.Current;
        using var read = InstanceReadActivityHelper.StartRead(
            InstanceReadKinds.HumanTasks, domain);

        var bounds = humanTaskOptions.Value;

        // Roles first, because they are cache-key material — and resolved once, because the fan-out
        // bodies open their own DI scopes, so a resolver read inside them would be a fresh memo and,
        // under a remote provider, one call per workflow.
        var callerRolesResult = await callerRoleResolver.ResolveRolesAsync(headers, cancellationToken);
        if (!callerRolesResult.IsSuccess)
            return Result<HumanTask.HumanTaskListOutput>.Fail(callerRolesResult.Error);
        var userRoles = callerRolesResult.Value ?? [];

        // Keyed on the caller scope, so the cache sits BEHIND the authorization filter and an entry
        // is only ever served back to the scope that produced it — which is what lets it hold
        // humanTask text at all. An override skips the READ and still takes the build gate and
        // writes the result: a full bypass would be a free way to make this endpoint more expensive
        // than it is with no cache, on a route with no rate limiter.
        var cacheEnabled = humanTaskFunctionCache.Enabled;
        var bypass = cacheOverride && humanTaskFunctionCache.AllowClientOverride;
        var cacheKey = cacheEnabled ? humanTaskFunctionCache.BuildKey(domain, userRoles, headers) : null;

        if (cacheEnabled && !bypass)
        {
            var cached = await humanTaskFunctionCache.GetAsync(cacheKey!, cancellationToken);
            if (cached is not null)
            {
                InstanceReadActivityHelper.SetReadOutcome(
                    transaction, InstanceReadKinds.HumanTasks, InstanceReadActivityHelper.FastPathCacheHit);
                return Result<HumanTask.HumanTaskListOutput>.Ok(new HumanTask.HumanTaskListOutput
                {
                    Items = cached.Items,
                    Truncated = cached.Truncated
                });
            }
        }

        InstanceReadActivityHelper.SetReadOutcome(
            transaction,
            InstanceReadKinds.HumanTasks,
            !cacheEnabled ? InstanceReadActivityHelper.FastPathDisabled
            : bypass ? HumanTaskBypassOutcome
            : InstanceReadActivityHelper.FastPathBuild);

        // Single-flight. A fixed TTL synchronises expiry, so without this every caller whose entry
        // expires in the same second starts its own full fan-out over every workflow schema.
        // Namespaced because the gate dictionary is a process-wide static shared with the
        // active-subflow gates.
        using var buildGate = cacheEnabled
            ? await AcquireBuildGateAsync($"human-task:{cacheKey}", cancellationToken)
            : null;

        if (cacheEnabled && !bypass && buildGate is { Contended: true })
        {
            // Someone else built it while this request waited at the gate.
            var cached = await humanTaskFunctionCache.GetAsync(cacheKey!, cancellationToken);
            if (cached is not null)
            {
                return Result<HumanTask.HumanTaskListOutput>.Ok(new HumanTask.HumanTaskListOutput
                {
                    Items = cached.Items,
                    Truncated = cached.Truncated
                });
            }
        }

        // Only now, on a real miss, is any database touched.
        List<string> workflowSchemas;
        using (currentSchema.Change(RuntimeSysSchemaInfo.Flows))
        {
            workflowSchemas = await instanceRepository.GetActiveFlowKeysAsync(cancellationToken);
        }

        read?.SetTag(TelemetryConstants.TagNames.HumanTaskSchemasScanned, workflowSchemas.Count);

        if (workflowSchemas.Count == 0)
            return Result<HumanTask.HumanTaskListOutput>.Ok(new HumanTask.HumanTaskListOutput());


        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var allItems = new System.Collections.Concurrent.ConcurrentBag<HumanTaskItemOutput>();

        // Counted rather than merely logged: a drop is invisible in the response by construction —
        // the list is simply shorter — so these are the only signal that an answer was incomplete.
        var candidateCount = 0;
        var droppedInstances = 0;
        var droppedWorkflows = 0;
        var schemaLimitHit = 0;

        // ── Phase A: ONE statement, ONE connection, every flow of the domain ──────────────────
        //
        // Every flow lives in a schema of the SAME database, so the per-flow scans differed only in
        // the name in the FROM clause. Running them as parallel branches bought a connection each
        // for nothing: the branch's whole body was one sub-millisecond index-only scan, and the
        // width of the fan-out became this endpoint's ceiling on connections taken from the pool.
        // Multiplied by concurrent callers — and every caller has their own cache key, so there is
        // no single-flight to collapse them — it exhausted the server's connection slots outright
        // (measured: 20 concurrent distinct callers over a 45-flow domain, 13 of them answered
        // 53300 "sorry, too many clients already").
        //
        // The work is not serialised by this, it MOVES: the database evaluates the UNION ALL arms
        // itself and answers in one round trip instead of N, so the scan is strictly faster than the
        // parallel form while holding one connection instead of FanoutParallelism of them.
        List<HumanTaskCandidate> allCandidates;
        using (var scan = InstanceReadActivityHelper.StartHumanTaskScan(domain))
        {
            allCandidates = await scopeFactory.ExecuteInIsolatedUnitOfWorkAsync(async (sp, innerCt) =>
            {
                var scopedRepo = sp.GetRequiredService<IInstanceRepository>();
                return await scopedRepo.GetHumanTaskCandidatesAcrossFlowsAsync(
                    workflowSchemas, bounds.PerSchemaLimit, bounds.FlowsPerScanStatement, innerCt);
            }, cancellationToken);

            scan?.SetTag(TelemetryConstants.TagNames.HumanTaskCandidates, allCandidates.Count);
        }

        if (allCandidates.Count == 0)
            return Result<HumanTask.HumanTaskListOutput>.Ok(new HumanTask.HumanTaskListOutput());

        // ── Phase B: descend, in parallel, over the flows that actually produced a candidate ──────
        //
        // This is the phase that genuinely needs the fan-out AND the isolation: a descent walks into
        // OTHER flows and other domains, loads aggregates with includes, and two branches can land
        // on the same flow — which is what a per-schema DbContext key cannot keep apart. It is also
        // far narrower than the scan was: a domain publishes many flows and only a few of them hold
        // human tasks at any moment, so the branch count is now the number of flows with work rather
        // than the number of flows that exist.
        var byFlow = allCandidates
            .GroupBy(c => c.Flow, StringComparer.Ordinal)
            .Select(g => (Flow: g.Key, Candidates: g.ToList()))
            .ToList();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = bounds.FanoutParallelism,
            CancellationToken = cancellationToken
        };

        // The candidate row is the ROOT: that is the identity the client holds and the only one it
        // can address. The work may be several SubFlow levels down and in another domain, so the
        // authorization decision and the humanTask text come from the LEAF. The walk is batched by
        // (domain, flow), so a domain boundary costs one call for that whole branch rather than one
        // per instance per level; its nested hops open their own scopes but inherit this unit of
        // work, which is correct because they run sequentially within the branch and it keeps open
        // connections bounded by the fan-out width instead of width × depth.
        await Parallel.ForEachAsync(byFlow, parallelOptions, async (group, ct) =>
        {
            var flowKey = group.Flow;
            var candidates = group.Candidates;

            Interlocked.Add(ref candidateCount, candidates.Count);
            if (candidates.Count >= bounds.PerSchemaLimit)
                Interlocked.Increment(ref schemaLimitHit);

            using var descend = InstanceReadActivityHelper.StartHumanTaskDescend(flowKey, candidates.Count);

            // Acquired BEFORE the unit of work, so a branch waiting for a slot is not waiting while
            // holding a connection. This is the only ceiling that spans requests; without it the
            // per-request width multiplies by however many callers happen to coincide.
            using var slot = await descentLimiter.AcquireAsync(ct);

            var descent = await scopeFactory.ExecuteInIsolatedUnitOfWorkAsync(async (sp, innerCt) =>
            {
                var resolver = sp.GetRequiredService<HumanTask.IHumanTaskLeafResolver>();
                return await resolver.ResolveAsync(
                    domain,
                    flowKey,
                    new HumanTask.HumanTaskLeafRequest
                    {
                        InstanceIds = [.. candidates.Select(c => c.Id)],
                        CallerRoles = userRoles,
                        Headers = headers is null
                            ? []
                            : new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase),
                        RemainingDepth = bounds.MaxDescentDepth
                    },
                    innerCt);
            }, ct);

            if (!descent.IsSuccess || descent.Value is null)
            {
                Interlocked.Add(ref droppedInstances, candidates.Count);
                logger.HumanTaskDescentHopFailed(domain, flowKey, descent.Error.Message);
                return;
            }

            var leafByRoot = descent.Value.ToDictionary(r => r.InstanceId);

            descend?.SetTag(
                TelemetryConstants.TagNames.HumanTaskResolved, descent.Value.Count(r => r.Resolved));
            descend?.SetTag(
                TelemetryConstants.TagNames.HumanTaskAuthorized, descent.Value.Count(r => r.Authorized));

            foreach (var candidate in candidates)
            {
                if (!leafByRoot.TryGetValue(candidate.Id, out var leaf) || !leaf.Resolved)
                {
                    // Already logged with its reason by the resolver. Counted because a dropped row
                    // and a row the caller may not act on look identical in the response.
                    Interlocked.Increment(ref droppedInstances);
                    continue;
                }

                if (!leaf.Authorized)
                    continue;

                allItems.Add(new HumanTaskItemOutput
                {
                    InstanceId = candidate.AddressableId,
                    Id = candidate.Id,
                    Workflow = flowKey,
                    Title = leaf.Title ?? string.Empty,
                    Description = leaf.Description ?? string.Empty,
                    CreatedAt = candidate.CreatedAt,
                    VNext = true
                });
            }
        });

        var ordered = allItems.OrderByDescending(x => x.CreatedAt).ToList();

        var capHit = ordered.Count > bounds.ResultCap;
        if (capHit)
        {
            logger.HumanTaskResultTruncated(domain, "result-cap", bounds.ResultCap, ordered.Count);
            ordered = ordered.Take(bounds.ResultCap).ToList();
        }
        else if (schemaLimitHit > 0)
        {
            logger.HumanTaskResultTruncated(domain, "per-schema", ordered.Count, candidateCount);
        }

        read?.SetTag(TelemetryConstants.TagNames.HumanTaskCandidates, candidateCount);
        read?.SetTag(TelemetryConstants.TagNames.HumanTaskReturned, ordered.Count);
        read?.SetTag(TelemetryConstants.TagNames.HumanTaskDropped, droppedInstances);
        read?.SetTag(TelemetryConstants.TagNames.HumanTaskWorkflowsDropped, droppedWorkflows);
        read?.SetTag(TelemetryConstants.TagNames.HumanTaskTruncated, capHit || schemaLimitHit > 0);

        var truncated = capHit || schemaLimitHit > 0;

        if (cacheEnabled)
        {
            await humanTaskFunctionCache.SetAsync(
                cacheKey!,
                new Caching.HumanTaskFunctionCacheEntry { Items = ordered, Truncated = truncated },
                cancellationToken);
        }

        return Result<HumanTask.HumanTaskListOutput>.Ok(new HumanTask.HumanTaskListOutput
        {
            Items = ordered,
            Truncated = truncated
        });
    }

    /// <summary>
    /// Read outcome for a request that skipped the cache read on the caller's request. Distinct from
    /// a miss so the two can be told apart when the override's cost is measured.
    /// </summary>
    private const string HumanTaskBypassOutcome = "bypass";

    private async Task<List<InstanceHierarchyNode>> BuildHierarchyTreeAsync(
        Guid parentInstanceId,
        string parentFlow,
        string domain,
        CancellationToken cancellationToken)
    {
        List<InstanceCorrelation> correlations;
        using (currentSchema.Change(parentFlow))
        {
            correlations = await instanceCorrelationRepository.GetByParentAsync(parentInstanceId, cancellationToken);
        }

        if (correlations.Count == 0)
        {
            return [];
        }

        var children = new List<InstanceHierarchyNode>();
        foreach (var correlation in correlations)
        {
            var childFlow = correlation.SubFlowName;
            var childDomain = correlation.SubFlowDomain;
            Instance? childInstance = null;

            using (currentSchema.Change(childFlow))
            {
                childInstance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
                    correlation.SubFlowInstanceId.ToString(),
                    cancellationToken);
            }

            var node = new InstanceHierarchyNode
            {
                Id = correlation.SubFlowInstanceId,
                Key = childInstance?.Key,
                Flow = childFlow,
                Domain = childDomain,
                FlowVersion = correlation.SubFlowVersion,
                CurrentState = correlation.SubFlowCurrentState ?? childInstance?.CurrentState,
                Status = childInstance?.Status ??
                         (correlation.IsCompleted ? InstanceStatus.Completed : InstanceStatus.Active),
                SubFlowType = correlation.SubFlowType,
                IsCompleted = correlation.IsCompleted,
                CompletedAt = correlation.CompletedAt,
                ParentState = correlation.ParentState
            };

            node.Children = await BuildHierarchyTreeAsync(
                correlation.SubFlowInstanceId,
                childFlow,
                childDomain,
                cancellationToken);

            children.Add(node);
        }

        return children;
    }

    /// <summary>
    /// Represents the complete state information retrieved from a SubFlow or main flow.
    /// Used to pass transitions, state, status, and additional SubFlow-specific data like view extensions and active correlations.
    /// </summary>
    /// <param name="AvailableTransitions">Available transitions from the flow</param>
    /// <param name="CurrentState">Current state of the flow</param>
    /// <param name="StateType">State type from the active SubFlow response, when applicable</param>
    /// <param name="Status">Status of the instance (always from main instance)</param>
    /// <param name="SubFlowData">Data href from SubFlow (contains extensions info) - null for main flow</param>
    /// <param name="SubFlowView">View href from SubFlow - null for main flow</param>
    /// <param name="SubFlowActiveCorrelations">Active correlations from SubFlow - empty for main flow</param>
    /// <param name="SubFlowCorrelations">Full correlation set (active + completed) from SubFlow - empty for main flow</param>
    /// <param name="SubFlowTransitionItems">Transition items from SubFlow (includes HasView) - null for main flow</param>
    private sealed record SubFlowStateInfo(
        List<string> AvailableTransitions,
        string? CurrentState,
        string? StateType,
        InstanceStatus? Status,
        DataHref? SubFlowData = null,
        ViewHref? SubFlowView = null,
        List<ActiveCorrelationHref>? SubFlowActiveCorrelations = null,
        List<ActiveCorrelationHref>? SubFlowCorrelations = null,
        List<TransitionItem>? SubFlowTransitionItems = null,
        InstanceInteractionOutput? Interaction = null,
        IncidentHref? Incident = null);

}
