using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
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
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Tasks.Coordinator;
namespace BBT.Workflow.Instances;

public sealed class InstanceQueryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceExtensionService instanceExtensionService,
    IScriptContextFactory scriptContextFactory,
    IInstanceQueryGateway instanceQueryGateway,
    ITaskConditionService taskConditionService,
    ILogger<InstanceQueryAppService> logger)
    : ApplicationService(serviceProvider), IInstanceQueryAppService
{
    public async Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .MatchAsync(
                onSuccess: async instance =>
                {
                    // Check ETag for conditional requests
                    if (!string.IsNullOrEmpty(input.IfNoneMatch) &&
                        instance.LatestData != null &&
                        instance.LatestData.ETag.MatchesIfNoneMatch(input.IfNoneMatch))
                    {
                        return ConditionalResult<GetInstanceOutput>.NotModified();
                    }

                    var result = await BuildInstanceOutputAsync(
                        input.Domain,
                        input.Extensions,
                        input.Workflow,
                        instance,
                        instance.LatestData,
                        ExtensionScope.GetInstance,
                        input.Headers,
                        input.QueryParameters,
                        cancellationToken);

                    // Propagate extension errors - fail-fast behavior
                    if (!result.IsSuccess)
                    {
                        return ConditionalResult<GetInstanceOutput>.Fail(result.Error);
                    }

                    return ConditionalResult<GetInstanceOutput>.Success(result.Value!);
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
                string[]? filters = input.Filter;
                string? groupBy = input.GroupBy;
                string? aggregations = input.Aggregations;

                // If filter array has one element, check if it's GraphQLFilterRequest format
                Definitions.GraphQL.GraphQLFilterRequest? parsedRequest = null;
                if (input.Filter != null && input.Filter.Length == 1 && string.IsNullOrWhiteSpace(groupBy))
                {
                    var filterString = input.Filter[0];
                    if (GraphQLFilterParser.TryParseRequest(filterString, out var request) && request != null)
                    {
                        parsedRequest = request;
                        // Extract filter and groupBy from GraphQLFilterRequest for backward compatibility path
                        filters = request.Filter != null
                            ? new[] { JsonSerializer.Serialize(request.Filter, new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                WriteIndented = false
                            }) }
                            : null;

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
                        filters,
                        groupBy,
                        aggregations,
                        ct);
                    pagedList = result.PagedList;
                    groups = result.Groups;
                }

                // If groups are present, populate items with groups instead of instances
                if (groups != null && groups.Count > 0)
                {
                    return InstanceListWithGroupsResponse<GetInstanceOutput>.FromGroups(groups);
                }

                // Normal flow: build instance outputs
                var list = new List<GetInstanceOutput>();
                foreach (var instance in pagedList.Items)
                {
                    var instanceOutputResult = await BuildInstanceOutputAsync(
                        input.Domain,
                        input.Extension,
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
                            instanceOutputResult.Error.Detail).WithData("Target", instanceOutputResult.Error.Target ?? string.Empty);
                    }

                    list.Add(instanceOutputResult.Value!);
                }

                var resultPagedList = new HateoasPagedList<GetInstanceOutput>(list, pagedList.CurrentPage, pagedList.PageSize,
                    pagedList.HasNext);

                return InstanceListWithGroupsResponse<GetInstanceOutput>.FromPagedList(resultPagedList, null);
            },
            cancellationToken);
    }

    public async Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .ThenAsync(async instance =>
            {
                var transitions = new List<GetInstanceOutput>();
                foreach (var instanceData in instance.DataList.OrderBy(d => d.EnteredAt))
                {
                    var transitionOutputResult = await BuildInstanceOutputAsync(
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
                    if (!transitionOutputResult.IsSuccess)
                    {
                        return Result<GetInstanceHistoryOutput>.Fail(transitionOutputResult.Error);
                    }

                    transitions.Add(transitionOutputResult.Value!);
                }

                return Result<GetInstanceHistoryOutput>.Ok(new GetInstanceHistoryOutput
                {
                    Transitions = transitions
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
                Extensions = extensions
            };

            var subFlowResult = await instanceQueryGateway.GetFunctionWithStateAsync(
                subFlowInput,
                cancellationToken);

            if (subFlowResult.IsSuccess && subFlowResult.Value != null)
            {
                var subFlowValue = subFlowResult.Value;
                
                // Extract transition names from TransitionItem list
                var transitionNames = subFlowValue.Transitions?
                    .Select(t => t.Name)
                    .ToList() ?? new List<string>();

                // Return complete SubFlow state including view extensions and active correlations
                return new SubFlowStateInfo(
                    AvailableTransitions: transitionNames,
                    CurrentState: subFlowValue.State,
                    Status: subFlowValue.Status,
                    SubFlowData: subFlowValue.Data,
                    SubFlowView: subFlowValue.View,
                    SubFlowActiveCorrelations: subFlowValue.ActiveCorrelations);
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
        var flowResult = await componentCacheStore.GetFlowAsync(domain, workflow, null, cancellationToken);

        var flow = flowResult.IsSuccess ? flowResult.Value! : null;

        var response = new GetInstanceOutput
        {
            Id = instance.Id,
            Flow = instance.Flow,
            FlowVersion = instanceData?.Version ?? string.Empty,
            Etag = instanceData?.ETag ?? string.Empty,
            Domain = domain,
            Key = instance.Key!,
            Tags = instance.Tags,
            Attributes = instanceData?.Data.JsonElement
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

        return Result<GetInstanceOutput>.Ok(response);
    }

    public async Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Railway chain: Load Flow → Get Instance → Match to ConditionalResult
        return await componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, null, cancellationToken)
            .BindAsync(workflow =>
                GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
                    .MapAsync(instance => (flow: workflow, instance)))
            .MatchAsync(
                onSuccess: async data =>
                {
                    var (flow, instance) = data;

                    // Check ETag for conditional requests
                    if (!string.IsNullOrEmpty(input.IfNoneMatch) &&
                        instance.LatestData != null &&
                        instance.LatestData.ETag.MatchesIfNoneMatch(input.IfNoneMatch))
                    {
                        return ConditionalResult<GetInstanceDataOutput>.NotModified();
                    }

                    var result = new GetInstanceDataOutput
                    {
                        Data = instance.LatestData?.Data.JsonElement,
                        Etag = instance.LatestData?.ETag ?? string.Empty
                    };

                    // If there's an active SubFlow and extensions are requested, fetch from SubFlow
                    if (instance.Subflow != null)
                    {
                        var subFlowExtensionsResult = await GetSubFlowExtensionsAsync(
                            instance.Subflow,
                            input.Extensions,
                            cancellationToken);

                        result.Extensions = subFlowExtensionsResult.Value?.Extensions ?? new Dictionary<string, object>();

                        return ConditionalResult<GetInstanceDataOutput>.Success(result);
                    }

                    // No active SubFlow - process extensions locally
                    var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                        .WithWorkflow(flow)
                        .WithInstance(instance)
                        .WithRuntime(runtimeInfoProvider)
                        .WithTransition(string.Empty)
                        .WithBody(instance.LatestData?.Data ?? new JsonData("{}"))
                        .WithHeaders(input.Headers)
                        .WithQueryParameters(input.QueryParameters)
                        .BuildAsync(cancellationToken);

                    // Execute extensions with fail-fast behavior
                    var extensionsResult = await instanceExtensionService.ProcessExtensionsAsync(
                        input.Extensions,
                        scriptContext,
                        flow,
                        ExtensionScope.GetInstance,
                        cancellationToken);

                    // Propagate extension errors - fail-fast behavior
                    if (!extensionsResult.IsSuccess)
                    {
                        return ConditionalResult<GetInstanceDataOutput>.Fail(extensionsResult.Error);
                    }

                    result.Extensions = extensionsResult.Value!;

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

    public async Task<Result<GetInstanceStateOutput>> GetInstanceStateAsync(
        GetInstanceStateInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Railway chain: Get Instance → Get Workflow → Build State Output
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .ThenAsync(data => BuildInstanceStateOutputAsync(data.instance, data.workflow, input, cancellationToken));
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

        var subFlowStateInfo = activeSubFlowCorrelation != null
            ? await GetSubFlowTransitionsAsync(activeSubFlowCorrelation, instance, currentWorkflow, input.Extensions,input.Headers,input.QueryParams, cancellationToken)
            : GetMainFlowTransitions(instance, currentWorkflow, transitionInfo);

        // Build transition items with href links
        var transitionItems = subFlowStateInfo.AvailableTransitions.Select(transitionKey => new TransitionItem
        {
            Name = transitionKey,
            Href = InstanceUrlTemplates.Transition(input.Domain, input.Workflow, instance.Id.ToString(), transitionKey),
            Schema = new HrefBase { Href = InstanceUrlTemplates.Schema(input.Domain, input.Workflow, instance.Id.ToString(), transitionKey) }
        }).ToList();

        // Get current state using Railway pattern
        return currentWorkflow.GetState(instance.CurrentState!)
            .Ensure(
                state => state != null,
                Error.NotFound("notfound", $"State {instance.CurrentState} not found in workflow {input.Workflow}"))
           .Map(currentStateValue =>
            {
                // Get view definition from main flow
                var viewDefinition = GetViewDefinition(instance, currentWorkflow, currentStateValue);

                // Get extensions and loadData from first view entry (if available)
                // For state output, we use the first/default view entry's extensions
                var firstViewEntry = viewDefinition?.Views.FirstOrDefault();
                var viewExtensions = firstViewEntry?.Extensions ?? [];
                var viewLoadData = firstViewEntry?.LoadData ?? false;

                // Build data href with extensions
                // If SubFlow is active, use SubFlow's extensions; otherwise use main flow extensions
                var allExtensions = subFlowStateInfo.SubFlowData != null
                    ? ExtractExtensionsFromDataHref(subFlowStateInfo.SubFlowData.Href)
                    : (input.Extensions ?? []).Concat(viewExtensions).ToArray();

                var dataHref = new DataHref
                {
                    Href = allExtensions.Length > 0
                        ? InstanceUrlTemplates.DataWithExtensions(input.Domain, input.Workflow, instance.Id.ToString(),
                            allExtensions)
                        : InstanceUrlTemplates.Data(input.Domain, input.Workflow, instance.Id.ToString())
                };

                // Build view href - use SubFlow view's LoadData if available, otherwise use main flow
                var viewHref = new ViewHref
                {
                    Href = InstanceUrlTemplates.View(input.Domain, input.Workflow, instance.Id.ToString()),
                    LoadData = subFlowStateInfo.SubFlowView?.LoadData ?? viewLoadData
                };

                // Build active correlations with href links from main flow
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
                            ? InstanceUrlTemplates.DataWithExtensions(correlation.SubFlowDomain, correlation.SubFlowName,
                                correlation.SubFlowInstanceId.ToString(),
                                allExtensions)
                            : InstanceUrlTemplates.Data(correlation.SubFlowDomain, correlation.SubFlowName,
                                correlation.SubFlowInstanceId.ToString())
                    }).ToList();

                // Merge SubFlow active correlations if available
                var allActiveCorrelations = subFlowStateInfo.SubFlowActiveCorrelations != null
                    ? mainFlowCorrelationHrefs.Concat(subFlowStateInfo.SubFlowActiveCorrelations).ToList()
                    : mainFlowCorrelationHrefs;

                return new GetInstanceStateOutput
                {
                    Data = dataHref,
                    View = viewHref,
                    State = subFlowStateInfo.CurrentState ?? string.Empty,
                    Status = subFlowStateInfo.Status,
                    ActiveCorrelations = allActiveCorrelations,
                    Transitions = transitionItems,
                    ETag = instance.LatestData?.ETag ?? string.Empty
                };
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
            return extensionsValues.SelectMany(v => v?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? []).ToArray();
        }

        return [];
    }

    public async Task<Result<GetViewOutput>> GetPlatformSpecificViewAsync(
        GetViewInput input,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Railway chain: Get Instance → Get Workflow → Resolve State → Get View
        return await GetInstanceByIdOrKeyAsync(input.Instance, cancellationToken)
            .BindAsync(instance =>
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, input.Version, cancellationToken)
                    .MapAsync(workflow => (instance, workflow)))
            .ThenAsync(data =>
                ResolveViewAsync(data.instance, data.workflow, input, platform, transitionKey, cancellationToken));
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
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, input.Version, cancellationToken)
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
                componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, input.Version, cancellationToken)
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
        string? platform,
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
                platform,
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

            if (ruleResult.IsSuccess && ruleResult.Value)
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

        // Fetch and return the view
        return await componentCacheStore.GetViewAsync(
                selectedViewEntry.View.Domain,
                selectedViewEntry.View.Key,
                selectedViewEntry.View.Version,
                cancellationToken)
            .MapAsync(view => BuildViewOutput(view, selectedViewEntry));
    }

    /// <summary>
    /// Gets the subflow view with override handling if applicable.
    /// Returns the subflow view if no override is needed, or the overridden view if override exists.
    /// </summary>
    /// <param name="instance">The workflow instance</param>
    /// <param name="currentState">The current state of the workflow</param>
    /// <param name="platform">Platform identifier (optional)</param>
    /// <param name="transitionKey"></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>GetViewOutput if subflow view is handled, null if should fall back to main flow view</returns>
    private async Task<GetViewOutput?> GetSubFlowViewWithOverrideAsync(
        Instance instance,
        State currentState,
        string? platform,
        string? transitionKey = null,
        Dictionary<string, string?>? headers=null,
        Dictionary<string, string?>? queryParams=null, 
        CancellationToken cancellationToken = default)
    {
        var subFlowViewResult = await instanceQueryGateway.GetFunctionWithViewAsync(
            new GetFunctionWithInstanceInput
            {
                Instance = instance.Subflow!.SubFlowInstanceId.ToString(),
                Domain = instance.Subflow!.SubFlowDomain,
                Workflow = instance.Subflow!.SubFlowName,
                Version = instance.Subflow!.SubFlowVersion,
                Headers=headers,
                QueryParams=queryParams

            },
            platform?.ToLowerInvariant(),
            transitionKey,
            cancellationToken);

        if (!subFlowViewResult.IsSuccess)
        {
            return null;
        }

        // If current state has view overrides, use the override view
        if (currentState.SubFlow!.HasViewOverrides)
        {
            var overrideViewRef = currentState.SubFlow!.ViewOverrides!.GetOrDefault(subFlowViewResult.Value!.Key);

            if (overrideViewRef != null)
            {
                var overrideViewResult = await componentCacheStore.GetViewAsync(
                    overrideViewRef.Domain,
                    overrideViewRef.Key,
                    overrideViewRef.Version,
                    cancellationToken);

                // If override view fetch fails, fall back to subflow view
                if (overrideViewResult.IsSuccess)
                {
                    // Create a default view entry for the override view
                    var overrideViewEntry = ViewEntry.CreateDefault(overrideViewRef);
                    return BuildViewOutput(overrideViewResult.Value!, overrideViewEntry);
                }
            }
        }

        // Return subflow view directly (remote call already handled view selection)
        return subFlowViewResult.Value!;
    }

    /// <summary>
    /// Builds a GetViewOutput from a View and ViewEntry.
    /// </summary>
    /// <param name="view">The view to build output from</param>
    /// <param name="viewEntry">The view entry containing extensions and loadData information</param>
    /// <returns>GetViewOutput with view content</returns>
    private GetViewOutput BuildViewOutput(View view, ViewEntry viewEntry)
    {
        return new GetViewOutput
        {
            Key = view.Key,
            Content = view.Content,
            Type = view.Type.ToString(),
            Display = view.Display,
            Label = ""
        };
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
    private sealed record SubFlowStateInfo(
        List<string> AvailableTransitions,
        string? CurrentState,
        InstanceStatus? Status,
        DataHref? SubFlowData = null,
        ViewHref? SubFlowView = null,
        List<ActiveCorrelationHref>? SubFlowActiveCorrelations = null);
}