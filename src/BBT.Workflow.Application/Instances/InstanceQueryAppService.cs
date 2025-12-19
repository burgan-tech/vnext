using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Extentions;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

public sealed class InstanceQueryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceCorrelationRepository instanceCorrelationRepository,
    IInstanceExtensionService instanceExtensionService,
    IScriptContextFactory scriptContextFactory,
    IRemoteInstanceQueryAppService remoteInstanceQueryAppService,
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
                        input.Extension,
                        input.Workflow,
                        instance,
                        instance.LatestData,
                        ExtensionScope.GetInstance,
                        input.Headers,
                        input.QueryParameters,
                        cancellationToken);

                    return ConditionalResult<GetInstanceOutput>.Success(result);
                },
                onFailure: error => ConditionalResult<GetInstanceOutput>.Fail(error));
    }

    public async Task<Result<HateoasPagedList<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        return await ResultExtensions.TryAsync(
            async ct =>
            {
                var pagedList = await instanceRepository.GetPagedResultsAsync(
                    input.Page,
                    input.PageSize,
                    input.Filter,
                    ct);

                var list = new List<GetInstanceOutput>();
                foreach (var instance in pagedList.Items)
                {
                    var instanceOutput = await BuildInstanceOutputAsync(
                        input.Domain,
                        input.Extension,
                        input.Workflow,
                        instance,
                        instance.LatestData,
                        ExtensionScope.GetAllInstances,
                        input.Headers,
                        input.QueryParameters,
                        ct);

                    list.Add(instanceOutput);
                }

                return new HateoasPagedList<GetInstanceOutput>(list, pagedList.CurrentPage, pagedList.PageSize,
                    pagedList.HasNext);
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
                    var transitionOutput = await BuildInstanceOutputAsync(
                        input.Domain,
                        input.Extension,
                        input.Workflow,
                        instance,
                        instanceData,
                        ExtensionScope.GetInstance,
                        input.Headers,
                        input.QueryParameters,
                        cancellationToken);

                    transitions.Add(transitionOutput);
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
    /// </summary>
    /// <param name="instance">The workflow instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing status, current state, and correlations</returns>
    private async
        Task<(InstanceStatus Status, string? CurrentState, List<InstanceCorrelationInfo>
            ActiveCorrelations)> BuildInstanceTransitionInfoAsync(
            Instance instance,
            CancellationToken cancellationToken = default)
    {
        // Get active correlations
        var activeCorrelations = await GetActiveCorrelationsAsync(instance.Id, cancellationToken);

        return (instance.Status, instance.CurrentState, activeCorrelations);
    }

    /// <summary>
    /// Gets available transitions from a remote SubFlow instance.
    /// </summary>
    /// <param name="activeSubFlowCorrelation">The active SubFlow correlation</param>
    /// <param name="mainInstance">The main workflow instance</param>
    /// <param name="currentWorkflow">The current workflow definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing available transitions and current state from SubFlow, status always from main flow</returns>
    private async Task<(List<string> AvailableTransitions, string? CurrentState, InstanceStatus? Status)>
        GetSubFlowTransitionsAsync(
            InstanceCorrelationInfo activeSubFlowCorrelation,
            Instance mainInstance,
            BBT.Workflow.Definitions.Workflow currentWorkflow,
            CancellationToken cancellationToken = default)
    {
        try
        {
            var subFlowInput = new GetFunctionWithInstanceInput
            {
                Domain = activeSubFlowCorrelation.SubFlowDomain,
                Workflow = activeSubFlowCorrelation.SubFlowName,
                Version = activeSubFlowCorrelation.SubFlowVersion,
                Instance = activeSubFlowCorrelation.SubFlowInstanceId.ToString()
            };

            var subFlowResult = await remoteInstanceQueryAppService.GetFunctionWithStateAsync(
                subFlowInput,
                cancellationToken);

            if (subFlowResult.IsSuccess && subFlowResult.Value != null)
            {
                // Use SubFlow transitions and current state, but always use main instance status
                // Extract transition names from TransitionItem list
                var transitionNames = subFlowResult.Value.Transitions
                    .Select(t => t.Name)
                    .ToList();
                return (transitionNames, subFlowResult.Value.State, mainInstance.Status);
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
    /// <returns>Tuple containing available transitions, current state, and status from main flow</returns>
    private (List<string> AvailableTransitions, string? CurrentState, InstanceStatus? Status) GetMainFlowTransitions(
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

        return (availableTransitions, currentState, status);
    }

    /// <summary>
    /// Gets active SubFlow/SubProcess correlations for the instance.
    /// </summary>
    /// <param name="instanceId">The workflow instance ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active correlation information</returns>
    private async Task<List<InstanceCorrelationInfo>> GetActiveCorrelationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var correlations = await instanceCorrelationRepository.GetActiveByParentAsync(instanceId, cancellationToken);

        return correlations
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
    }

    /// <summary>
    /// Retrieves an instance by ID or key using Railway pattern.
    /// Returns Result.Fail if instance is not found instead of throwing.
    /// </summary>
    private async Task<Result<Instance>> GetInstanceByIdOrKeyAsync(
        string instanceIdentifier,
        CancellationToken cancellationToken)
    {
        var instance = await instanceRepository.FindByIdentifierAsync(instanceIdentifier, cancellationToken);
        return instance.EnsureNotNull(WorkflowErrors.InstanceNotFound(instanceIdentifier));
    }

    private async Task<GetInstanceOutput> BuildInstanceOutputAsync(
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
            return response;
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

        var extensionsResult = await instanceExtensionService.ProcessExtensionsAsync(
            extensionRequested,
            scriptContext,
            flow,
            currentScope,
            cancellationToken);

        // Extensions are optional enrichment - use empty dictionary if processing fails
        response.Extensions = extensionsResult.ValueOrDefault(new Dictionary<string, object>())!;

        return response;
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

                    var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                        .WithWorkflow(flow)
                        .WithInstance(instance)
                        .WithRuntime(runtimeInfoProvider)
                        .WithTransition(string.Empty)
                        .WithBody(instance.LatestData?.Data ?? new JsonData("{}"))
                        .WithHeaders(input.Headers)
                        .WithQueryParameters(input.QueryParameters)
                        .BuildAsync(cancellationToken);

                    var extensionsResult = await instanceExtensionService.ProcessExtensionsAsync(
                        input.Extensions,
                        scriptContext,
                        flow,
                        ExtensionScope.GetInstance,
                        cancellationToken);

                    // Extensions are optional enrichment - use empty dictionary if processing fails
                    result.Extensions = extensionsResult.ValueOrDefault(new Dictionary<string, object>())!;

                    return ConditionalResult<GetInstanceDataOutput>.Success(result);
                },
                onFailure: error => ConditionalResult<GetInstanceDataOutput>.Fail(error));
    }

    /// <summary>
    /// Gets the view definition for platform-specific view retrieval.
    /// Throws <see cref="EntityNotFoundException"/> if view definition is not found.
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
    /// </summary>
    private async Task<Result<GetInstanceStateOutput>> BuildInstanceStateOutputAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetInstanceStateInput input,
        CancellationToken cancellationToken)
    {
        // Build instance transition information using shared logic
        var transitionInfo = await BuildInstanceTransitionInfoAsync(instance, cancellationToken);

        // Check if there are any active SubFlow correlations
        var activeSubFlowCorrelation = transitionInfo.ActiveCorrelations
            .Where(c => c.SubFlowType.Equals(SubFlowType.SubFlow) && !c.IsCompleted)
            .OrderByDescending(c => c.CorrelationId)
            .FirstOrDefault();

        var (availableTransitions, currentState, status) = activeSubFlowCorrelation != null
            ? await GetSubFlowTransitionsAsync(activeSubFlowCorrelation, instance, currentWorkflow, cancellationToken)
            : GetMainFlowTransitions(instance, currentWorkflow, transitionInfo);

        // Build transition items with href links
        var transitionItems = availableTransitions.Select(transitionKey => new TransitionItem
        {
            Name = transitionKey,
            Href = InstanceUrlTemplates.Transition(input.Domain, input.Workflow, instance.Id.ToString(), transitionKey)
        }).ToList();

        // Get current state using Railway pattern
        return currentWorkflow.GetState(instance.CurrentState!)
            .Ensure(
                state => state != null,
                Error.NotFound("notfound", $"State {instance.CurrentState} not found in workflow {input.Workflow}"))
            .Map(currentStateValue =>
            {
                // Get view definition
                var viewDefinition = GetViewDefinition(instance, currentWorkflow, currentStateValue);

                // Build data href with extensions
                var allExtensions = (input.Extensions ?? []).Concat(viewDefinition?.Extensions ?? []).ToArray();
                var dataHref = new DataHref
                {
                    Href = allExtensions.Length > 0
                        ? InstanceUrlTemplates.DataWithExtensions(input.Domain, input.Workflow, instance.Id.ToString(),
                            string.Join(",", allExtensions))
                        : InstanceUrlTemplates.Data(input.Domain, input.Workflow, instance.Id.ToString())
                };

                // Build view href
                var viewHref = new ViewHref
                {
                    Href = InstanceUrlTemplates.View(input.Domain, input.Workflow, instance.Id.ToString()),
                    LoadData = viewDefinition?.LoadData == true
                };

                // Build active correlations with href links
                var activeCorrelationHrefs = transitionInfo.ActiveCorrelations.Select(correlation =>
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
                        Href = InstanceUrlTemplates.Data(correlation.SubFlowDomain, correlation.SubFlowName,
                            correlation.SubFlowInstanceId.ToString())
                    }).ToList();

                return new GetInstanceStateOutput
                {
                    Data = dataHref,
                    View = viewHref,
                    State = currentState ?? string.Empty,
                    Status = status,
                    ActiveCorrelations = activeCorrelationHrefs,
                    Transitions = transitionItems,
                    ETag = instance.LatestData?.ETag ?? string.Empty
                };
            });
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
    /// Builds the schema output for a specific transition.
    /// Handles state resolution and schema lookup using Railway pattern.
    /// </summary>
    private async Task<Result<GetSchemaOutput>> BuildSchemaOutputAsync(
        Instance instance,
        Definitions.Workflow currentWorkflow,
        GetSchemaInput input,
        string? transitionKey,
        CancellationToken cancellationToken)
    {
        // Get current state using Railway pattern
        var currentStateResult = currentWorkflow.GetState(instance.GetCurrentState);
        if (!currentStateResult.IsSuccess || currentStateResult.Value == null)
        {
            return Result<GetSchemaOutput>.Fail(
                Error.NotFound("notfound", $"State {instance.CurrentState} not found in workflow {input.Workflow}"));
        }

        var currentState = currentStateResult.Value;

        if (string.IsNullOrEmpty(transitionKey))
        {
            return Result<GetSchemaOutput>.Fail(
                Error.Validation("validation", "Transition key is required to get schema"));
        }

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
    /// Resolves and returns the appropriate view for the instance.
    /// Handles subflow view overrides and platform-specific content.
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
                cancellationToken);

            if (subFlowViewResult != null)
            {
                return Result<GetViewOutput>.Ok(subFlowViewResult);
            }
        }

        // Get view definition
        var viewDefinition = GetViewDefinition(instance, currentWorkflow, currentState, transitionKey);

        if (viewDefinition?.View == null)
        {
            return Result<GetViewOutput>.Fail(
                Error.NotFound("notfound",
                    $"View not found for state {instance.CurrentState} in workflow {currentWorkflow.Key}"));
        }

        // Fetch and return the view
        return await componentCacheStore.GetViewAsync(
                viewDefinition.View.Domain,
                viewDefinition.View.Key,
                viewDefinition.View.Version,
                cancellationToken)
            .MapAsync(view => BuildViewOutput(view, platform));
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
        CancellationToken cancellationToken = default)
    {
        var subFlowViewResult = await remoteInstanceQueryAppService.GetFunctionWithViewAsync(
            new GetFunctionWithInstanceInput
            {
                Instance = instance.Subflow!.SubFlowInstanceId.ToString(),
                Domain = instance.Subflow!.SubFlowDomain,
                Workflow = instance.Subflow!.SubFlowName,
                Version = instance.Subflow!.SubFlowVersion
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
                    return BuildViewOutput(overrideViewResult.Value!, platform);
                }
            }
        }

        // Return subflow view directly (remote call already handled platform-specific logic)
        return subFlowViewResult.Value!;
    }

    /// <summary>
    /// Builds a GetViewOutput from a View, applying platform-specific content if available.
    /// </summary>
    /// <param name="view">The view to build output from</param>
    /// <param name="platform">Platform identifier (optional)</param>
    /// <returns>GetViewOutput with platform-specific content if available, otherwise default content</returns>
    private GetViewOutput BuildViewOutput(View view, string? platform)
    {
        var content = GetPlatformSpecificContent(view, platform);

        return new GetViewOutput
        {
            Key = view.Key,
            Content = content,
            Type = view.Type.ToString(),
            Display = view.Display,
            Label = ""
        };
    }

    /// <summary>
    /// Extracts platform-specific content from a view based on the platform identifier.
    /// Returns default content if platform is not specified or no platform override exists.
    /// </summary>
    /// <param name="view">The view to extract content from</param>
    /// <param name="platform">Platform identifier (optional)</param>
    /// <returns>Platform-specific content if available, otherwise default view content</returns>
    private string GetPlatformSpecificContent(View view, string? platform)
    {
        // If no platform specified or no platform overrides, return default content
        if (string.IsNullOrEmpty(platform) || view.PlatformOverrides == null)
        {
            return view.Content;
        }

        var platformLower = platform.ToLowerInvariant();

        return platformLower switch
        {
            PlatformConst.Android => view.PlatformOverrides.Android?.Content ?? view.Content,
            PlatformConst.Web => view.PlatformOverrides.Web?.Content ?? view.Content,
            PlatformConst.Ios => view.PlatformOverrides.Ios?.Content ?? view.Content,
            _ => view.Content
        };
    }
}