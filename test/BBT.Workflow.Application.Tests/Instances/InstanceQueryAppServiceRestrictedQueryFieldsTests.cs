using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Application.Pagination;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Instances;

/// <summary>
/// <see cref="InstanceQueryAppService.GetInstanceListAsync"/> binds the schema context it hands to the repository
/// to the caller (vnext#1262): the <c>x-roles</c> paths the caller's roles do not open and the <c>x-masking</c>
/// paths it is not exempt from travel as <see cref="SchemaFilterContext.RestrictedPaths"/>, where the SQL
/// builders refuse a filter, sort, groupBy or aggregation on them. The grouped response returns before the
/// per-instance exposure pass, so this is the only guard those values have.
/// </summary>
public sealed class InstanceQueryAppServiceRestrictedQueryFieldsTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "wf-key";

    private const string GuardedSchema = """
        {
          "type": "workflow",
          "schema": {
            "type": "object",
            "properties": {
              "assignedUser": { "type": "string", "x-roles": [{ "role": "manager", "grant": "allow" }] },
              "processingBranch": { "type": "string" },
              "phone": { "type": "string", "x-masking": { "operator": "mask" } }
            }
          }
        }
        """;

    private const string OpenSchema = """
        {
          "type": "workflow",
          "schema": { "type": "object", "properties": { "processingBranch": { "type": "string" } } }
        }
        """;

    private const string GroupByAssignedUser = """{"fields":["attributes.assignedUser"]}""";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _repo = Substitute.For<IInstanceRepository>();
    private readonly ICallerRoleResolver _resolver = Substitute.For<ICallerRoleResolver>();
    private readonly IPaginationLinkGenerator _links = Substitute.For<IPaginationLinkGenerator>();
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceRestrictedQueryFieldsTests()
    {
        var uow = Substitute.For<IUnitOfWorkManager>();
        uow.BeginAsync(default!, default!).ReturnsForAnyArgs(Task.FromResult(Substitute.For<IUnitOfWork>()));
        _ambientServiceProvider = new ServiceCollection().AddSingleton(uow).BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _links.Relative().Returns(_links);
        _links.GenerateLinks(Arg.Any<HateoasPagedList<GetInstanceOutput>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());
        _links.GenerateLinks(Arg.Any<HateoasPagedList<GroupSummary>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());

        var empty = (new HateoasPagedList<Instance>([], 1, 10, false), (List<GroupSummary>?)null);
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(empty));
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<GraphQLFilterRequest?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(empty));

        StubSchema(GuardedSchema);
        StubRoles("branch-user");
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    // ---- the measured leak ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GroupBy_ForACallerWithoutTheRole_CarriesTheGuardedPathAsRestricted()
    {
        var result = await InvokeAsync(Input(groupBy: GroupByAssignedUser));

        result.IsSuccess.ShouldBeTrue();
        var context = SingleStringRouteContext();
        context.RestrictedPaths.ShouldBe(["assignedUser", "phone"], ignoreOrder: true);
        context.IsRestrictedOrRelated("assignedUser").ShouldBeTrue();
        context.IsRestrictedOrRelated("processingBranch").ShouldBeFalse();
    }

    [Fact]
    public async Task GroupBy_ForACallerWithTheRole_LeavesTheGuardedPathOpen()
    {
        StubRoles("manager");

        await InvokeAsync(Input(groupBy: GroupByAssignedUser));

        // Still not exempt from the mask on 'phone': it has no exemption list.
        SingleStringRouteContext().RestrictedPaths.ShouldBe(["phone"]);
    }

    /// <summary>
    /// What the repository answers on a restricted path comes back as a Validation-PREFIXED error. The code alone
    /// is not enough: a thrown one leaves <c>TryAsync</c> Failure-prefixed, which the HTTP status table still maps
    /// to 400 but every prefix reader — the GetInstances task — reads as a 500.
    /// </summary>
    [Fact]
    public async Task TheRepositoryRefusal_SurfacesAsAValidationPrefixedSchemaFilterError()
    {
        RepositoryRefusesAssignedUser();

        var result = await InvokeAsync(Input(groupBy: GroupByAssignedUser));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.SchemaFilterValidation);
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.Validation);
        result.Error.Target.ShouldBe("filter");
        result.Error.Message.ShouldContain("assignedUser");
    }


    // ---- every query part, both repository routes ---------------------------------------------------------------

    [Theory]
    [InlineData("""{"attributes":{"assignedUser":{"eq":"U-1"}}}""", null, null, null)]
    [InlineData(null, """{"field":"attributes.assignedUser","direction":"asc"}""", null, null)]
    [InlineData(null, null, """{"fields":["attributes.processingBranch"],"aggregations":{"max":"assignedUser"}}""", null)]
    [InlineData(null, null, null, """{"max":"assignedUser"}""")]
    public async Task AnyQueryPart_BindsTheContextToTheCaller(string? filter, string? sort, string? groupBy, string? aggregations)
    {
        await InvokeAsync(Input(filter, sort, groupBy, aggregations));

        SingleContext().IsRestrictedOrRelated("assignedUser").ShouldBeTrue();
    }

    [Fact]
    public async Task TheEnvelopeRoute_CarriesTheRestrictedContextOnTheParsedRequest()
    {
        const string envelope =
            """{"filter":{"attributes":{"processingBranch":{"eq":"B-1"}}},"groupBy":{"fields":["attributes.assignedUser"]}}""";

        await InvokeAsync(Input(filter: envelope));

        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10,
            Arg.Is<GraphQLFilterRequest?>(r => r != null && r.SchemaContext != null &&
                                               r.SchemaContext.IsRestrictedOrRelated("assignedUser")),
            Arg.Any<CancellationToken>());
    }

    // ---- the role set is the one the list body is filtered with -------------------------------------------------

    [Fact]
    public async Task RolesAreResolvedFromTheRequestHeaders()
    {
        var input = Input(groupBy: GroupByAssignedUser);
        input.Headers = new Dictionary<string, string?> { ["role"] = "branch-user" };

        await InvokeAsync(input);

        await _resolver.Received(1).ResolveRoleSetAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["role"] == "branch-user"),
            Arg.Any<CancellationToken>());
    }

    // ---- the role provider could not answer ---------------------------------------------------------------------

    /// <summary>Fail closed: an unknown role set opens nothing, whatever the provider handed back with it.</summary>
    [Fact]
    public async Task WhenRolesCannotBeResolved_EveryGuardedPathIsRestricted()
    {
        StubRoleSet(new CallerRoleResolution(["manager"], CallerRoleOutcome.Failed, "timeout"));

        var result = await InvokeAsync(Input(groupBy: GroupByAssignedUser));

        result.IsSuccess.ShouldBeTrue();
        SingleStringRouteContext().RestrictedPaths.ShouldBe(["assignedUser", "phone"], ignoreOrder: true);
    }

    /// <summary>
    /// The refusal is then the provider's outage, not the caller's mistake: retryable 503, not the 400 a caller
    /// without the role gets. The built-in providers report an outage only through the outcome — their
    /// <c>ResolveRolesAsync</c> answers an empty set — which is why the outcome is what is read.
    /// </summary>
    [Fact]
    public async Task WhenRolesCannotBeResolved_AQueryOnAGuardedPath_AnswersTheRetryableError()
    {
        StubRoleSet(new CallerRoleResolution([], CallerRoleOutcome.Failed, "timeout"));
        RepositoryRefusesAssignedUser();

        var result = await InvokeAsync(Input(groupBy: GroupByAssignedUser));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRolesUnavailable);
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.Transient);
    }

    /// <summary>A query that names no guarded path is served through the outage, like the plain list is.</summary>
    [Fact]
    public async Task WhenRolesCannotBeResolved_AQueryOnAnOpenPath_IsStillServed()
    {
        StubRoleSet(new CallerRoleResolution([], CallerRoleOutcome.Failed, "timeout"));

        var result = await InvokeAsync(Input(filter: BranchFilter));

        result.IsSuccess.ShouldBeTrue();
    }

    /// <summary>A rule that holds for every caller is a 400 whether or not the roles were resolved.</summary>
    [Fact]
    public async Task WhenRolesCannotBeResolved_ARefusalThatIsNotTheCallerRestriction_StaysAValidationError()
    {
        StubRoleSet(new CallerRoleResolution([], CallerRoleOutcome.Failed, "timeout"));
        RepositoryThrows(new SchemaFilterValidationException("Operator 'eq' is not allowed for field 'processingBranch'."));

        var result = await InvokeAsync(Input(filter: BranchFilter));

        result.Error.Code.ShouldBe(WorkflowErrorCodes.SchemaFilterValidation);
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.Validation);
    }

    /// <summary>"Holds no roles" is an answer, not an outage: the caller is refused with the 400.</summary>
    [Theory]
    [InlineData(CallerRoleOutcome.Empty)]
    [InlineData(CallerRoleOutcome.Skipped)]
    public async Task ARoleLessCaller_IsRefusedWithTheValidationError_NotTheRetryableOne(CallerRoleOutcome outcome)
    {
        StubRoleSet(new CallerRoleResolution([], outcome));
        RepositoryRefusesAssignedUser();

        var result = await InvokeAsync(Input(groupBy: GroupByAssignedUser));

        result.Error.Code.ShouldBe(WorkflowErrorCodes.SchemaFilterValidation);
    }

    // ---- the two recorded limits (known issues, pinned so a change to either is deliberate) ---------------------

    /// <summary>
    /// KNOWN GAP, pinned as it is today. When the flow's master schema cannot be read — in practice the cache
    /// answers not-found for a reference that is not published yet — there is no schema context, so nothing is
    /// bound to the caller and the query runs without the schema's policy. The plain list serves the rows as
    /// stored in the same condition; the two belong to one follow-up decision and are not fixed here.
    /// </summary>
    [Theory]
    [InlineData("""{"attributes":{"assignedUser":{"eq":"U-1"}}}""", null, null, null)]
    [InlineData(null, """{"field":"attributes.assignedUser","direction":"asc"}""", null, null)]
    [InlineData(null, null, GroupByAssignedUser, null)]
    [InlineData(null, null, null, """{"max":"assignedUser"}""")]
    public async Task WhenTheDeclaredSchemaCannotBeRead_TheQueryRunsWithoutASchemaPolicy(
        string? filter, string? sort, string? groupBy, string? aggregations)
    {
        _cache.GetSchemaAsync(Domain, "sch", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SchemaDefinition>.Fail(
                Error.NotFound(WorkflowErrorCodes.CacheItemNotFound, "schema 'sch' has no published version"))));

        var result = await InvokeAsync(Input(filter, sort, groupBy, aggregations));

        result.IsSuccess.ShouldBeTrue();
        _repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name.StartsWith("GetPaged", StringComparison.Ordinal))
            .ShouldHaveSingleItem()
            .GetArguments().OfType<SchemaFilterContext>().ShouldBeEmpty();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    /// <summary>
    /// KNOWN GAP, pinned as it is today. The guards are read from the flow the list resolves —
    /// <c>GetFlowAsync(domain, workflow, null)</c>, the DEFAULT version — and from that flow's master schema
    /// only. The list body is filtered per row with each instance's own flow version, so a path guarded only
    /// in another version's schema (a pilot version above the default pointer) is pruned from the rows and is
    /// still not restricted for a set query.
    /// </summary>
    [Fact]
    public async Task GuardsComeFromTheDefaultVersionSchema_APathGuardedOnlyInAnotherVersion_IsNotRestricted()
    {
        // Default version: 'assignedUser' is an ordinary field. A later version guards it.
        StubSchema(OpenSchema);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var guarded = JsonSerializer.Deserialize<SchemaDefinition>(GuardedSchema, options)!;
        guarded.SetReference(new Reference("sch", Domain, RuntimeSysSchemaInfo.Schemas, "2.0.0"));
        _cache.GetSchemaAsync(Domain, "sch", "2.0.0", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SchemaDefinition>.Ok(guarded)));

        var result = await InvokeAsync(Input(groupBy: GroupByAssignedUser));

        result.IsSuccess.ShouldBeTrue();
        SingleStringRouteContext().IsRestrictedOrRelated("assignedUser").ShouldBeFalse();
        await _cache.Received(1).GetFlowAsync(Domain, WorkflowKey, null, Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().GetSchemaAsync(Domain, "sch", "2.0.0", Arg.Any<CancellationToken>());
    }

    // ---- nothing to guard costs nothing -------------------------------------------------------------------------

    [Fact]
    public async Task APlainList_ResolvesNoRoles_AndCarriesNoRestriction()
    {
        await InvokeAsync(Input());

        SingleStringRouteContext().RestrictedPaths.ShouldBeEmpty();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task ASchemaThatGuardsNothing_ResolvesNoRoles()
    {
        StubSchema(OpenSchema);

        await InvokeAsync(Input(groupBy: """{"fields":["attributes.processingBranch"]}"""));

        SingleStringRouteContext().RestrictedPaths.ShouldBeEmpty();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task WithMaskingSwitchedOff_TheMaskedPathIsNotRestricted()
    {
        await InvokeAsync(Input(groupBy: GroupByAssignedUser), new SchemaMaskingOptions { Enabled = false });

        SingleStringRouteContext().RestrictedPaths.ShouldBe(["assignedUser"]);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private const string BranchFilter = """{"attributes":{"processingBranch":{"eq":"B-1"}}}""";

    private void StubRoles(params string[] roles) =>
        StubRoleSet(new CallerRoleResolution(roles, CallerRoleOutcome.Resolved));

    private void StubRoleSet(CallerRoleResolution resolution) =>
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(resolution);

    /// <summary>The repository refuses 'assignedUser' the way the SQL builders do: through the context it was handed.</summary>
    private void RepositoryRefusesAssignedUser() =>
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(HateoasPagedList<Instance>, List<GroupSummary>?)>>(call =>
            {
                call.Arg<SchemaFilterContext?>()!.EnsureNotRestricted("assignedUser", "grouped or aggregated");
                throw new InvalidOperationException("the restricted path was not refused");
            });

    private void RepositoryThrows(Exception exception) =>
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns<Task<(HateoasPagedList<Instance>, List<GroupSummary>?)>>(_ => throw exception);

    private void StubSchema(string schemaJson)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>("""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [],
              "sharedTransitions": [],
              "extensions": [],
              "startTransition": {"key": "start", "target": "init"},
              "schema": {"key":"sch","domain":"test-domain","flow":"sys-schemas","version":"1.0.0"}
            }
            """, options)!;
        workflow.SetReference(new Reference(WorkflowKey, Domain, RuntimeSysSchemaInfo.Flows, "1.0.0"));

        var schema = JsonSerializer.Deserialize<SchemaDefinition>(schemaJson, options)!;
        schema.SetReference(new Reference("sch", Domain, RuntimeSysSchemaInfo.Schemas, "1.0.0"));

        _cache.GetFlowAsync(Domain, WorkflowKey, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<WorkflowDefinition>.Ok(workflow)));
        _cache.GetSchemaAsync(Domain, "sch", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SchemaDefinition>.Ok(schema)));
    }

    private static GetInstanceListInput Input(
        string? filter = null, string? sort = null, string? groupBy = null, string? aggregations = null)
    {
        var input = new GetInstanceListInput
        {
            Domain = Domain,
            Workflow = WorkflowKey,
            Page = 1,
            PageSize = 10,
            Filter = filter,
            Sort = sort,
            Headers = new Dictionary<string, string?>(),
            QueryParameters = new Dictionary<string, string?>()
        };
        // groupBy / aggregations are read off the query string, as the controller leaves them.
        if (groupBy != null) input.QueryParameters["groupBy"] = groupBy;
        if (aggregations != null) input.QueryParameters["aggregations"] = aggregations;
        return input;
    }

    /// <summary>The context of the single string-route repository call.</summary>
    private SchemaFilterContext SingleStringRouteContext() =>
        _repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name.StartsWith("GetPaged", StringComparison.Ordinal))
            .ShouldHaveSingleItem()
            .GetArguments().OfType<SchemaFilterContext>().ShouldHaveSingleItem();

    /// <summary>The context of the single repository call, whichever route it took.</summary>
    private SchemaFilterContext SingleContext()
    {
        var arguments = _repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name.StartsWith("GetPaged", StringComparison.Ordinal))
            .ShouldHaveSingleItem()
            .GetArguments();
        return arguments.OfType<SchemaFilterContext>().SingleOrDefault()
               ?? arguments.OfType<GraphQLFilterRequest>().ShouldHaveSingleItem().SchemaContext.ShouldNotBeNull();
    }

    private Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> InvokeAsync(
        GetInstanceListInput input, SchemaMaskingOptions? masking = null) =>
        CreateService(masking).GetInstanceListAsync(input, CancellationToken.None);

    private InstanceQueryAppService CreateService(SchemaMaskingOptions? masking)
    {
        var urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlTemplateBuilder.BuildInstanceListUrl(Arg.Any<string>(), Arg.Any<string>()).Returns("/route");

        return new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: _cache,
            instanceRepository: _repo,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: Substitute.For<IInstanceTaskRepository>(),
            instanceActionRepository: Substitute.For<IInstanceActionRepository>(),
            longPollInteractionGate: Substitute.For<BBT.Workflow.Execution.LongPoll.ILongPollInteractionGate>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: urlTemplateBuilder,
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            correlationOptions: Options.Create(new BBT.Workflow.Instances.Correlation.InstanceCorrelationOptions()),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            callerRoleResolver: _resolver,
            paginationLinkGenerator: _links,
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            currentUser: Substitute.For<ICurrentUser>(),
            descentLimiter: new HumanTaskDescentLimiter(Options.Create(new HumanTaskFunctionOptions())),
            transitionSchemaResolver: Substitute.For<BBT.Workflow.Execution.Validation.ITransitionSchemaResolver>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>(),
            schemaMaskingOptions: masking is null ? null : Options.Create(masking));
    }
}
