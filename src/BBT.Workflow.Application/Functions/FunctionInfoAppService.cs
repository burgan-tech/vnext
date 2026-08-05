using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Functions.DTOs;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Functions;

/// <summary>
/// Application service backing function discovery: the <c>info</c> endpoint and the view/schema
/// endpoints its hyperlinks point at.
/// </summary>
/// <remarks>
/// Lifecycle: scoped, one per request. Every entry point runs the same access policy as execution
/// before it reveals anything, and shares a single lazily-built script context across all four
/// contract slots so a rule-based definition is evaluated against one consistent snapshot.
/// </remarks>
public sealed class FunctionInfoAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceRepository instanceRepository,
    IScriptContextFactory scriptContextFactory,
    IComponentCacheStore componentCacheStore,
    ICurrentSchema currentSchema,
    IUrlTemplateBuilder urlTemplateBuilder,
    IFunctionAccessPolicy functionAccessPolicy,
    IFunctionContractResolver contractResolver,
    IViewContentResolutionService viewContentResolutionService)
    : ApplicationService(serviceProvider), IFunctionInfoAppService
{
    private const string TargetInput = "input";
    private const string TargetOutput = "output";

    /// <inheritdoc />
    public async Task<Result<FunctionInfoOutput>> GetInfoByKeyAsync(
        string domain,
        string key,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(RuntimeSysSchemaInfo.Functions))
        {
            var functionResult = await componentCacheStore.GetFunctionAsync(domain, key, version, cancellationToken);
            if (!functionResult.IsSuccess)
                return Result<FunctionInfoOutput>.Fail(functionResult.Error);

            return await BuildInfoAsync(
                functionResult.Value!, domain, null, null, headers, queryParameters, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<FunctionInfoOutput>> GetInfoByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        string key,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(workflow))
        {
            var resolved = await ResolveInstanceFunctionAsync(domain, workflow, instanceKey, key, cancellationToken);
            if (!resolved.IsSuccess)
                return Result<FunctionInfoOutput>.Fail(resolved.Error);

            var (function, instance, flow) = resolved.Value;
            return await BuildInfoAsync(
                function, domain, instance, flow, headers, queryParameters, cancellationToken, workflow, instanceKey);
        }
    }

    /// <inheritdoc />
    public async Task<Result<FunctionCatalogOutput>> GetCatalogByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(workflow))
        {
            var instance = await instanceRepository.FindByIdentifierAsync(instanceKey, cancellationToken);
            if (instance == null)
                return Result<FunctionCatalogOutput>.Fail(WorkflowErrors.InstanceNotFound(instanceKey));

            var flowResult = await componentCacheStore.GetFlowAsync(
                domain, workflow, instance.FlowVersion, cancellationToken);
            if (!flowResult.IsSuccess)
                return Result<FunctionCatalogOutput>.Fail(flowResult.Error);

            var flow = flowResult.Value!;
            var instanceId = instance.Id.ToString();
            var catalog = new FunctionCatalogOutput();

            foreach (var reference in flow.Functions)
            {
                var functionResult = await componentCacheStore.GetFunctionAsync(
                    reference.Domain, reference.Key, reference.Version, cancellationToken);

                // A broken reference must not fail the whole catalog — omit it and keep going.
                if (!functionResult.IsSuccess)
                {
                    Logger.WorkflowFunctionReferenceUnresolved(
                        flow.Key, reference.Key, functionResult.Error.Message ?? "unknown");
                    continue;
                }

                var function = functionResult.Value!;

                // Same gate as execution and /info, so every link handed out is actionable and a
                // function the caller cannot invoke is not advertised at all.
                var access = await functionAccessPolicy.AuthorizeAsync(
                    function, instance, flow, headers, queryParameters, cancellationToken);
                if (!access.IsSuccess)
                    continue;

                var scope = function.Scope;
                catalog.Functions.Add(new WorkflowFunctionHref
                {
                    Name = reference.Key,
                    Version = reference.Version ?? string.Empty,
                    Scope = scope.Code,
                    // The href must match the scope: the domain route rejects Flow and Instance
                    // scopes with 403, so linking them there would be a dead link.
                    Href = scope.Equals(TaskScope.Domain)
                        ? urlTemplateBuilder.BuildDomainFunctionInfoUrl(domain, reference.Key)
                        : urlTemplateBuilder.BuildInstanceFunctionInfoUrl(
                            domain, workflow, instanceId, reference.Key)
                });
            }

            return Result<FunctionCatalogOutput>.Ok(catalog);
        }
    }

    /// <inheritdoc />
    public async Task<Result<GetViewOutput>> GetViewByKeyAsync(
        string domain,
        string key,
        string target,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(RuntimeSysSchemaInfo.Functions))
        {
            var slot = ResolveViewSlot(target);
            if (!slot.IsSuccess)
                return Result<GetViewOutput>.Fail(slot.Error);

            var functionResult = await componentCacheStore.GetFunctionAsync(domain, key, version, cancellationToken);
            if (!functionResult.IsSuccess)
                return Result<GetViewOutput>.Fail(functionResult.Error);

            var reference = await ResolveSlotReferenceAsync(
                functionResult.Value!, slot.Value, domain, null, null, headers, queryParameters, cancellationToken);
            if (!reference.IsSuccess)
                return Result<GetViewOutput>.Fail(reference.Error);

            return await viewContentResolutionService.ResolveViewContentAsync(
                reference.Value!, domain, headers, queryParameters, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<GetViewOutput>> GetViewByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        string key,
        string target,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(workflow))
        {
            var slot = ResolveViewSlot(target);
            if (!slot.IsSuccess)
                return Result<GetViewOutput>.Fail(slot.Error);

            var resolved = await ResolveInstanceFunctionAsync(domain, workflow, instanceKey, key, cancellationToken);
            if (!resolved.IsSuccess)
                return Result<GetViewOutput>.Fail(resolved.Error);

            var (function, instance, flow) = resolved.Value;
            var reference = await ResolveSlotReferenceAsync(
                function, slot.Value, domain, instance, flow, headers, queryParameters, cancellationToken);
            if (!reference.IsSuccess)
                return Result<GetViewOutput>.Fail(reference.Error);

            return await viewContentResolutionService.ResolveViewContentAsync(
                reference.Value!, domain, headers, queryParameters, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<FunctionSchemaOutput>> GetSchemaByKeyAsync(
        string domain,
        string key,
        string target,
        string? version = null,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(RuntimeSysSchemaInfo.Functions))
        {
            var slot = ResolveSchemaSlot(target);
            if (!slot.IsSuccess)
                return Result<FunctionSchemaOutput>.Fail(slot.Error);

            var functionResult = await componentCacheStore.GetFunctionAsync(domain, key, version, cancellationToken);
            if (!functionResult.IsSuccess)
                return Result<FunctionSchemaOutput>.Fail(functionResult.Error);

            var reference = await ResolveSlotReferenceAsync(
                functionResult.Value!, slot.Value, domain, null, null, headers, queryParameters, cancellationToken);
            if (!reference.IsSuccess)
                return Result<FunctionSchemaOutput>.Fail(reference.Error);

            return await LoadSchemaAsync(reference.Value!, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<FunctionSchemaOutput>> GetSchemaByInstanceAsync(
        string domain,
        string workflow,
        string instanceKey,
        string key,
        string target,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParameters = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        using (currentSchema.Change(workflow))
        {
            var slot = ResolveSchemaSlot(target);
            if (!slot.IsSuccess)
                return Result<FunctionSchemaOutput>.Fail(slot.Error);

            var resolved = await ResolveInstanceFunctionAsync(domain, workflow, instanceKey, key, cancellationToken);
            if (!resolved.IsSuccess)
                return Result<FunctionSchemaOutput>.Fail(resolved.Error);

            var (function, instance, flow) = resolved.Value;
            var reference = await ResolveSlotReferenceAsync(
                function, slot.Value, domain, instance, flow, headers, queryParameters, cancellationToken);
            if (!reference.IsSuccess)
                return Result<FunctionSchemaOutput>.Fail(reference.Error);

            return await LoadSchemaAsync(reference.Value!, cancellationToken);
        }
    }

    /// <summary>
    /// Loads the instance, its workflow and the function definition the instance's flow binds to.
    /// </summary>
    private async Task<Result<(Function Function, Instance Instance, Definitions.Workflow Workflow)>>
        ResolveInstanceFunctionAsync(
            string domain,
            string workflow,
            string instanceKey,
            string key,
            CancellationToken cancellationToken)
    {
        var instance = await instanceRepository.FindByIdentifierAsync(instanceKey, cancellationToken);
        if (instance == null)
            return Result<(Function, Instance, Definitions.Workflow)>.Fail(
                WorkflowErrors.InstanceNotFound(instanceKey));

        var flowResult = await componentCacheStore.GetFlowAsync(domain, workflow, instance.FlowVersion, cancellationToken);
        if (!flowResult.IsSuccess)
            return Result<(Function, Instance, Definitions.Workflow)>.Fail(flowResult.Error);

        // The flow pins a function version when it declares the function; otherwise take the latest.
        var functionReference = flowResult.Value!.FindFunction(key);
        var functionResult = await componentCacheStore.GetFunctionAsync(
            domain, key, functionReference?.Version, cancellationToken);
        if (!functionResult.IsSuccess)
            return Result<(Function, Instance, Definitions.Workflow)>.Fail(functionResult.Error);

        return Result<(Function, Instance, Definitions.Workflow)>.Ok(
            (functionResult.Value!, instance, flowResult.Value!));
    }

    /// <summary>
    /// Runs the shared access gates, then resolves all four contract slots against one script context
    /// and projects them into the hyperlink response.
    /// </summary>
    private async Task<Result<FunctionInfoOutput>> BuildInfoAsync(
        Function function,
        string domain,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken,
        string? workflowKey = null,
        string? instanceKey = null)
    {
        var access = await functionAccessPolicy.AuthorizeAsync(
            function, instance, workflow, headers, queryParameters, cancellationToken);
        if (!access.IsSuccess)
            return Result<FunctionInfoOutput>.Fail(access.Error);

        var scriptContext = CreateLazyScriptContext(function, instance, workflow, headers, queryParameters);

        var slots = new Dictionary<FunctionContractSlot, FunctionContractResolution?>();
        foreach (var slot in Enum.GetValues<FunctionContractSlot>())
        {
            var resolution = await contractResolver.ResolveAsync(function, slot, scriptContext, cancellationToken);
            if (!resolution.IsSuccess)
                return Result<FunctionInfoOutput>.Fail(resolution.Error);

            slots[slot] = resolution.Value;
        }

        var isInstanceScoped = workflowKey != null && instanceKey != null;

        return Result<FunctionInfoOutput>.Ok(new FunctionInfoOutput
        {
            Key = function.Key,
            Domain = function.Domain,
            Version = function.Version,
            Scope = function.Scope.Code,
            RawResponse = function.RawResponse,
            Cacheable = function.Cache is not null,
            Function = new FunctionHref
            {
                Href = isInstanceScoped
                    ? urlTemplateBuilder.BuildInstanceFunctionUrl(domain, workflowKey!, instanceKey!, function.Key)
                    : urlTemplateBuilder.BuildDomainFunctionUrl(domain, function.Key),
                Verbs = function.Verbs.ToList()
            },
            InputView = BuildViewHref(
                slots[FunctionContractSlot.InputView], domain, function.Key, TargetInput, workflowKey, instanceKey),
            OutputView = BuildViewHref(
                slots[FunctionContractSlot.OutputView], domain, function.Key, TargetOutput, workflowKey, instanceKey),
            InputSchema = BuildSchemaHref(
                slots[FunctionContractSlot.InputSchema], domain, function.Key, TargetInput, workflowKey, instanceKey),
            OutputSchema = BuildSchemaHref(
                slots[FunctionContractSlot.OutputSchema], domain, function.Key, TargetOutput, workflowKey, instanceKey)
        });
    }

    /// <summary>
    /// Authorizes and resolves a single contract slot to the component reference it points at,
    /// for the view/schema content endpoints.
    /// </summary>
    private async Task<Result<Reference>> ResolveSlotReferenceAsync(
        Function function,
        FunctionContractSlot slot,
        string domain,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken)
    {
        var access = await functionAccessPolicy.AuthorizeAsync(
            function, instance, workflow, headers, queryParameters, cancellationToken);
        if (!access.IsSuccess)
            return Result<Reference>.Fail(access.Error);

        var scriptContext = CreateLazyScriptContext(function, instance, workflow, headers, queryParameters);

        var resolution = await contractResolver.ResolveAsync(function, slot, scriptContext, cancellationToken);
        if (!resolution.IsSuccess)
            return Result<Reference>.Fail(resolution.Error);

        if (resolution.Value is null)
            return Result<Reference>.Fail(
                WorkflowErrors.FunctionContractNotResolved(function.Key, ToSlotName(slot)));

        return Result<Reference>.Ok(resolution.Value.Reference);
    }

    /// <summary>
    /// Builds the script context contract rules are evaluated against. Discovery carries no request
    /// body, so rules see the instance's latest data as the body - the same material state and
    /// transition view rules read.
    /// </summary>
    private LazyScriptContext CreateLazyScriptContext(
        Function function,
        Instance? instance,
        Definitions.Workflow? workflow,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters)
    {
        return new LazyScriptContext(ct => scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithBody(instance?.LatestData?.Data ?? new JsonData("{}"))
            .WithHeaders(headers)
            .WithQueryParameters(queryParameters)
            .BuildAsync(ct));
    }

    private async Task<Result<FunctionSchemaOutput>> LoadSchemaAsync(
        Reference reference,
        CancellationToken cancellationToken)
    {
        var schemaResult = await componentCacheStore.GetSchemaAsync(reference, cancellationToken);
        if (!schemaResult.IsSuccess)
            return Result<FunctionSchemaOutput>.Fail(schemaResult.Error);

        var schema = schemaResult.Value!;
        return Result<FunctionSchemaOutput>.Ok(new FunctionSchemaOutput
        {
            Key = schema.Key,
            Type = schema.Type,
            Schema = schema.Schema
        });
    }

    /// <summary>
    /// The href is emitted whether or not a contract resolved: rules read request state, so a slot
    /// that matched nothing now can match on the next call. <c>hasView</c>/<c>hasSchema</c> tells the
    /// client whether following it right now would return content.
    /// </summary>
    private ViewHref BuildViewHref(
        FunctionContractResolution? resolution,
        string domain,
        string functionKey,
        string target,
        string? workflowKey,
        string? instanceKey)
    {
        return new ViewHref
        {
            Href = workflowKey != null && instanceKey != null
                ? urlTemplateBuilder.BuildInstanceFunctionViewUrl(domain, workflowKey, instanceKey, functionKey, target)
                : urlTemplateBuilder.BuildDomainFunctionViewUrl(domain, functionKey, target),
            HasView = resolution is not null,
            LoadData = resolution?.LoadData ?? false
        };
    }

    private SchemaHref BuildSchemaHref(
        FunctionContractResolution? resolution,
        string domain,
        string functionKey,
        string target,
        string? workflowKey,
        string? instanceKey)
    {
        return new SchemaHref
        {
            Href = workflowKey != null && instanceKey != null
                ? urlTemplateBuilder.BuildInstanceFunctionSchemaUrl(domain, workflowKey, instanceKey, functionKey, target)
                : urlTemplateBuilder.BuildDomainFunctionSchemaUrl(domain, functionKey, target),
            HasSchema = resolution is not null
        };
    }

    private static Result<FunctionContractSlot> ResolveViewSlot(string? target) =>
        Normalize(target) switch
        {
            TargetInput => Result<FunctionContractSlot>.Ok(FunctionContractSlot.InputView),
            TargetOutput => Result<FunctionContractSlot>.Ok(FunctionContractSlot.OutputView),
            _ => Result<FunctionContractSlot>.Fail(WorkflowErrors.FunctionContractTargetInvalid(target))
        };

    private static Result<FunctionContractSlot> ResolveSchemaSlot(string? target) =>
        Normalize(target) switch
        {
            TargetInput => Result<FunctionContractSlot>.Ok(FunctionContractSlot.InputSchema),
            TargetOutput => Result<FunctionContractSlot>.Ok(FunctionContractSlot.OutputSchema),
            _ => Result<FunctionContractSlot>.Fail(WorkflowErrors.FunctionContractTargetInvalid(target))
        };

    /// <summary>Blank target means "input", so the common case needs no query parameter.</summary>
    private static string Normalize(string? target) =>
        string.IsNullOrWhiteSpace(target) ? TargetInput : target.Trim().ToLowerInvariant();

    /// <summary>Renders the slot as the JSON property name clients see, for error messages.</summary>
    private static string ToSlotName(FunctionContractSlot slot) => slot switch
    {
        FunctionContractSlot.InputSchema => "inputSchema",
        FunctionContractSlot.OutputSchema => "outputSchema",
        FunctionContractSlot.InputView => "inputView",
        FunctionContractSlot.OutputView => "outputView",
        _ => slot.ToString()
    };
}
