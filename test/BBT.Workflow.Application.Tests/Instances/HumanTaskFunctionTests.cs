using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Extentions;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Filtering;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Caching;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The <c>human-task</c> domain function — the endpoint morph-idm fans out over every domain to
/// build a banker's task list. It had no test of any kind, while carrying an authorization filter,
/// a subflow descent and an unbounded fan-out over every workflow schema.
/// <para>
/// These cover what the response cannot show on its own. A dropped row and a row the caller may not
/// act on are indistinguishable in the output — both just make the list shorter — so the counters
/// and the truncation signal are the only way an incomplete answer is visible at all.
/// </para>
/// </summary>
public class HumanTaskFunctionTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string FlowKey = "loan-application";
    private const string HumanState = "collect-documents";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly ICurrentSchema _currentSchema = Substitute.For<ICurrentSchema>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly ITransitionAuthorizationManager _authorizationManager =
        Substitute.For<ITransitionAuthorizationManager>();
    private readonly IRoleGrantEvaluator _evaluator = Substitute.For<IRoleGrantEvaluator>();
    private readonly IHumanTaskFunctionCache _cache = Substitute.For<IHumanTaskFunctionCache>();
    private readonly InstanceQueryAppService _service;
    private readonly ServiceProvider _provider;
    private readonly IServiceProvider? _previousAmbient;

    public HumanTaskFunctionTests()
        : this(new HumanTaskFunctionOptions())
    {
    }

    private HumanTaskFunctionTests(HumanTaskFunctionOptions bounds)
    {
        _currentSchema.Change(Arg.Any<string>()).Returns(_ => Substitute.For<IDisposable>());

        // The fan-out opens its own DI scopes and resolves these there, so substitutes have to be
        // reachable from a real container rather than only from the constructor.
        var services = new ServiceCollection();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        services.AddSingleton(uowManager);
        services.AddSingleton(_componentCacheStore);
        services.AddSingleton(_instanceRepository);
        services.AddSingleton(_currentSchema);
        services.AddSingleton(_authorizationManager);
        services.AddSingleton(_runtimeInfoProvider);
        services.AddSingleton(Options.Create(bounds));
        services.AddLogging();
        // The REAL resolver, so the descent — leaf selection, leaf-side authorization and leaf-side
        // payload — is exercised rather than stubbed out.
        services.AddSingleton<IHumanTaskLeafGateway, RecursingLeafGateway>();
        services.AddSingleton<IHumanTaskLeafResolver, HumanTaskLeafResolver>();
        _provider = services.BuildServiceProvider();

        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _provider;

        _authorizationManager.CreateEvaluatorAsync(
                Arg.Any<Instance>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<IEnumerable<RoleGrant>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_evaluator));
        Authorize(true);

        _service = new InstanceQueryAppService(
            serviceProvider: _provider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: Substitute.For<IInstanceTaskRepository>(),
            instanceActionRepository: Substitute.For<IInstanceActionRepository>(),
            longPollInteractionGate: Substitute.For<Execution.LongPoll.ILongPollInteractionGate>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            currentSchema: _currentSchema,
            transitionAuthorizationManager: _authorizationManager,
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            callerRoleResolver: new DefaultCallerRoleResolver(_caller),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(bounds),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<IInstanceSchemaFunctionCache>(),

            humanTaskFunctionCache: _cache,
            descentLimiter: new HumanTask.HumanTaskDescentLimiter(
                Microsoft.Extensions.Options.Options.Create(new HumanTask.HumanTaskFunctionOptions())),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reads the list as a caller holding <paramref name="roles"/>. Set on ICurrentUser rather than
    /// in a header because that is the branch ResolveCallerRoles takes first, so the test says what
    /// it means without depending on a header name.
    /// </summary>
    private async Task<IReadOnlyList<HumanTaskItemOutput>> ListAsAsync(params string[] roles)
    {
        _caller.Roles.Returns(roles);
        var result = await _service.GetHumanTaskInstancesAsync(Domain, headers: null, cacheOverride: true);
        return result.Value!.Items;
    }

    /// <summary>
    /// Makes the substituted evaluator apply the REAL rule to the grants it is handed, through the
    /// production static path. Opt-in, so the tests that only care about descent, identity or
    /// caching keep their fixed answer and are not rewritten into role fixtures.
    /// </summary>
    /// <remarks>
    /// Delegating to <c>EvaluateRolesStatic</c> rather than restating the rule is deliberate: a test
    /// that re-implements the thing it is testing passes whatever the test author believed. These
    /// grant sets are static-only, which is exactly what that path resolves.
    /// </remarks>
    private void UseRealGrantEvaluation() =>
        _evaluator.IsAnyRoleAllowed(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Transition?>())
            .Returns(call => TransitionAuthorizationManager.EvaluateRolesStatic(
                call.Arg<IReadOnlyCollection<string>>() ?? [],
                call.Arg<IReadOnlyCollection<RoleGrant>>()));

    private void Authorize(bool allowed) =>
        _evaluator.IsAnyRoleAllowed(
                Arg.Any<string[]>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(), Arg.Any<Transition?>())
            .Returns(allowed);

    private void SchemasAre(params string[] flowKeys) =>
        _instanceRepository.GetActiveFlowKeysAsync(Arg.Any<CancellationToken>())
            .Returns(flowKeys.ToList());

    private void CandidatesAre(params Instance[] instances)
    {
        var list = instances.ToList();
        // The scan returns a projection for EVERY flow in one call — one statement on one
        // connection — so the flow each row came from travels on the row itself.
        _instanceRepository.GetHumanTaskCandidatesAcrossFlowsAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([.. list.Select(i => new HumanTaskCandidate(i.Id, i.Key, i.Type, i.CreatedAt, FlowKey))]);
        // The descent reloads the candidates it was handed, by id, one batch per level.
        _instanceRepository
            .GetForHumanTaskDescentAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                return list.Where(i => ids.Contains(i.Id)).ToList();
            });
    }

    private void FlowResolvesTo(Definitions.Workflow? workflow) =>
        _componentCacheStore.GetFlowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(workflow is null
                ? Result<Definitions.Workflow>.Fail(Error.NotFound("flow", "missing"))
                : Result<Definitions.Workflow>.Ok(workflow));

    /// <summary>
    /// A workflow whose human state declares <c>queryRoles</c> — the gate this list is decided by.
    /// </summary>
    /// <remarks>
    /// Built from JSON because <c>queryRoles</c> has no public setter on the aggregate; it is
    /// authored, not constructed. The state also carries a transition so the shape stays realistic,
    /// but the transition no longer influences the answer and several tests below assert exactly
    /// that.
    /// </remarks>
    private static Definitions.Workflow ActionableFlow(
        string stateKey = HumanState,
        string stateQueryRoles = """[{"role":"ht-approver","grant":"allow"}]""",
        string rootQueryRoles = "[]") =>
        JsonSerializer.Deserialize<Definitions.Workflow>($$"""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [
                {
                  "key": "{{stateKey}}",
                  "stateType": "intermediate",
                  "subType": "human",
                  "labels": [],
                  "queryRoles": {{stateQueryRoles}},
                  "transitions": [
                    { "key": "approve", "target": "approved", "triggerType": "manual", "labels": [] }
                  ]
                }
              ],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": {{rootQueryRoles}}
            }
            """, FlowJsonOptions)!;

    private static readonly JsonSerializerOptions FlowJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static Instance WaitingInstance(string key, string stateKey = HumanState, string? title = null)
    {
        var instance = Instance.Create(Guid.NewGuid(), FlowKey, "1.0.0", key);
        instance.ChangeState(StateFactory.CreateDefault(stateKey, StateType.Intermediate, StateSubType.Human));
        if (title is not null)
        {
            instance.SeedData(
                Guid.NewGuid(),
                new JsonData(JsonSerializer.SerializeToElement(
                    new { humanTask = new { title, description = $"{title} description" } })));
        }
        return instance;
    }

    /// <summary>
    /// Marks an instance as a SubProcess the way the runtime does — through the start metadata,
    /// which is the only writer of <c>Instance.Type</c> and is latched on <c>IsTransient</c>.
    /// </summary>
    private static void MarkAsSubProcess(Instance instance) =>
        instance.SetInfoMetadata(
            isSync: false,
            callback: null,
            flowType: WorkflowType.SubProcess.Code,
            userMetadata: new BBT.Aether.ExtraPropertyDictionary
            {
                [DomainConsts.MetaDataKeys.Id] = Guid.NewGuid().ToString(),
                [DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubProcess.Code
            });

    /// <summary>
    /// A SubProcess is an independent unit of work — nothing waits for it and nothing projects its
    /// state upward — so its row must be addressable by ITS OWN identity.
    /// </summary>
    /// <remarks>
    /// <c>SubflowStarter</c> gives every child <c>Key = parentInstance.Key</c>, for SubProcesses as
    /// much as for SubFlows. Emitting that inherited key is wrong twice over: two rows of one case
    /// collide on it, and a client following it lands on the PARENT instance rather than on the
    /// SubProcess that actually holds the task.
    /// </remarks>
    [Fact]
    public async Task ASubProcessRowIsAddressedByItsOwnIdNotTheKeyItInherited()
    {
        var spawned = WaitingInstance("PARENT-KEY", title: "spawned step");
        MarkAsSubProcess(spawned);

        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(spawned);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        var row = result.Value!.Items.ShouldHaveSingleItem();
        row.InstanceId.ShouldBe(spawned.Id.ToString(),
            "a SubProcess owns no business key — the one it carries belongs to the case that spawned it");
        row.Id.ShouldBe(spawned.Id);
    }

    /// <summary>A root DOES own its business key, so its row keeps addressing by it.</summary>
    [Fact]
    public async Task ARootRowKeepsBeingAddressedByItsBusinessKey()
    {
        var root = WaitingInstance("APP-1", title: "root step");

        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(root);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.Value!.Items.ShouldHaveSingleItem().InstanceId.ShouldBe("APP-1");
    }

    private void CacheIs(bool enabled, bool allowOverride = true)
    {
        _cache.Enabled.Returns(enabled);
        _cache.AllowClientOverride.Returns(allowOverride);
        _cache.BuildKey(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>())
            .Returns("human-task:v1:test");
    }

    [Fact]
    public async Task AWarmCacheIsServedWithoutTouchingAnyWorkflowSchema()
    {
        CacheIs(enabled: true);
        _cache.GetAsync("human-task:v1:test", Arg.Any<CancellationToken>())
            .Returns(new HumanTaskFunctionCacheEntry
            {
                Items = [new HumanTaskItemOutput { InstanceId = "APP-CACHED", Title = "cached" }],
                Truncated = true
            });

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.Value!.Items.ShouldHaveSingleItem().InstanceId.ShouldBe("APP-CACHED");
        result.Value.Truncated.ShouldBeTrue();
        await _instanceRepository.DidNotReceive().GetActiveFlowKeysAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The override skips the READ and still performs the WRITE. Skipping both would hand an
    /// unauthenticated caller a free way to make this endpoint more expensive than it is with no
    /// cache at all — on a route with no rate limiter of its own.
    /// </summary>
    [Fact]
    public async Task TheOverrideSkipsTheCacheReadButStillWritesTheResultBack()
    {
        CacheIs(enabled: true);
        _cache.GetAsync("human-task:v1:test", Arg.Any<CancellationToken>())
            .Returns(new HumanTaskFunctionCacheEntry
            {
                Items = [new HumanTaskItemOutput { InstanceId = "APP-STALE", Title = "stale" }]
            });
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(WaitingInstance("APP-FRESH", title: "fresh"));

        var result = await _service.GetHumanTaskInstancesAsync(Domain, headers: null, cacheOverride: true);

        result.Value!.Items.ShouldHaveSingleItem().InstanceId.ShouldBe("APP-FRESH");
        await _cache.Received(1).SetAsync(
            "human-task:v1:test", Arg.Any<HumanTaskFunctionCacheEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheOverrideIsIgnoredWhenTheServerDisallowsIt()
    {
        CacheIs(enabled: true, allowOverride: false);
        _cache.GetAsync("human-task:v1:test", Arg.Any<CancellationToken>())
            .Returns(new HumanTaskFunctionCacheEntry
            {
                Items = [new HumanTaskItemOutput { InstanceId = "APP-CACHED", Title = "cached" }]
            });

        var result = await _service.GetHumanTaskInstancesAsync(Domain, headers: null, cacheOverride: true);

        result.Value!.Items.ShouldHaveSingleItem().InstanceId.ShouldBe("APP-CACHED");
    }

    [Fact]
    public async Task WithTheCacheDisabledNothingIsReadOrWritten()
    {
        CacheIs(enabled: false);
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(WaitingInstance("APP-1", title: "one"));

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.Value!.Items.ShouldHaveSingleItem();
        await _cache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<HumanTaskFunctionCacheEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Stands in for the routed gateway: every hop in these tests is same-domain, so it re-enters
    /// the resolver directly. The production local gateway does the same thing through a fresh DI
    /// scope, which is what breaks the resolver/gateway construction cycle; here the cycle is
    /// broken by resolving lazily instead.
    /// </summary>
    private sealed class RecursingLeafGateway(IServiceProvider serviceProvider) : IHumanTaskLeafGateway
    {
        public Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
            string domain, string flow, HumanTaskLeafRequest request, CancellationToken cancellationToken = default)
            => serviceProvider.GetRequiredService<IHumanTaskLeafResolver>()
                .ResolveAsync(domain, flow, request, cancellationToken);
    }

    private const string ChildFlow = "document-check";
    private const string GrandchildFlow = "manual-review";

    /// <summary>Attaches an open blocking SubFlow correlation pointing at <paramref name="child"/>.</summary>
    private static void Descends(Instance parent, Instance child, string childFlow, string childDomain = Domain)
    {
        parent.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            parent.Id,
            parent.GetCurrentState,
            child.Id,
            SubFlowType.SubFlow.Code,
            childDomain,
            childFlow,
            "1.0.0"));
    }

    /// <summary>Routes each flow key to its own definition, the way the component cache does.</summary>
    private void FlowsResolveTo(Dictionary<string, Definitions.Workflow> byKey) =>
        _componentCacheStore.GetFlowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => byKey.TryGetValue(call.ArgAt<string>(1), out var wf)
                ? Result<Definitions.Workflow>.Ok(wf)
                : Result<Definitions.Workflow>.Fail(Error.NotFound("flow", call.ArgAt<string>(1))));

    private void InstancesAre(params Instance[] all)
    {
        var list = all.ToList();
        _instanceRepository
            .GetForHumanTaskDescentAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                return list.Where(i => ids.Contains(i.Id)).ToList();
            });
    }

    /// <summary>
    /// The case the old one-level descent could not answer. The root is what the client holds and
    /// what the row must be addressed by; the work is two levels down, and both the authorization
    /// decision and the title have to come from there.
    /// </summary>
    [Fact]
    public async Task AtDepthTwo_TheRowKeepsTheRootsIdentityAndTakesTheLeafsText()
    {
        var root = WaitingInstance("APP-1", "awaiting-check", title: "root text");
        var child = WaitingInstance("APP-1", "awaiting-review", title: "child text");
        var grandchild = WaitingInstance("APP-1", HumanState, title: "sign the mandate");
        Descends(root, child, ChildFlow);
        Descends(child, grandchild, GrandchildFlow);

        SchemasAre(FlowKey);
        FlowsResolveTo(new Dictionary<string, Definitions.Workflow>
        {
            [FlowKey] = ActionableFlow("awaiting-check"),
            [ChildFlow] = ActionableFlow("awaiting-review"),
            [GrandchildFlow] = ActionableFlow(HumanState)
        });
        CandidatesAre(root);
        InstancesAre(root, child, grandchild);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        var item = result.Value!.Items.ShouldHaveSingleItem();
        item.InstanceId.ShouldBe("APP-1");
        item.Id.ShouldBe(root.Id);
        item.Workflow.ShouldBe(FlowKey);
        item.Title.ShouldBe("sign the mandate");
        item.Description.ShouldBe("sign the mandate description");
    }

    /// <summary>
    /// Authorization is decided on the LEAF's state. Previously the root's state was authorized and
    /// the leaf's text was never read, so a caller could be offered a task whose actual step they
    /// could not act on.
    /// </summary>
    [Fact]
    public async Task TheLeafsStateIsWhatDecidesAuthorization()
    {
        var root = WaitingInstance("APP-1", "awaiting-check", title: "root text");
        var child = WaitingInstance("APP-1", HumanState, title: "child text");
        Descends(root, child, ChildFlow);

        // The leaf's state exists in its definition but offers nothing to act on; the ROOT's state
        // would have offered a transition.
        var leafFlow = WorkflowFactory.CreateDefault(ChildFlow, Domain);
        leafFlow.SetStartTransition(TransitionFactory.CreateDefault("start", null, HumanState));
        leafFlow.AddState(StateFactory.CreateDefault(HumanState, StateType.Intermediate, StateSubType.Human));

        SchemasAre(FlowKey);
        FlowsResolveTo(new Dictionary<string, Definitions.Workflow>
        {
            [FlowKey] = ActionableFlow("awaiting-check"),
            [ChildFlow] = leafFlow
        });
        CandidatesAre(root);
        InstancesAre(root, child);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.Value!.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// The descent is bounded. A correlation graph that somehow cycles must not walk forever on a
    /// public read path, and hitting the bound is an incomplete answer — reported, never silently
    /// turned into "no task here".
    /// </summary>
    [Fact]
    public async Task ACyclicCorrelationStopsAtTheDepthBoundInsteadOfWalkingForever()
    {
        var fixture = new HumanTaskFunctionTests(
            new HumanTaskFunctionOptions { PerSchemaLimit = 200, ResultCap = 500, MaxDescentDepth = 3 });
        try
        {
            var a = WaitingInstance("APP-1", "awaiting-check", title: "a");
            var b = WaitingInstance("APP-1", "awaiting-review", title: "b");
            Descends(a, b, ChildFlow);
            Descends(b, a, FlowKey);

            fixture.SchemasAre(FlowKey);
            fixture.FlowsResolveTo(new Dictionary<string, Definitions.Workflow>
            {
                [FlowKey] = ActionableFlow("awaiting-check"),
                [ChildFlow] = ActionableFlow("awaiting-review")
            });
            fixture.CandidatesAre(a);
            fixture.InstancesAre(a, b);

            var result = await fixture._service.GetHumanTaskInstancesAsync(Domain);

            result.IsSuccess.ShouldBeTrue();
            result.Value!.Items.ShouldBeEmpty();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// A cross-domain child used to resolve against the CALLER's domain and yield nothing, which
    /// removed the whole instance from the list. Now the hop is handed to the domain that owns it.
    /// </summary>
    [Fact]
    public async Task ACrossDomainChildIsRoutedToItsOwnDomainRatherThanTheCallers()
    {
        var root = WaitingInstance("APP-1", "awaiting-check", title: "root text");
        var child = WaitingInstance("APP-1", HumanState, title: "partner step");
        Descends(root, child, ChildFlow, childDomain: "partner");

        SchemasAre(FlowKey);
        FlowsResolveTo(new Dictionary<string, Definitions.Workflow>
        {
            [FlowKey] = ActionableFlow("awaiting-check"),
            [ChildFlow] = ActionableFlow(HumanState)
        });
        CandidatesAre(root);
        InstancesAre(root, child);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        var item = result.Value!.Items.ShouldHaveSingleItem();
        item.Title.ShouldBe("partner step");

        // The hop carried the correlation's own domain, not the caller's.
        await _componentCacheStore.Received()
            .GetFlowAsync("partner", ChildFlow, "1.0.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoWorkflowSchemas_TheListIsEmptyAndNothingIsTruncated()
    {
        SchemasAre();

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBeEmpty();
        result.Value.Truncated.ShouldBeFalse();
    }

    /// <summary>
    /// The business key is what a client addresses, but it is not unique — a SubProcess child
    /// inherits its parent's key. Every row therefore also carries its own id, additively, so the
    /// consumer can tell two rows of one case apart without the array shape changing.
    /// </summary>
    [Fact]
    public async Task EveryRowCarriesItsOwnIdBesideTheBusinessKey()
    {
        var instance = WaitingInstance("APP-1", title: "Collect documents");
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(instance);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        var item = result.Value!.Items.ShouldHaveSingleItem();
        item.InstanceId.ShouldBe("APP-1");
        item.Id.ShouldBe(instance.Id);
        item.Workflow.ShouldBe(FlowKey);
        item.Title.ShouldBe("Collect documents");
        item.VNext.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnauthorizedCallerGetsAnEmptyListRatherThanAnError()
    {
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));
        Authorize(false);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBeEmpty();
        result.Value.Truncated.ShouldBeFalse();
    }

    /// <summary>
    /// The widest silent drop on this path: one unresolvable definition takes every instance
    /// waiting in that workflow with it, and the response is simply shorter. It must be counted
    /// and logged, never swallowed.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableWorkflowDropsItsWholeContributionWithoutFailingTheRequest()
    {
        SchemasAre(FlowKey);
        FlowResolvesTo(null);
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// The per-schema limit reaches SQL — an unbounded query here is unbounded end to end, because
    /// the response has no paging and morph-idm multiplies it by every registered domain.
    /// </summary>
    [Fact]
    public async Task ThePerSchemaLimitIsPassedToTheRepository()
    {
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre();

        await _service.GetHumanTaskInstancesAsync(Domain);

        await _instanceRepository.Received(1)
            .GetHumanTaskCandidatesAcrossFlowsAsync(
                Arg.Any<IReadOnlyList<string>>(), 200, 64, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Every flow is scanned in ONE call, whatever the domain's flow count. The call count IS the
    /// connection count for this phase, and the per-flow form is what exhausted the server's
    /// connection slots under concurrent callers.
    /// </summary>
    [Fact]
    public async Task EveryFlowIsScannedInASingleRepositoryCall()
    {
        SchemasAre("flow-a", "flow-b", "flow-c", FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre();

        await _service.GetHumanTaskInstancesAsync(Domain);

        await _instanceRepository.Received(1)
            .GetHumanTaskCandidatesAcrossFlowsAsync(
                Arg.Is<IReadOnlyList<string>>(keys => keys.Count == 4),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A flow that produced no candidate is never descended. The descent is the remaining fan-out,
    /// so this is what keeps its width at "flows with work" instead of "flows that exist".
    /// </summary>
    [Fact]
    public async Task AFlowWithNoCandidatesIsNeverDescended()
    {
        SchemasAre("flow-a", "flow-b", "flow-c", FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre();

        await _service.GetHumanTaskInstancesAsync(Domain);

        await _instanceRepository.DidNotReceive()
            .GetForHumanTaskDescentAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A capped list must say so. The body stays a bare JSON array for the consumer, so the flag
    /// travels beside it and the HTTP layer turns it into a response header — a silently cut task
    /// list is a wrong answer, not a shorter one.
    /// </summary>
    [Fact]
    public async Task HittingTheResultCapTruncatesAndSaysSo()
    {
        var fixture = new HumanTaskFunctionTests(
            new HumanTaskFunctionOptions { PerSchemaLimit = 200, ResultCap = 2 });
        try
        {
            fixture.SchemasAre(FlowKey);
            fixture.FlowResolvesTo(ActionableFlow());
            fixture.CandidatesAre(
                WaitingInstance("APP-1", title: "one"),
                WaitingInstance("APP-2", title: "two"),
                WaitingInstance("APP-3", title: "three"));

            var result = await fixture._service.GetHumanTaskInstancesAsync(Domain);

            result.Value!.Items.Count.ShouldBe(2);
            result.Value.Truncated.ShouldBeTrue();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// A full per-schema page means the database had more to give, so the answer is incomplete even
    /// when the merged cap was never reached.
    /// </summary>
    [Fact]
    public async Task AFullPerSchemaPageAlsoMarksTheListTruncated()
    {
        var fixture = new HumanTaskFunctionTests(
            new HumanTaskFunctionOptions { PerSchemaLimit = 2, ResultCap = 500 });
        try
        {
            fixture.SchemasAre(FlowKey);
            fixture.FlowResolvesTo(ActionableFlow());
            fixture.CandidatesAre(
                WaitingInstance("APP-1", title: "one"),
                WaitingInstance("APP-2", title: "two"));

            var result = await fixture._service.GetHumanTaskInstancesAsync(Domain);

            result.Value!.Items.Count.ShouldBe(2);
            result.Value.Truncated.ShouldBeTrue();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task TheListIsOrderedNewestFirst()
    {
        var older = WaitingInstance("APP-OLD", title: "older");
        older.CreatedAt = DateTime.UtcNow.AddHours(-2);
        var newer = WaitingInstance("APP-NEW", title: "newer");
        newer.CreatedAt = DateTime.UtcNow;

        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(older, newer);

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.Value!.Items.Select(i => i.InstanceId).ShouldBe(["APP-NEW", "APP-OLD"]);
    }

    /// <summary>
    /// A state that offers nothing to act on is not a task. This is a skip, not a drop — nothing is
    /// missing from the answer.
    /// </summary>
    [Fact]
    public async Task AStateWithNoUserTransitionIsNotListed()
    {
        var workflow = WorkflowFactory.CreateDefault(FlowKey, Domain);
        workflow.SetStartTransition(TransitionFactory.CreateDefault("start", null, HumanState));
        workflow.AddState(StateFactory.CreateDefault(HumanState, StateType.Intermediate, StateSubType.Human));

        SchemasAre(FlowKey);
        FlowResolvesTo(workflow);
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.Value!.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// An instance whose current state is absent from the resolved definition leaves the list. The
    /// row still exists and still waits; the answer is incomplete, which is why it is counted.
    /// </summary>
    [Fact]
    public async Task AnInstanceWhoseStateIsNotInTheDefinitionIsDropped()
    {
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow("some-other-state"));
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        var result = await _service.GetHumanTaskInstancesAsync(Domain);

        result.Value!.Items.ShouldBeEmpty();
    }

    // ── The gate is the state's queryRoles ───────────────────────────────────────
    //
    // The list answers "which human tasks am I responsible for?" — a VISIBILITY question. Which
    // button is offered is settled later, when the client opens the instance. These pin that the
    // decision comes from queryRoles and from nothing else.

    /// <summary>
    /// A caller the state's <c>queryRoles</c> admit is listed; one they do not is not.
    /// </summary>
    [Fact]
    public async Task TheStatesQueryRolesDecideVisibility()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        var allowed = await ListAsAsync("ht-approver");
        allowed.ShouldHaveSingleItem();

        var refused = await ListAsAsync("someone-else");
        refused.ShouldBeEmpty();
    }

    /// <summary>
    /// Transition roles no longer influence the answer — the inversion of the previous rule.
    /// </summary>
    /// <remarks>
    /// Stated as its own test because it is the behaviour change: a caller who can act on nothing in
    /// the state is still listed when <c>queryRoles</c> admit them. That is intended. Actionability
    /// is resolved by the state function on open; conflating it with visibility is what made this a
    /// fourth authorization surface, let a role-less <c>cancel</c> authorize everyone, and produced a
    /// list that could be more permissive than the screen it led to.
    /// </remarks>
    [Fact]
    public async Task ACallerWithNoActionableTransitionIsStillListedWhenQueryRolesAdmitThem()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        // The state's only transition grants "approve-only", which the caller does not hold; its
        // queryRoles grant "ht-approver", which it does.
        FlowResolvesTo(ActionableFlow());
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        var result = await ListAsAsync("ht-approver");

        result.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A state that declares no <c>queryRoles</c>, and whose workflow root declares none either, is
    /// DROPPED rather than published to everyone.
    /// </summary>
    /// <remarks>
    /// An empty grant set allows everywhere else in the runtime. Letting that default through here
    /// would publish every human task of every flow that has not authored queryRoles to every
    /// caller — 8 of the 10 example flows with a human state, when measured. A task list is the one
    /// read surface where "no rule authored" must not mean "everyone", so this fails closed and logs
    /// loudly enough to drive the migration.
    /// </remarks>
    [Fact]
    public async Task AStateWithNoQueryRolesIsDroppedRatherThanShownToEveryone()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow(stateQueryRoles: "[]", rootQueryRoles: "[]"));
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        var result = await ListAsAsync("ht-approver");

        result.ShouldBeEmpty();
    }

    /// <summary>The workflow root's <c>queryRoles</c> cover a state that declares none of its own.</summary>
    [Fact]
    public async Task TheWorkflowRootQueryRolesCoverAStateThatDeclaresNone()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow(
            stateQueryRoles: "[]",
            rootQueryRoles: """[{"role":"ht-approver","grant":"allow"}]"""));
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        (await ListAsAsync("ht-approver")).ShouldHaveSingleItem();
        (await ListAsAsync("other")).ShouldBeEmpty();
    }

    /// <summary>The state's own <c>queryRoles</c> override the workflow root's.</summary>
    [Fact]
    public async Task TheStatesQueryRolesOverrideTheWorkflowRoots()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow(
            stateQueryRoles: """[{"role":"state-role","grant":"allow"}]""",
            rootQueryRoles: """[{"role":"root-role","grant":"allow"}]"""));
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        (await ListAsAsync("state-role")).ShouldHaveSingleItem();
        // Replace, not merge: the root's grant does not survive the state's declaration.
        (await ListAsAsync("root-role")).ShouldBeEmpty();
    }

    /// <summary>
    /// The parent's stamped state override beats the child's own <c>queryRoles</c>.
    /// </summary>
    /// <remarks>
    /// <c>SubflowStarter</c> has always written <c>subflow.state_role_overrides</c> beside the
    /// transition map, but nothing in <c>src/</c> read it — a dead write. This is its first reader,
    /// and it is what puts state overrides on the same footing as the transition and view overrides:
    /// a parent that narrows a child's visibility must have that narrowing honoured at the leaf,
    /// where the parent-side reader cannot reach because a leaf has no active correlation.
    /// </remarks>
    [Fact]
    public async Task TheParentsStampedStateOverrideBeatsTheChildsOwnQueryRoles()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());   // child grants ht-approver

        var instance = WaitingInstance("APP-1", title: "Collect documents");
        instance.ExtraProperties[DomainConsts.MetaDataKeys.StateRoleOverrides] =
            $$$"""{"{{{HumanState}}}":{"queryRoles":[{"role":"parent-only","grant":"allow"}]}}""";
        CandidatesAre(instance);

        (await ListAsAsync("parent-only")).ShouldHaveSingleItem();
        // The child's own grant is replaced, not merged — the parent narrowed it deliberately.
        (await ListAsAsync("ht-approver")).ShouldBeEmpty();
    }

    /// <summary>A stamped map for another state leaves this state's own grants in force.</summary>
    [Fact]
    public async Task AStampedOverrideForAnotherStateDoesNotApply()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());

        var instance = WaitingInstance("APP-1", title: "Collect documents");
        instance.ExtraProperties[DomainConsts.MetaDataKeys.StateRoleOverrides] =
            """{"some-other-state":{"queryRoles":[{"role":"parent-only","grant":"allow"}]}}""";
        CandidatesAre(instance);

        (await ListAsAsync("ht-approver")).ShouldHaveSingleItem();
    }

    /// <summary>A malformed stamp degrades to the child's own grants, never to allowing everyone.</summary>
    [Fact]
    public async Task AMalformedStampedOverrideFallsBackToTheChildsOwnQueryRoles()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow());

        var instance = WaitingInstance("APP-1", title: "Collect documents");
        instance.ExtraProperties[DomainConsts.MetaDataKeys.StateRoleOverrides] = "{ not json";
        CandidatesAre(instance);

        (await ListAsAsync("ht-approver")).ShouldHaveSingleItem();
        (await ListAsAsync("other")).ShouldBeEmpty();
    }

    /// <summary>
    /// A DENY in <c>queryRoles</c> refuses even a caller whose other role is allowed.
    /// </summary>
    [Fact]
    public async Task ADenyInQueryRolesRefusesACallerWhoseOtherRoleIsAllowed()
    {
        UseRealGrantEvaluation();
        SchemasAre(FlowKey);
        FlowResolvesTo(ActionableFlow(
            stateQueryRoles: """[{"role":"ht-approver","grant":"allow"},{"role":"blocked","grant":"deny"}]"""));
        CandidatesAre(WaitingInstance("APP-1", title: "Collect documents"));

        (await ListAsAsync("ht-approver")).ShouldHaveSingleItem();
        (await ListAsAsync("ht-approver", "blocked")).ShouldBeEmpty();
    }
}
