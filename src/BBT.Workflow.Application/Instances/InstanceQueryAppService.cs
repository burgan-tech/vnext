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

namespace BBT.Workflow.Instances;

public sealed class InstanceQueryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceCorrelationRepository instanceCorrelationRepository,
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
    IPaginationLinkGenerator paginationLinkGenerator,
    ILogger<InstanceQueryAppService> logger)
    : ApplicationService(serviceProvider), IInstanceQueryAppService
{
    private static readonly HashSet<InstanceStatus> TerminalStatuses =
    [
        InstanceStatus.Completed,
        InstanceStatus.Faulted,
        InstanceStatus.Passive
    ];
    
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
                Role = role
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
                    Status: subFlowValue.Status,
                    SubFlowData: subFlowValue.Data,
                    SubFlowView: subFlowValue.View,
                    SubFlowActiveCorrelations: subFlowValue.ActiveCorrelations,
                    SubFlowTransitionItems: subFlowValue.Transitions);
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
    /// Merges subflow transition names with the parent workflow's shared transitions only (manual/event, available in current state).
    /// When in active subflow, clients see subflow transitions plus parent's shared transitions; state-level parent transitions are not included.
    /// </summary>
    private static List<string> MergeWithParentAvailableTransitions(
        List<string> subflowTransitionNames,
        Instance mainInstance,
        BBT.Workflow.Definitions.Workflow currentWorkflow)
    {
        var stateResult = currentWorkflow.GetState(mainInstance.GetCurrentState);
        if (!stateResult.IsSuccess)
            return subflowTransitionNames;

        var parentSharedOnly = currentWorkflow.GetAvailableSharedTransitionKeysOnly(stateResult.Value!);
        return subflowTransitionNames.Union(parentSharedOnly).ToList();
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
        return instance.EnsureNotNull(WorkflowErrors.InstanceNotFound(instanceIdentifier));
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
            await schemaFieldFilterService.ApplyAsync(flow, response.Attributes, instance, cancellationToken) ??
            response.Attributes;

        return Result<GetInstanceOutput>.Ok(response);
    }

    public async Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Railway chain: Get Instance → Load Flow (using instance.FlowVersion) → Match to ConditionalResult
        return await GetInstanceByIdOrKeyAsync(input.Instance, input.Version, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion, cancellationToken)
                    .MapAsync(workflow => (flow: workflow, instance)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    var (flow, instance) = data;
                    var instanceData = instance.FindData(input.Version);
                    var entityEtag = instanceData?.ETag ?? string.Empty;

                    var result = new GetInstanceDataOutput
                    {
                        Data = instanceData?.Data.JsonElement
                    };

                    result.Data = await schemaFieldFilterService.ApplyAsync(flow, result.Data, instance, cancellationToken) ??
                                  result.Data;

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
                    var representationEtag = representationEtagService.Generate(result);

                    if (!string.IsNullOrEmpty(input.IfNoneMatch) && representationEtag.MatchesIfNoneMatch(input.IfNoneMatch))
                    {
                        return ConditionalResult<GetInstanceDataOutput>.NotModified();
                    }

                    result.ETag = representationEtag;
                    return ConditionalResult<GetInstanceDataOutput>.Success(result);
                },
                onFailure: ConditionalResult<GetInstanceDataOutput>.Fail);
    }

    /// Gets the view definition for rule-based view selection.
    /// Returns the view definition from the transition (if transitionKey is provided) or from the state.
    /// </summary>
    private ViewDefinition? GetViewDefinition(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        State currentState,
        string? transitionKey = null)
    {
        if (!transitionKey.IsNullOrWhiteSpace())
        {
            var transition = currentWorkflow.ResolveTransition(transitionKey, currentState);
            return transition?.View;
        }

        bool isWizardState = currentState is { StateType: StateType.Wizard };

        // Get available transitions
        var availableTransitions = new List<string>();

        if (instance.Status.Equals(InstanceStatus.Active))
        {
            availableTransitions = currentWorkflow.GetAvailableUserTransitionKeys(currentState);
        }

        ViewDefinition? viewDefinition = null;

        // If there's exactly one transition, get its view
        if (isWizardState)
        {
            var transition = currentState.FindTransition(availableTransitions[0]);
            viewDefinition = transition?.View;
        }
        // If there are multiple transitions or no transitions, get the state view
        else if (!string.IsNullOrEmpty(instance.CurrentState) && currentState.View != null)
        {
            viewDefinition = currentState.View;
        }

        return viewDefinition;
    }

    public async Task<ConditionalResult<GetInstanceStateOutput>> GetInstanceStateAsync(
        GetInstanceStateInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    var buildResult = await BuildInstanceStateOutputAsync(data.instance, data.workflow, input, cancellationToken);
                    if (!buildResult.IsSuccess)
                        return ConditionalResult<GetInstanceStateOutput>.Fail(buildResult.Error);

                    var output = buildResult.Value!;
                    var entityEtag = data.instance.LatestData?.ETag ?? string.Empty;
                    output.EntityEtag = entityEtag;
                    var representationEtag = representationEtagService.Generate(output);

                    if (!string.IsNullOrEmpty(input.IfNoneMatch) && representationEtag.MatchesIfNoneMatch(input.IfNoneMatch))
                        return ConditionalResult<GetInstanceStateOutput>.NotModified();

                    output.ETag = representationEtag;
                    return ConditionalResult<GetInstanceStateOutput>.Success(output);
                },
                onFailure: error => ConditionalResult<GetInstanceStateOutput>.Fail(error));
    }

    /// <summary>
    /// Builds the complete instance state output including transitions, correlations, and view information.
    /// When there's an active SubFlow, includes the SubFlow's view extensions in data href and merges active correlations.
    /// </summary>
    private async Task<Result<GetInstanceStateOutput>> BuildInstanceStateOutputAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetInstanceStateInput input,
        CancellationToken cancellationToken)
    {
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
            var parentTransitionOverrides = TryGetParentTransitionRoleOverrides(instance);
            if (parentTransitionOverrides is { Count: > 0 })
            {
                var filteredKeys = new List<string>();
                foreach (var key in keysForTransitions)
                {
                    if (parentTransitionOverrides.TryGetValue(key, out var tOverride) &&
                        tOverride.Roles is { Count: > 0 })
                    {
                        // Parent override (replace mode): use parent-defined grants
                        var allowed = await transitionAuthorizationManager
                            .IsRoleAllowedForGrantsAsync(input.Role, tOverride.Roles!, instance, cancellationToken: cancellationToken);
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
                                currentWorkflow, currentStateValue, instance, [key], input.Role, cancellationToken);
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
                            currentWorkflow, currentStateValue, instance, parentSharedKeys, input.Role, cancellationToken))
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
                        currentWorkflow, currentStateValue, instance, keysForTransitions, input.Role, cancellationToken))
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
                    if (subFlowItem != null)
                    {
                        hasView = subFlowItem.View?.HasView ?? false;
                        loadData = subFlowItem.View?.LoadData ?? false;
                        hasSchema = subFlowItem.Schema?.HasSchema ?? false;
                    }
                    else
                    {
                        // Parent-owned transition (e.g., shared transition not from SubFlow): resolve from parent workflow
                        var transition = currentWorkflow.ResolveTransition(key, currentStateValue);
                        hasView = transition?.View is { Views.Count: > 0 };
                        loadData = false;
                        hasSchema = transition?.Schema != null;
                    }
                    return new TransitionItem
                    {
                        Name = key,
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
                        }
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
                    }
                };
            }).ToList();
        }

        var viewDefinition = GetViewDefinition(instance, currentWorkflow, currentStateValue);
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
        var mainFlowCorrelationHrefs = transitionInfo.ActiveCorrelations.Select(correlation =>
            new ActiveCorrelationHref
            {
                CorrelationId = correlation.CorrelationId,
                ParentState = correlation.ParentState,
                SubFlowInstanceId = correlation.SubFlowInstanceId,
                SubFlowType = correlation.SubFlowType,
                SubFlowDomain = correlation.SubFlowDomain,
                SubFlowName = correlation.SubFlowName,
                SubFlowVersion = correlation.SubFlowVersion,
                IsCompleted = correlation.IsCompleted,
                Href = allExtensions.Length > 0
                    ? urlTemplateBuilder.BuildDataWithExtensionsUrl(correlation.SubFlowDomain, correlation.SubFlowName,
                        correlation.SubFlowInstanceId.ToString(), allExtensions)
                    : urlTemplateBuilder.BuildDataUrl(correlation.SubFlowDomain, correlation.SubFlowName,
                        correlation.SubFlowInstanceId.ToString())
            }).ToList();
        var allActiveCorrelations = subFlowStateInfo.SubFlowActiveCorrelations != null
            ? mainFlowCorrelationHrefs.Concat(subFlowStateInfo.SubFlowActiveCorrelations).ToList()
            : mainFlowCorrelationHrefs;

        return Result<GetInstanceStateOutput>.Ok(new GetInstanceStateOutput
        {
            Data = dataHref,
            View = viewHref,
            State = subFlowStateInfo.CurrentState ?? string.Empty,
            Status = subFlowStateInfo.Status,
            ActiveCorrelations = allActiveCorrelations,
            Transitions = transitionItems
        });
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
    public async Task<Result<GetSchemaOutput>> GetSchemaAsync(
        GetSchemaInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Railway chain: Get Instance → Get Workflow → Build Schema Output
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, instance.FlowVersion ?? input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .ThenAsync(data =>
                BuildSchemaOutputAsync(data.instance, data.workflow, input, transitionKey, cancellationToken));
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
            Extensions = extensions
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
            Instance = subflow.SubFlowInstanceId.ToString()
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
                input.Headers,
                input.QueryParameters,
                cancellationToken);

            if (subFlowViewResult != null)
            {
                return Result<GetViewOutput>.Ok(subFlowViewResult);
            }
        }

        // Get view definition
        var viewDefinition = GetViewDefinition(instance, currentWorkflow, currentState, transitionKey);

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
                QueryParams = queryParams ?? new Dictionary<string, string?>()
            },
            transitionKey,
            cancellationToken);

        if (!subFlowViewResult.IsSuccess)
        {
            return null;
        }

        // If current state has view overrides, resolve override view (local or remote) via service
        // EffectiveViewOverrides: overrides.views takes precedence over legacy viewOverrides
        if (currentState.SubFlow!.HasViewOverrides)
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

    private async Task<List<InstanceHierarchyNode>> BuildHierarchyTreeAsync(
        Guid parentInstanceId,
        string parentFlow,
        string domain,
        CancellationToken cancellationToken)
    {
        List<InstanceCorrelation> correlations;
        using (currentSchema.Use(parentFlow))
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

            using (currentSchema.Use(childFlow))
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
    /// <param name="Status">Status of the instance (always from main instance)</param>
    /// <param name="SubFlowData">Data href from SubFlow (contains extensions info) - null for main flow</param>
    /// <param name="SubFlowView">View href from SubFlow - null for main flow</param>
    /// <param name="SubFlowActiveCorrelations">Active correlations from SubFlow - empty for main flow</param>
    /// <param name="SubFlowTransitionItems">Transition items from SubFlow (includes HasView) - null for main flow</param>
    private sealed record SubFlowStateInfo(
        List<string> AvailableTransitions,
        string? CurrentState,
        InstanceStatus? Status,
        DataHref? SubFlowData = null,
        ViewHref? SubFlowView = null,
        List<ActiveCorrelationHref>? SubFlowActiveCorrelations = null,
        List<TransitionItem>? SubFlowTransitionItems = null);

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