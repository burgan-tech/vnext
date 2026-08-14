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
using System.Text.Json;
using BBT.Aether.Application.Pagination;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Aether.MultiSchema;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances;

public sealed class InstanceQueryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceCorrelationRepository instanceCorrelationRepository,
    IInstanceJobRepository instanceJobRepository,
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
    ICurrentUser currentUser,
    IPaginationLinkGenerator paginationLinkGenerator,
    IOptions<InstanceFilteringOptions> instanceFilteringOptions,
    Caching.IStateFunctionCache stateFunctionCache,
    Caching.IDataFunctionCache dataFunctionCache,
    Caching.IInstanceSchemaFunctionCache instanceSchemaFunctionCache,
    ILogger<InstanceQueryAppService> logger)
    : ApplicationService(serviceProvider), IInstanceQueryAppService
{
    private static readonly HashSet<InstanceStatus> TerminalStatuses =
    [
        InstanceStatus.Completed,
        InstanceStatus.Faulted,
        InstanceStatus.Passive
    ];

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

    public async Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        return await GetInstanceByIdOrKeyAsync(input.Instance, input.Version, cancellationToken)
            .MatchAsync(
                onSuccess: async instance =>
                {
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
        runtimeInfoProvider.Check(input.Domain);

        return await ResultExtensions.TryAsync(
            async ct =>
            {
 
                // Resolve schema-driven filter/sort metadata from workflow's master schema
                SchemaFilterContext? schemaContext = null;
                var flowResult = await componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, null, ct);
                if (flowResult.IsSuccess && flowResult.Value?.Schema is not null)
                {
                    var schemaResult = await componentCacheStore.GetSchemaAsync(flowResult.Value.Schema, ct);
                    if (schemaResult.IsSuccess)
                        schemaContext = SchemaFilterMetadataResolver.Resolve(schemaResult.Value!.Schema);
                }

                if (!instanceFilteringOptions.Value.EnforceMasterSchemaFiltering)
                    schemaContext = null;

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

                // Normal flow: build instance outputs
                var list = new List<GetInstanceOutput>();
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
                        ct);

                    // Propagate extension errors - fail-fast behavior
                    if (!instanceOutputResult.IsSuccess)
                    {
                        throw new UserFriendlyException(
                            instanceOutputResult.Error.Code,
                            instanceOutputResult.Error.Message,
                            instanceOutputResult.Error.Detail).WithData("Target",
                            instanceOutputResult.Error.Target ?? string.Empty);
                    }

                    list.Add(instanceOutputResult.Value!);
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

    public async Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        return await GetInstanceWithFullHistoryAsync(input.Instance, cancellationToken)
            .ThenAsync(async instance =>
            {
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
            var subFlowInput = new GetFunctionWithInstanceInput
            {
                Domain = activeSubFlowCorrelation.SubFlowDomain,
                Workflow = activeSubFlowCorrelation.SubFlowName,
                Version = activeSubFlowCorrelation.SubFlowVersion,
                Instance = activeSubFlowCorrelation.SubFlowInstanceId.ToString(),
                Extensions = extensions,
                Headers = headers,
                QueryParams = queryParams,
                Role = currentUser.ResolveCallerRole(headers),
                Roles = currentUser.ResolveCallerRoles(headers)
            };

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
                    Interaction: subFlowValue.Interaction);
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
    /// in a SubFlow state (see <c>HandleUpdateDataPreflightStep</c>), so this merge is its primary surface.
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
        CancellationToken cancellationToken)
    {
        var flowResult =
            await componentCacheStore.GetFlowAsync(domain, workflow, instance.FlowVersion ?? null, cancellationToken);

        var flow = flowResult.IsSuccess ? flowResult.Value! : null;

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
        var extensionsResult = await instanceExtensionService.ProcessExtensionsAsync(
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
            await schemaFieldFilterService.ApplyAsync(
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
        var isLatestRequest = InstanceDataVersionComparer.IsRequestingLatest(input.Version);
        string? dataCacheKey = null;
        Caching.DataFunctionCacheEntry? validatedEntry = null;
        if (dataFunctionCache.Enabled && isLatestRequest)
        {
            var fastPath = await TryServeDataFromFingerprintAsync(input, cancellationToken);
            if (fastPath.NotModified.HasValue)
                return fastPath.NotModified.Value;
            validatedEntry = fastPath.ValidatedEntry;
            dataCacheKey = dataFunctionCache.BuildKey(input);
        }

        // Railway chain: Get Instance → Load Flow (using instance.FlowVersion) → Match to ConditionalResult
        return await GetInstanceByIdOrKeyAsync(input.Instance, input.Version, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken)
                    .MapAsync(workflow => (flow: workflow, instance)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    var (flow, instance) = data;

                    if (!await IsInstanceQueryAllowedAsync(flow, instance, input.Roles, input.Headers, input.QueryParameters, cancellationToken))
                        return ConditionalResult<GetInstanceDataOutput>.Fail(WorkflowErrors.QueryAccessDenied(instance.GetEffectiveState));

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

    /// <summary>
    /// <see cref="TransitionItem.Kind"/> value for a state-owned caller-triggerable transition
    /// (previously <c>stateTransition</c>; renamed with the v6 shape). Also the fallback kind when a
    /// key resolves to no other category.
    /// </summary>
    private const string ManualTransitionKind = "manual";

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
            return ManualTransitionKind;

        if (workflow.FindSharedTransition(transitionKey) != null)
            return "sharedTransition";

        return ManualTransitionKind;
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
        string? stateCacheKey = null;
        if (stateFunctionCache.Enabled)
        {
            var fastResult = await TryServeStateFromFingerprintAsync(input, cancellationToken);
            if (fastResult.HasValue)
                return fastResult.Value;
            stateCacheKey = stateFunctionCache.BuildKey(input);
        }

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    using var rootScope = BeginRootIdScopeIfSubflow(data.instance);
                    if (!await IsInstanceQueryAllowedAsync(data.workflow, data.instance, input.Roles, input.Headers, input.QueryParams, cancellationToken))
                        return ConditionalResult<GetInstanceStateOutput>.Fail(WorkflowErrors.QueryAccessDenied(data.instance.GetEffectiveState));

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

                    // Warm the cache before the 304 decision so a Not-Modified outcome still
                    // stores the entry. Active-subflow responses are built from a live subflow
                    // call and cannot be validated by the local fingerprint — never cache them.
                    if (stateCacheKey is not null && !data.instance.HasActiveSubFlow)
                    {
                        await stateFunctionCache.SetAsync(stateCacheKey, new Caching.StateFunctionCacheEntry
                        {
                            Etag = etag,
                            EntityEtag = entityEtag,
                            Output = output
                        }, cancellationToken);
                    }

                    if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
                        return ConditionalResult<GetInstanceStateOutput>.NotModified();

                    output.ETag = etag;
                    return ConditionalResult<GetInstanceStateOutput>.Success(output);
                },
                onFailure: error => ConditionalResult<GetInstanceStateOutput>.Fail(error));
    }

    /// <summary>
    /// Long-poll fast path over the state fingerprint. Loads the lightweight projection, computes
    /// the deterministic fingerprint ETag and answers: 304 when the caller's If-None-Match matches
    /// (no cache access, no build), or the cached response when the stored entry carries the same
    /// ETag. Returns null when the full build path must run: instance not found (proper error
    /// comes from the full path), active subflow (live evaluation required), cache miss, or a
    /// stale cache entry.
    /// </summary>
    private async Task<ConditionalResult<GetInstanceStateOutput>?> TryServeStateFromFingerprintAsync(
        GetInstanceStateInput input,
        CancellationToken cancellationToken)
    {
        var fingerprint = await instanceRepository.GetStateFingerprintAsync(input.Instance, cancellationToken);
        if (fingerprint is null)
            return null;

        if (fingerprint.HasActiveSubFlow)
        {
            logger.StateFunctionCacheBypassedForSubFlow(input.Instance);
            return null;
        }

        var etag = stateFunctionCache.ComputeEtag(input, fingerprint);

        if (!string.IsNullOrEmpty(input.IfNoneMatch) && etag.MatchesIfNoneMatch(input.IfNoneMatch))
        {
            logger.StateFunctionEtagNotModified(input.Instance, fingerprint.EffectiveState, fingerprint.Status.Code);
            return ConditionalResult<GetInstanceStateOutput>.NotModified();
        }

        var entry = await stateFunctionCache.GetAsync(stateFunctionCache.BuildKey(input), cancellationToken);
        if (entry is null)
        {
            logger.StateFunctionCacheMiss(input.Instance);
            return null;
        }

        if (!string.Equals(entry.Etag, etag, StringComparison.Ordinal))
        {
            logger.StateFunctionCacheInvalidated(input.Instance, entry.Etag, etag);
            return null;
        }

        logger.StateFunctionCacheHit(input.Instance, fingerprint.EffectiveState, fingerprint.Status.Code);

        var output = entry.Output;
        output.EntityEtag = entry.EntityEtag;
        output.ETag = entry.Etag;
        return ConditionalResult<GetInstanceStateOutput>.Success(output);
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
        // Active scheduled-transition jobs feed the kind:"scheduled" entries of the transitions
        // response list only — deliberately NOT the fingerprint ETag (team decision, issue #864;
        // known staleness gap, see the ETag doc). Loaded here so the fast 304 path never pays for it.
        var activeScheduledTransitionJobs = (await instanceJobRepository
                .GetListActiveAsync(instance.Id, cancellationToken))
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
            var sfStatus = subFlowStateInfo.Status;
            var subFlowIsTerminal = sfStatus is not null && TerminalStatuses.Contains(sfStatus);

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

            var parentTransitionOverrides = TryGetParentTransitionRoleOverrides(instance);
            if (parentTransitionOverrides is { Count: > 0 })
            {
                var filteredKeys = new List<string>();
                foreach (var key in keysForTransitions)
                {
                    if (parentTransitionOverrides.TryGetValue(key, out var tOverride) &&
                        tOverride.Roles is { Count: > 0 })
                    {
                        // Parent override (replace mode): use parent-defined grants verbatim.
                        // No availableIn narrowing here — these overrides key off the SUBFLOW's
                        // transitions, so the parent's availableIn states would not apply to them.
                        var allowed = await transitionAuthorizationManager
                            .IsRoleAllowedForGrantsAsync(input.Role, tOverride.Roles!, instance, authRequestContext, cancellationToken);
                        if (allowed) filteredKeys.Add(key);
                    }
                    else
                    {
                        // No parent override: if the transition belongs to this workflow, apply own role filtering.
                        // If not found (e.g. it came from a deeper SubFlow like C), pass through — already filtered by C.
                        var ownTransition = currentWorkflow.FindTransitionInContext(key);
                        if (ownTransition != null)
                        {
                            var result = await transitionAuthorizationManager.FilterAuthorizedTransitionKeysAsync(
                                currentWorkflow, currentStateValue, instance, [key], input.Role, authRequestContext, cancellationToken);
                            filteredKeys.AddRange(result);
                        }
                        else
                        {
                            filteredKeys.Add(key);
                        }
                    }
                }
                keysForTransitions = filteredKeys;
            }
            else if (activeSubFlowCorrelation != null)
            {
                // Parent context with active SubFlow:
                // SubFlow transitions are already correctly role-filtered by the SubFlow itself.
                // Only apply parent-level filtering to parent-added shared transitions.
                var subFlowTransitionKeys = subFlowStateInfo.SubFlowTransitionItems?
                    .Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
                var parentSharedKeys = keysForTransitions
                    .Where(k => !subFlowTransitionKeys.Contains(k))
                    .ToList();
                var filteredParentSharedKeys = parentSharedKeys.Count > 0
                    ? (await transitionAuthorizationManager.FilterAuthorizedTransitionKeysAsync(
                            currentWorkflow, currentStateValue, instance, parentSharedKeys, input.Role, authRequestContext, cancellationToken))
                      .ToList()
                    : parentSharedKeys;
                keysForTransitions = keysForTransitions
                    .Where(k => subFlowTransitionKeys.Contains(k))
                    .Concat(filteredParentSharedKeys)
                    .ToList();
            }
            else
            {
                keysForTransitions = (await transitionAuthorizationManager.FilterAuthorizedTransitionKeysAsync(
                        currentWorkflow, currentStateValue, instance, keysForTransitions, input.Role, authRequestContext, cancellationToken))
                    .ToList();
            }
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
                currentStateValue, instance, input.Role, culture, requestContext, cancellationToken);
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
                input, instance, currentStateValue, displayedState, cancellationToken);

        // Only the flag and the link — never the list. Enumerating the functions means one component
        // read per declared function plus a role evaluation each, which this response cannot afford.
        var functionsHref = new FunctionsHref
        {
            HasFunctions = currentWorkflow.Functions.Count > 0,
            Href = urlTemplateBuilder.BuildFunctionCatalogUrl(
                input.Domain, input.Workflow, instance.Id.ToString())
        };

        // Scheduled entries ride in the same transitions list, appended after the caller-triggerable
        // ones; clients discriminate on kind ("scheduled" ⇒ executeAtUtc present, no href).
        transitionItems.AddRange(BuildScheduledTransitionEntries(activeScheduledTransitionJobs));

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
            Interaction = interaction
        });
    }

    /// <summary>
    /// Maps the instance's active scheduled-transition jobs to <c>kind: "scheduled"</c> entries of the
    /// response's <c>transitions</c> list, ordered by execution time ascending. No href/view/schema —
    /// callers cannot trigger a scheduled transition. Rows without an
    /// <see cref="InstanceJob.ExecuteAt"/> (persisted before the column existed) are omitted rather
    /// than emitted without a time — every scheduled entry carries an execution instant, and such rows
    /// age out as their jobs fire or are cancelled. Not role-filtered: a scheduled transition fires
    /// regardless of the caller, so the entries are facts about the instance, not caller capabilities.
    /// </summary>
    private static IEnumerable<TransitionItem> BuildScheduledTransitionEntries(
        IReadOnlyCollection<InstanceJob> activeScheduledTransitionJobs) =>
        activeScheduledTransitionJobs
            .Where(j => j.ExecuteAt.HasValue && !string.IsNullOrEmpty(j.TransitionKey))
            .OrderBy(j => j.ExecuteAt!.Value)
            .Select(j => new TransitionItem
            {
                Name = j.TransitionKey!,
                Kind = ScheduledTransitionKind,
                ExecuteAtUtc = j.ExecuteAt!.Value
            });

    /// <summary>
    /// Resolves the client-workflow-manager interaction directives for the response, or null when none
    /// apply. Today this is the long-poll directive: emitted on the main-flow current state whenever the
    /// state declares <c>interaction.longPoll</c> and the caller's role is granted by
    /// <c>interaction.longPoll.roles</c> (default-allow when no roles configured). The <c>terminate</c>
    /// flag and <c>fallbackTimeoutSeconds</c> are surfaced as configured; the ack href is included only
    /// when <c>terminate</c> is true (the pipeline pauses awaiting acknowledge in that case).
    /// </summary>
    private async Task<InstanceInteractionOutput?> ResolveInteractionAsync(
        GetInstanceStateInput input,
        Instance instance,
        State currentStateValue,
        string? displayedState,
        CancellationToken cancellationToken)
    {
        if (currentStateValue.Interaction?.LongPoll is null)
            return null;

        // Only signal on the main-flow current state view, not a subflow terminal view.
        if (!string.Equals(displayedState, instance.CurrentState, StringComparison.Ordinal)
            && displayedState is not null)
        {
            // displayedState may be a role alias of the current state; still allow when it aliases it.
            if (currentStateValue.Aliases.Count == 0)
                return null;
        }

        var ackRoles = currentStateValue.LongPollAckRoles;
        if (ackRoles is { Count: > 0 })
        {
            var requestContext = new AuthorizationRequestContext(input.Headers, input.QueryParams);
            var callerRoles = input.Roles ?? (string.IsNullOrWhiteSpace(input.Role) ? [] : [input.Role]);
            var allowed = await transitionAuthorizationManager.IsAnyRoleAllowedForGrantsAsync(
                callerRoles, ackRoles, instance, requestContext, cancellationToken);
            if (!allowed)
                return null;
        }

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
    /// Enforces state/workflow <c>queryRoles</c> visibility for the instance query functions
    /// (state/data/view/schema). Returns true when access is permitted: no grants defined → allow;
    /// otherwise the caller's roles must resolve to an allow (DENY wins; predefined/dynamic roles honored).
    /// </summary>
    private async Task<bool> IsInstanceQueryAllowedAsync(
        Definitions.Workflow workflow,
        Instance instance,
        IReadOnlyCollection<string>? roles,
        IReadOnlyDictionary<string, string?>? headers,
        IReadOnlyDictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken)
    {
        var requestContext = new AuthorizationRequestContext(headers, queryParameters);
        return await transitionAuthorizationManager.IsQueryAllowedAsync(
            workflow, instance, roles, requestContext, cancellationToken);
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
        string? role,
        string culture,
        AuthorizationRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        foreach (var alias in state.Aliases)
        {
            var allowed = await transitionAuthorizationManager.IsRoleAllowedForGrantsAsync(
                role, alias.Roles, instance, requestContext, cancellationToken);
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

        // Railway chain: Get Instance → Get Workflow → Resolve State → Get View
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .ThenAsync(data =>
                ResolveViewAsync(data.instance, data.workflow, input, transitionKey, cancellationToken));
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

        // Railway chain: Get Instance → Get Workflow → Build Extensions Output
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .ThenAsync(data =>
                BuildExtensionsOutputAsync(data.instance, data.workflow, input, cancellationToken));
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
        if (!await IsInstanceQueryAllowedAsync(currentWorkflow, instance, input.Roles, input.Headers, input.QueryParameters, cancellationToken))
            return Result<GetSchemaOutput>.Fail(WorkflowErrors.QueryAccessDenied(instance.GetEffectiveState));

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
        var subFlowInput = new GetFunctionWithInstanceInput
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            Version = subflow.SubFlowVersion,
            Instance = subflow.SubFlowInstanceId.ToString(),
            Headers = input.Headers ?? new Dictionary<string, string?>(),
            QueryParams = input.QueryParameters ?? new Dictionary<string, string?>(),
            Role = currentUser.ResolveCallerRole(input.Headers),
            Roles = currentUser.ResolveCallerRoles(input.Headers)
        };

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
        var subFlowInput = new GetFunctionWithInstanceInput
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            Version = subflow.SubFlowVersion,
            Instance = subflow.SubFlowInstanceId.ToString(),
            Extensions = extensions,
            Role = currentUser.ResolveCallerRole(null),
            Roles = currentUser.ResolveCallerRoles(null)
        };

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
        if (!await IsInstanceQueryAllowedAsync(currentWorkflow, instance, input.Roles, input.Headers, input.QueryParameters, cancellationToken))
            return Result<GetSchemaOutput>.Fail(WorkflowErrors.QueryAccessDenied(instance.GetEffectiveState));

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
        var subFlowInput = new GetFunctionWithInstanceInput
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            Version = subflow.SubFlowVersion,
            Instance = subflow.SubFlowInstanceId.ToString(),
            Role = currentUser.ResolveCallerRole(null),
            Roles = currentUser.ResolveCallerRoles(null)
        };

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
        if (!await IsInstanceQueryAllowedAsync(currentWorkflow, instance, input.Roles, input.Headers, input.QueryParameters, cancellationToken))
            return Result<GetViewOutput>.Fail(WorkflowErrors.QueryAccessDenied(instance.GetEffectiveState));

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
        var subFlowViewResult = await instanceQueryGateway.GetFunctionWithViewAsync(
            new GetFunctionWithInstanceInput
            {
                Instance = instance.Subflow!.SubFlowInstanceId.ToString(),
                Domain = instance.Subflow!.SubFlowDomain,
                Workflow = instance.Subflow!.SubFlowName,
                Version = instance.Subflow!.SubFlowVersion,
                Headers = headers ?? new Dictionary<string, string?>(),
                QueryParams = queryParams ?? new Dictionary<string, string?>(),
                Role = currentUser.ResolveCallerRole(headers),
                Roles = currentUser.ResolveCallerRoles(headers)
            },
            transitionKey,
            cancellationToken);

        if (!subFlowViewResult.IsSuccess)
        {
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
    public async Task<Result<List<HumanTaskItemOutput>>> GetHumanTaskInstancesAsync(
        string domain,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        List<InstanceKeyModel> workflowSchemas;
        using (currentSchema.Change(RuntimeSysSchemaInfo.Flows))
        {
            workflowSchemas = await instanceRepository.GetActiveInstanceKeysAsync(cancellationToken);
        }

        if (workflowSchemas.Count == 0)
            return Result<List<HumanTaskItemOutput>>.Ok([]);

        const int humanTaskFanoutParallelism = 10;

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        // Honor the legacy `role` header: a caller whose roles arrive only as a header must not be
        // treated as role-less, which would silently drop every task guarded by a role grant.
        var userRoles = currentUser.ResolveCallerRoles(headers) ?? [];
        var requestContext = new AuthorizationRequestContext(headers);
        var allItems = new System.Collections.Concurrent.ConcurrentBag<HumanTaskItemOutput>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = humanTaskFanoutParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(workflowSchemas, parallelOptions, async (schema, ct) =>
        {
            var flowResult = await scopeFactory.ExecuteInScopeRawAsync(async (sp, innerCt) =>
            {
                var cacheStore = sp.GetRequiredService<IComponentCacheStore>();
                return await cacheStore.GetFlowAsync(domain, schema.Key, schema.Version, innerCt);
            }, ct);

            if (!flowResult.IsSuccess || flowResult.Value == null)
                return;

            var currentWorkflow = flowResult.Value;

            var instances = await scopeFactory.ExecuteInScopeRawAsync(async (sp, innerCt) =>
            {
                var scopedSchema = sp.GetRequiredService<ICurrentSchema>();
                var scopedRepo = sp.GetRequiredService<IInstanceRepository>();

                using (scopedSchema.Change(schema.Key))
                {
                    return await scopedRepo.GetHumanTaskInstancesAsync(innerCt);
                }
            }, ct);

            if (instances.Count == 0)
                return;

            var filtered = await FilterAuthorizedInstancesAsync(
                instances, currentWorkflow, domain, userRoles, requestContext, ct);

            foreach (var instance in filtered)
            {
                var title = string.Empty;
                var description = string.Empty;

                var latestData = instance.LatestData;
                if (latestData?.Data != null
                    && latestData.Data.JsonElement.ValueKind == JsonValueKind.Object
                    && latestData.Data.JsonElement.TryGetProperty("humanTask", out var humanTaskElement)
                    && humanTaskElement.ValueKind == JsonValueKind.Object)
                {
                    if (humanTaskElement.TryGetProperty("title", out var titleProp))
                        title = titleProp.GetString() ?? string.Empty;
                    if (humanTaskElement.TryGetProperty("description", out var descProp))
                        description = descProp.GetString() ?? string.Empty;
                }

                allItems.Add(new HumanTaskItemOutput
                {
                    InstanceId = instance.Key ?? instance.Id.ToString(),
                    Workflow = schema.Key,
                    Title = title,
                    Description = description,
                    CreatedAt = instance.CreatedAt,
                    VNext = true
                });
            }
        });

        var ordered = allItems.OrderByDescending(x => x.CreatedAt).ToList();
        return Result<List<HumanTaskItemOutput>>.Ok(ordered);
    }

    /// <summary>
    /// Resolves the workflow and state for transition authorization.
    /// If the instance has an active SubFlow correlation, loads the sub-workflow
    /// and resolves the state from SubFlowCurrentState. Otherwise uses the parent workflow.
    /// </summary>
    private async Task<(Definitions.Workflow? Workflow, State? State)> ResolveInstanceWorkflowAndStateAsync(
        Instance instance,
        Definitions.Workflow parentWorkflow,
        string domain,
        CancellationToken cancellationToken)
    {
        var activeSubFlow = instance.Subflow;

        if (activeSubFlow == null)
        {
            var stateResult = parentWorkflow.GetState(instance.GetCurrentState);
            if (!stateResult.IsSuccess || stateResult.Value == null)
                return (null, null);

            return (parentWorkflow, stateResult.Value);
        }

        var subFlowResult = await componentCacheStore.GetFlowAsync(
            domain, activeSubFlow.SubFlowName, null, cancellationToken);

        if (!subFlowResult.IsSuccess || subFlowResult.Value == null)
            return (null, null);

        var subFlowState = activeSubFlow.SubFlowCurrentState;
        if (string.IsNullOrEmpty(subFlowState))
            return (null, null);

        var stateInSubFlow = subFlowResult.Value.GetState(subFlowState);
        if (!stateInSubFlow.IsSuccess || stateInSubFlow.Value == null)
            return (null, null);

        return (subFlowResult.Value, stateInSubFlow.Value);
    }

    /// <summary>
    /// Filters instances by checking whether the current user is authorized to trigger
    /// at least one transition. For each instance, resolves the correct workflow and state
    /// (main flow or active SubFlow via correlation), then evaluates that transition's grants through the
    /// shared role evaluator — the same decision the transition-execution path makes, so a task appears in
    /// the list exactly when its transition is executable.
    /// When a SubFlow transition is overridden by the parent, the parent's role grants are used instead.
    /// </summary>
    private async Task<List<Instance>> FilterAuthorizedInstancesAsync(
        List<Instance> instances,
        Definitions.Workflow parentWorkflow,
        string domain,
        string[] userRoles,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        var result = new List<Instance>(instances.Count);

        foreach (var instance in instances)
        {
            var (workflow, state) = await ResolveInstanceWorkflowAndStateAsync(
                instance, parentWorkflow, domain, cancellationToken);

            if (workflow == null || state == null)
                continue;

            var transitions = workflow.GetAvailableUserTransitionKeys(state);
            if (transitions.Count == 0)
                continue;

            var parentOverrides = GetParentTransitionOverrides(instance, parentWorkflow);

            // Resolve every candidate first so the evaluator's prefetch hint covers the whole instance:
            // one previous-transition fetch serves all of this instance's transitions and caller roles.
            var candidates = new List<(Transition Transition, IReadOnlyCollection<RoleGrant> Grants)>(transitions.Count);
            foreach (var transitionKey in transitions)
            {
                var transition = workflow.FindTransitionInContext(transitionKey);
                if (transition == null)
                    continue;

                var grants = parentOverrides != null
                             && parentOverrides.TryGetValue(transitionKey, out var tOverride)
                             && tOverride.Roles is { Count: > 0 }
                    ? tOverride.Roles!
                    : transition.Roles;

                candidates.Add((transition, grants));
            }

            if (candidates.Count == 0)
                continue;

            var evaluator = await transitionAuthorizationManager.CreateEvaluatorAsync(
                instance,
                workflow,
                requestContext,
                candidates.SelectMany(c => c.Grants),
                cancellationToken);

            var isAuthorized = candidates.Any(c =>
                evaluator.IsAnyRoleAllowed(userRoles, c.Grants, c.Transition));

            if (isAuthorized)
                result.Add(instance);
        }

        return result;
    }

    /// <summary>
    /// Gets parent-defined transition role overrides for instances in a SubFlow state.
    /// Returns null if the instance is not in a SubFlow or no overrides are defined.
    /// </summary>
    private static Dictionary<string, SubFlowTransitionOverride>? GetParentTransitionOverrides(
        Instance instance,
        Definitions.Workflow parentWorkflow)
    {
        if (instance.Subflow == null)
            return null;

        var parentStateResult = parentWorkflow.GetState(instance.GetCurrentState);
        if (!parentStateResult.IsSuccess || parentStateResult.Value?.SubFlow?.Overrides?.Transitions == null)
            return null;

        return parentStateResult.Value.SubFlow.Overrides.Transitions;
    }

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
        InstanceInteractionOutput? Interaction = null);

    private static Dictionary<string, SubFlowTransitionOverride>? TryGetParentTransitionRoleOverrides(Instance instance)
    {
        if (!instance.ExtraProperties.TryGetValue(DomainConsts.MetaDataKeys.TransitionRoleOverrides, out var raw) ||
            raw is null)
            return null;
        var json = raw.ToString();
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<Dictionary<string, SubFlowTransitionOverride>>(json);
    }
}
