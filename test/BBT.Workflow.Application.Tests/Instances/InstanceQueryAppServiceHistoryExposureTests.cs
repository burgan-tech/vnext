using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.History;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using BBT.Workflow.Headers;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// What <c>GET …/instances/{id}/transitions</c> exposes (vnext#1260). Every other instance read passes the stored data
/// through the master schema's <c>x-roles</c> → <c>x-masking</c> → <c>x-encryption</c> plan for the caller; transition
/// history must apply the same plan to each row's body, and must withhold the body when the plan cannot be read — the
/// stored body is the plaintext request, so "serve as stored" is never a safe fallback for it.
/// <para>
/// The filter, the role-grant evaluator and the read service are the real ones; only the component cache, the
/// repositories and the caller's role set are substituted.
/// </para>
/// </summary>
public sealed class InstanceQueryAppServiceHistoryExposureTests : IDisposable
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string Admin = "morph-idm.admin";
    private const string Reader = "morph-idm.reader";
    private const string NationalId = "12345678901";

    private static readonly Reference SchemaReference = new("schema", TestDomain, "sys-schemas", TestVersion);

    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceTransitionRepository _transitionRepository = Substitute.For<IInstanceTransitionRepository>();
    private readonly ICallerRoleResolver _roles = Substitute.For<ICallerRoleResolver>();
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceHistoryExposureTests()
    {
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        unitOfWorkManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(unitOfWorkManager);
        services.AddSingleton(_componentCacheStore);
        _ambientServiceProvider = services.BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        var currentUser = Substitute.For<ICurrentUser>();
        var filter = new SchemaFieldFilterService(
            _componentCacheStore,
            new TransitionAuthorizationManager(currentUser, _transitionRepository),
            _roles,
            new FakeFieldMaskingEngine(),
            Options.Create(new SchemaMaskingOptions()));

        _service = new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: _transitionRepository,
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
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            correlationOptions: Options.Create(new BBT.Workflow.Instances.Correlation.InstanceCorrelationOptions()),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new InstanceDataReadService(filter),
            callerRoleResolver: _roles,
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            currentUser: currentUser,
            descentLimiter: new HumanTaskDescentLimiter(Options.Create(new HumanTaskFunctionOptions())),
            transitionSchemaResolver: Substitute.For<BBT.Workflow.Execution.Validation.ITransitionSchemaResolver>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>(),
            headerExposureDenyList: new DeferredDenyList(() => _denyList));
    }

    /// <summary>The real deny list, replaceable per test; the default is the one built from empty configuration.</summary>
    private IHeaderExposureDenyList _denyList = DenyList();

    private static HeaderExposureDenyList DenyList(params string[] deny) =>
        new(Options.Create(new HeaderExposureOptions { Deny = [.. deny] }));

    private sealed class DeferredDenyList(Func<IHeaderExposureDenyList> current) : IHeaderExposureDenyList
    {
        public bool IsEmpty => current().IsEmpty;
        public bool IsDenied(string name) => current().IsDenied(name);
        public Dictionary<string, string?> Filter(IEnumerable<KeyValuePair<string, string?>> headers) => current().Filter(headers);
        public JsonElement? Apply(JsonElement? recorded) => current().Apply(recorded);
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// R1 — the issue's core finding: a field the master schema guards with <c>x-roles</c> is pruned from instance
    /// GET, list and the data function for a caller without the grant, and must be pruned from history the same way.
    /// </summary>
    [Fact]
    public async Task R1_AnXRolesGuardedBodyField_IsPrunedForACallerWithoutTheGrant()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: $$"""{ "nationalId": "{{NationalId}}", "amount": 100 }""",
            header: """{ "x-request-id": "abc" }""");
        GivenTheMasterSchemaCanBeRead();
        GivenTheCallerHasRoles(Reader);

        var row = await ReadTheOnlyHistoryRowAsync(instance);

        row.Body.ShouldNotBeNull("an unguarded field is still the caller's to read");
        row.Body!.Value.GetProperty("amount").GetInt32().ShouldBe(100);
        row.Body.Value.TryGetProperty("nationalId", out _)
            .ShouldBeFalse("a reader without the x-roles grant must not see the guarded field in history");
    }

    /// <summary>R1 counter-check: the caller holding the grant still reads the field — the fix filters, it does not drop.</summary>
    [Fact]
    public async Task R1_AnXRolesGuardedBodyField_IsServedToACallerWithTheGrant()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: $$"""{ "nationalId": "{{NationalId}}", "amount": 100 }""",
            header: "{}");
        GivenTheMasterSchemaCanBeRead();
        GivenTheCallerHasRoles(Admin);

        var row = await ReadTheOnlyHistoryRowAsync(instance);

        row.Body!.Value.GetProperty("nationalId").GetString().ShouldBe(NationalId);
    }

    /// <summary>
    /// R2b — compatibility pin for <c>Workflow:HeaderExposure:Deny</c>: with no configuration the recorded header
    /// set is served exactly as before the setting existed. A secure default is a Chair decision (K2), not this change.
    /// </summary>
    [Fact]
    public async Task R2b_WithoutHeaderConfiguration_TheStoredHeaderSetIsServedAsToday()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: "{}",
            header: $$"""{ "authorization": "Bearer test-token", "x-actor": "{{NationalId}}", "x-request-id": "abc" }""");
        GivenTheMasterSchemaCanBeRead();
        GivenTheCallerHasRoles(Reader);

        var row = await ReadTheOnlyHistoryRowAsync(instance);

        row.Header.ShouldNotBeNull();
        row.Header!.Value.GetProperty("authorization").GetString().ShouldBe("Bearer test-token");
        row.Header.Value.GetProperty("x-actor").GetString().ShouldBe(NationalId);
        row.Header.Value.GetProperty("x-request-id").GetString().ShouldBe("abc");
    }

    /// <summary>
    /// R3 — fail-closed: when the master schema cannot be read, the filter applies nothing. Instance data falls back to
    /// its stored form, which keeps encrypt values as tokens; a transition body is stored as the plaintext request, so
    /// falling back to it would serve every guarded field in clear. The body is withheld; the row itself is still served.
    /// </summary>
    [Fact]
    public async Task R3_WhenTheMasterSchemaCannotBeRead_TheBodyIsWithheldAndTheRowIsStillServed()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: $$"""{ "nationalId": "{{NationalId}}", "amount": 100 }""",
            header: "{}");
        _componentCacheStore
            .GetSchemaAsync(SchemaReference.Domain, SchemaReference.Key, SchemaReference.Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.NotFound("schema", "gone")));
        GivenTheCallerHasRoles(Reader);

        var row = await ReadTheOnlyHistoryRowAsync(instance);

        row.TransitionId.ShouldBe("approve");
        row.FromState.ShouldBe("draft");
        row.Body.ShouldBeNull("an unreadable plan must withhold the plaintext body, never serve it as stored");
    }

    /// <summary>
    /// R2a — a configured deny list keeps the credential and identity headers out of the history response. This is what
    /// protects rows recorded before the list was configured: they are never rewritten.
    /// </summary>
    [Fact]
    public async Task R2a_ConfiguredDenyList_DropsTheDeniedHeadersFromTheResponse()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: "{}",
            header: $$"""{ "authorization": "Bearer test-token", "x-actor": "{{NationalId}}", "x-request-id": "abc" }""");
        GivenTheMasterSchemaCanBeRead();
        GivenTheCallerHasRoles(Reader);
        _denyList = DenyList("Authorization", "x-actor");

        var row = await ReadTheOnlyHistoryRowAsync(instance);

        row.Header!.Value.TryGetProperty("authorization", out _).ShouldBeFalse();
        row.Header.Value.TryGetProperty("x-actor", out _).ShouldBeFalse();
        row.Header.Value.GetProperty("x-request-id").GetString().ShouldBe("abc");
    }

    /// <summary>
    /// <c>x-masking</c> and <c>x-encryption</c> apply to a recorded body as they do to instance data. The body is the
    /// plaintext request, so a non-exempt caller gets a full mask on an <c>encrypt</c> field (there is no stored token
    /// to serve) and a <c>hash</c> field's raw value is masked rather than echoed.
    /// </summary>
    [Fact]
    public async Task MaskingAndEncryptionRules_ApplyToTheRecordedPlaintextBody()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: """{ "iban": "TR330006100519786457841326", "email": "user@example.com", "tckn": "98765432109", "amount": 5 }""",
            header: "{}");
        GivenTheMasterSchemaCanBeRead("""
            "iban":   { "type": "string", "x-masking": { "operator": "mask", "params": { "keepFirst": 2, "keepLast": 4 } } },
            "email":  { "type": "string", "x-encryption": { "type": "encrypt" } },
            "tckn":   { "type": "string", "x-encryption": { "type": "hash" } },
            "amount": { "type": "number" }
            """);
        GivenTheCallerHasRoles(Reader);

        var body = (await ReadTheOnlyHistoryRowAsync(instance)).Body!.Value;

        body.GetProperty("iban").GetString().ShouldBe("TR********************1326");
        body.GetProperty("email").GetString().ShouldNotBe("user@example.com");
        body.GetProperty("tckn").GetString().ShouldNotBe("98765432109");
        body.GetProperty("amount").GetInt32().ShouldBe(5);
    }

    /// <summary>A workflow without a master schema has nothing to protect: the recorded body is served as recorded.</summary>
    [Fact]
    public async Task AWorkflowWithoutAMasterSchema_ServesTheRecordedBody()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: $$"""{ "nationalId": "{{NationalId}}" }""",
            header: "{}");
        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(WorkflowWithoutSchema()));
        GivenTheCallerHasRoles(Reader);

        var row = await ReadTheOnlyHistoryRowAsync(instance);

        row.Body!.Value.GetProperty("nationalId").GetString().ShouldBe(NationalId);
    }

    /// <summary>A workflow definition that cannot be loaded leaves the rules unknown: bodies withheld, rows and headers served.</summary>
    [Fact]
    public async Task AWorkflowDefinitionThatCannotBeLoaded_WithholdsTheBody()
    {
        var instance = GivenAnInstanceWithOneTransition(
            body: $$"""{ "nationalId": "{{NationalId}}" }""",
            header: """{ "x-request-id": "abc" }""");
        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Fail(Error.NotFound("flow", "gone")));
        GivenTheCallerHasRoles(Reader);

        var row = await ReadTheOnlyHistoryRowAsync(instance);

        row.Body.ShouldBeNull();
        row.Header!.Value.GetProperty("x-request-id").GetString().ShouldBe("abc");
    }

    /// <summary>
    /// The caller's plan is decided once per request, not per row: one role resolution and one schema read for every
    /// transition — the start row (data a SubFlow parent carried), a mapped row and a manual row alike.
    /// </summary>
    [Fact]
    public async Task ThePlanIsDecidedOncePerRequest_AndAppliedToEveryRow()
    {
        var instance = GivenAnInstanceWithTransitions(
            "{}",
            ("start", $$"""{ "nationalId": "{{NationalId}}", "amount": 1 }"""),
            ("approve", $$"""{ "nationalId": "{{NationalId}}", "amount": 2 }"""),
            ("complete", """{ "amount": 3 }"""));
        GivenTheMasterSchemaCanBeRead();
        GivenTheCallerHasRoles(Reader);

        var result = await _service.GetInstanceHistoryAsync(Input(instance), CancellationToken.None);

        result.Value!.Transitions.Count.ShouldBe(3);
        foreach (var row in result.Value.Transitions)
            row.Body!.Value.TryGetProperty("nationalId", out _).ShouldBeFalse(row.TransitionId);
        await _roles.Received(1).ResolveRolesAsync(
            Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>());
        await _componentCacheStore.Received(1).GetSchemaAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An instance without transitions still answers an empty list.</summary>
    [Fact]
    public async Task AnInstanceWithoutTransitions_ReturnsAnEmptyList()
    {
        var instance = GivenAnInstanceWithTransitions("{}");
        GivenTheMasterSchemaCanBeRead();
        GivenTheCallerHasRoles(Reader);

        var result = await _service.GetInstanceHistoryAsync(Input(instance), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Transitions.ShouldBeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string SchemaProperties = """
        "nationalId": { "type": "string", "x-roles": [ { "role": "morph-idm.admin", "grant": "allow" } ] },
        "amount":     { "type": "number" }
        """;

    private Instance GivenAnInstanceWithOneTransition(string body, string header) =>
        GivenAnInstanceWithTransitions(header, ("approve", body));

    private Instance GivenAnInstanceWithTransitions(string header, params (string Key, string Body)[] rows)
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var transitions = new List<InstanceTransition>();
        foreach (var (key, body) in rows)
            transitions.Add(InstanceTransition.Create(
                id: Guid.NewGuid(),
                instanceId: instance.Id,
                transitionId: key,
                fromState: "draft",
                triggerType: TriggerType.Manual,
                body: new JsonData(body),
                header: new JsonData(header)));

        // The test pins what is served, not how the instance is loaded: any finder answers.
        var identifier = instance.Id.ToString();
        _instanceRepository.FindByIdentifierWithFullHistoryAsync(identifier, Arg.Any<CancellationToken>()).Returns(instance);
        _instanceRepository.FindByIdentifierAsReadOnlyAsync(identifier, Arg.Any<CancellationToken>()).Returns(instance);
        _instanceRepository.FindByIdentifierAsync(identifier, Arg.Any<CancellationToken>()).Returns(instance);
        _transitionRepository
            .GetByInstanceIdAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(transitions);

        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(WorkflowWithMasterSchema()));
        return instance;
    }

    private void GivenTheMasterSchemaCanBeRead(string properties = SchemaProperties)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            "{\"type\":\"master\",\"schema\":{\"type\":\"object\",\"properties\":{" + properties + "}}}", options)!;
        schema.SetReference(SchemaReference);
        _componentCacheStore
            .GetSchemaAsync(SchemaReference.Domain, SchemaReference.Key, SchemaReference.Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
    }

    private void GivenTheCallerHasRoles(params string[] roles) =>
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(roles));

    private static GetInstanceHistoryInput Input(Instance instance) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instance.Id.ToString(),
        Headers = new Dictionary<string, string?>(),
        QueryParameters = new Dictionary<string, string?>()
    };

    private async Task<InstanceTransitionDto> ReadTheOnlyHistoryRowAsync(Instance instance)
    {
        var result = await _service.GetInstanceHistoryAsync(Input(instance), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Transitions.Count.ShouldBe(1);
        return result.Value.Transitions[0];
    }

    private static Definitions.Workflow WorkflowWithoutSchema()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>("{\"type\":\"F\",\"states\":[]}", options)!;
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));
        return workflow;
    }

    private static Definitions.Workflow WorkflowWithMasterSchema()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(
            "{\"type\":\"F\",\"states\":[],\"schema\":" + JsonSerializer.Serialize(SchemaReference) + "}", options)!;
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));
        return workflow;
    }
}
