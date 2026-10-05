using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Correlation;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the instance-correlation tree read (<see cref="InstanceQueryAppService.GetInstanceCorrelationAsync"/>,
/// vnext-client-sdk-core#58 AB-20). The contract clients depend on is the DIRECTION and the RECURSION:
/// the tree is walked <b>parent → child</b>, top-down, so a caller asking about an instance sees that
/// instance as the root and every correlated descendant nested beneath it — never its ancestors.
/// </summary>
/// <remarks>
/// Renamed from "hierarchy" to "instance-correlation" (hard break, no alias): the tree is built purely
/// from <see cref="InstanceCorrelation"/> rows, so the name now says what it actually reads.
/// </remarks>
public sealed class InstanceQueryAppServiceCorrelationTreeTests : IDisposable
{
    private const string TestDomain = "test-domain";
    private const string RootFlow = "root-flow";
    private const string ChildFlow = "child-flow";
    private const string GrandchildFlow = "grandchild-flow";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceCorrelationRepository _correlationRepository =
        Substitute.For<IInstanceCorrelationRepository>();
    private readonly IUrlTemplateBuilder _urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();

    // The resolver reads a LEVEL at a time, so the stubs are registries the batch reads filter,
    // not per-id returns. StubInstance/StubChildren populate them; every test body is unchanged.
    private readonly Dictionary<Guid, Instance> _instances = [];
    private readonly Dictionary<Guid, List<InstanceCorrelation>> _childrenByParent = [];
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceQueryAppServiceCorrelationTreeTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));

        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        // Echo the arguments back, so a swapped domain/flow/instance would produce a different string
        // and the href assertions below would fail instead of passing on a null substitute return.
        _urlTemplateBuilder
            .BuildInstanceUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => $"/{ci.ArgAt<string>(0)}/workflows/{ci.ArgAt<string>(1)}/instances/{ci.ArgAt<string>(2)}");

        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore
            .GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(Definitions.Workflow.Create()));

        // Batch reads, served from the registries the two Stub* helpers populate. This is what the
        // resolver actually calls: one correlation read and one instance read PER LEVEL, never per
        // node — so a test that stubs a level gets the whole level in one answer.
        _correlationRepository
            .GetByParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.ArgAt<IReadOnlyCollection<Guid>>(0);
                return ids
                    .SelectMany(id => _childrenByParent.TryGetValue(id, out var rows)
                        ? rows
                        : Enumerable.Empty<InstanceCorrelation>())
                    .ToList();
            });

        _instanceRepository
            .GetForCorrelationWalkAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            // Projects from the same stubbed instances the root lookup uses, exactly as the EF
            // query projects from the row — one source of truth, two shapes.
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(_instances.ContainsKey)
                .Select(id => _instances[id])
                .Select(i => new CorrelationWalkRow(i.Id, i.Key, i.CurrentState, i.Status, i.FlowVersion))
                .ToList());

        var currentSchema = Substitute.For<ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());

        // A REAL resolver, with a gateway that routes every hop back into it in-process. That is
        // exactly what RoutedInstanceCorrelationGateway does for a same-domain hop, so the
        // recursion under test is the production recursion rather than a stand-in.
        var correlationGateway = Substitute.For<IInstanceCorrelationGateway>();
        var correlationOptions = Options.Create(new Correlation.InstanceCorrelationOptions());
        InstanceCorrelationResolver? resolver = null;
        correlationGateway
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CorrelationBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => resolver!.ResolveAsync(
                ci.ArgAt<string>(0), ci.ArgAt<string>(1),
                ci.ArgAt<CorrelationBatchRequest>(2), ci.ArgAt<CancellationToken>(3)));

        resolver = new InstanceCorrelationResolver(
            _correlationRepository,
            _instanceRepository,
            currentSchema,
            _urlTemplateBuilder,
            _ambient.GetRequiredService<IServiceScopeFactory>(),
            correlationGateway,
            new CorrelationHopLimiter(correlationOptions),
            correlationOptions,
            Substitute.For<ILogger<InstanceCorrelationResolver>>());

        _service = new InstanceQueryAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            componentCacheStore: componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: _correlationRepository,
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
            urlTemplateBuilder: _urlTemplateBuilder,
            instanceCorrelationResolver: resolver,
            correlationOptions: correlationOptions,
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new BBT.Workflow.Instances.InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            callerRoleResolver: new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()),
            currentUser: Substitute.For<ICurrentUser>(),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            descentLimiter: new HumanTaskDescentLimiter(Options.Create(new HumanTaskFunctionOptions())),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The shape the whole feature exists for: root → child → grandchild, nested top-down. If the walk
    /// ever inverted (child → parent), the grandchild would surface at the root and this fails.
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_WalksParentToChild_Recursively()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        StubInstance(rootId, RootFlow);
        StubInstance(childId, ChildFlow);
        StubInstance(grandchildId, GrandchildFlow);

        StubChildren(rootId, Correlation(rootId, childId, ChildFlow, "root-waiting", SubFlowType.SubFlow.Code));
        StubChildren(childId, Correlation(childId, grandchildId, GrandchildFlow, "child-waiting", SubFlowType.SubFlow.Code));
        StubChildren(grandchildId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var root = result.Value!.Root;

        // The requested instance is the root, and it is not itself a correlated child of anything.
        root.Id.ShouldBe(rootId);
        root.SubFlowType.ShouldBeNull();
        root.ParentState.ShouldBeNull();

        // parent -> child, one level down…
        var child = root.Children.ShouldHaveSingleItem();
        child.Id.ShouldBe(childId);
        child.Flow.ShouldBe(ChildFlow);
        child.ParentState.ShouldBe("root-waiting");

        // …and recursively, the grandchild hangs off the child — not off the root.
        var grandchild = child.Children.ShouldHaveSingleItem();
        grandchild.Id.ShouldBe(grandchildId);
        grandchild.Flow.ShouldBe(GrandchildFlow);
        grandchild.Children.ShouldBeEmpty();

        root.Children.ShouldNotContain(n => n.Id == grandchildId);
    }

    /// <summary>
    /// A leaf (or any instance with no correlated children) answers with itself and an empty child list,
    /// never with its ancestors — the read is strictly downward.
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_LeafInstance_ReturnsRootOnly_NoAncestors()
    {
        var leafId = Guid.NewGuid();
        StubInstance(leafId, ChildFlow);
        StubChildren(leafId);

        var result = await _service.GetInstanceCorrelationAsync(Input(leafId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Root.Id.ShouldBe(leafId);
        result.Value!.Root.Children.ShouldBeEmpty();
    }

    /// <summary>
    /// Both correlation kinds are surfaced and told apart: a blocking SubFlow (S) and a fire-and-forget
    /// SubProcess (P) both appear as children, each carrying its own type.
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_CarriesBothSubFlowAndSubProcessChildren()
    {
        var rootId = Guid.NewGuid();
        var blockingId = Guid.NewGuid();
        var fireAndForgetId = Guid.NewGuid();

        StubInstance(rootId, RootFlow);
        StubInstance(blockingId, ChildFlow);
        StubInstance(fireAndForgetId, ChildFlow);

        StubChildren(rootId,
            Correlation(rootId, blockingId, ChildFlow, "s1", SubFlowType.SubFlow.Code),
            Correlation(rootId, fireAndForgetId, ChildFlow, "s1", SubFlowType.SubProcess.Code));
        StubChildren(blockingId);
        StubChildren(fireAndForgetId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        var children = result.Value!.Root.Children;
        children.Count.ShouldBe(2);
        children.Single(c => c.Id == blockingId).SubFlowType.ShouldBe(SubFlowType.SubFlow);
        children.Single(c => c.Id == fireAndForgetId).SubFlowType.ShouldBe(SubFlowType.SubProcess);
    }

    /// <summary>
    /// Completed correlations stay in the tree — the read is the full history of what this instance
    /// spawned, not only what is still running (the repository read is deliberately unfiltered).
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_IncludesCompletedCorrelations()
    {
        var rootId = Guid.NewGuid();
        var doneChildId = Guid.NewGuid();

        StubInstance(rootId, RootFlow);
        StubInstance(doneChildId, ChildFlow);

        var completed = Correlation(rootId, doneChildId, ChildFlow, "s1", SubFlowType.SubFlow.Code);
        completed.Completed();
        StubChildren(rootId, completed);
        StubChildren(doneChildId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        var child = result.Value!.Root.Children.ShouldHaveSingleItem();
        child.Id.ShouldBe(doneChildId);
        child.IsCompleted.ShouldBeTrue();
        child.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetInstanceCorrelationAsync_WhenInstanceNotFound_ReturnsFailure()
    {
        var missingId = Guid.NewGuid();
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(missingId.ToString(), Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetInstanceCorrelationAsync(Input(missingId), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// The node carries the link-scoped members the state function already returns for the same
    /// <see cref="InstanceCorrelation"/> row, so the two read surfaces agree: the correlation's own id
    /// (the handle for addressing the LINK), when the child was spawned, how the link ended, when the
    /// child's state last moved, and a navigable href. All four link members are null on the root,
    /// which is nobody's correlated child.
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_CarriesTheLinkScopedMembers()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubInstance(childId, ChildFlow);

        var correlation = Correlation(rootId, childId, ChildFlow, "s1", SubFlowType.SubFlow.Code);
        StubChildren(rootId, correlation);
        StubChildren(childId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        var root = result.Value!.Root;
        // The root is not a correlated child, so every link-scoped member is absent.
        root.CorrelationId.ShouldBeNull();
        root.CreatedAt.ShouldBeNull();
        root.TerminalOutcome.ShouldBeNull();
        root.StateChangedAt.ShouldBeNull();

        var child = root.Children.ShouldHaveSingleItem();
        child.CorrelationId.ShouldBe(correlation.Id);   // addresses the LINK, not the instance
        child.CorrelationId.ShouldNotBe(child.Id);      // …and is distinct from the instance id
    }

    /// <summary>
    /// <c>isCompleted</c> only says WHETHER the link ended; <c>terminalOutcome</c> says HOW. A graph
    /// needs the difference between a child that finished and one that faulted.
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_TerminalOutcome_DistinguishesHowTheLinkEnded()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubInstance(childId, ChildFlow);

        var completed = Correlation(rootId, childId, ChildFlow, "s1", SubFlowType.SubFlow.Code);
        completed.Completed();
        StubChildren(rootId, completed);
        StubChildren(childId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        var child = result.Value!.Root.Children.ShouldHaveSingleItem();
        child.IsCompleted.ShouldBeTrue();
        child.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Completed);
    }

    /// <summary>
    /// <c>currentState</c> keeps its existing meaning (the correlation's tracked state, which reports
    /// the deepest active descendant), while <c>ownState</c> reports where the node itself actually is
    /// — so a tree/graph can place each node correctly. Here the correlation has bubbled the
    /// grandchild's state up onto the child; the child's own state must still be its own.
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_OwnState_IsTheNodesOwnStateNotTheDescendants()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        // The child's OWN row sits at child-subflow-state…
        StubInstance(childId, ChildFlow, ownState: "child-subflow-state");

        // …while the correlation has bubbled the grandchild's state up onto it.
        var correlation = Correlation(rootId, childId, ChildFlow, "root-waiting", SubFlowType.SubFlow.Code);
        correlation.UpdateSubFlowState("grandchild-initial", DateTime.UtcNow);
        StubChildren(rootId, correlation);
        StubChildren(childId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        var child = result.Value!.Root.Children.ShouldHaveSingleItem();
        child.CurrentState.ShouldBe("grandchild-initial");     // unchanged, bubbled-up semantics
        child.OwnState.ShouldBe("child-subflow-state");        // pinned exactly — null would now fail
        child.StateChangedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// Every node carries a navigable link to its OWN instance resource — built from that node's own
    /// domain/flow/id, not the parent's. A swapped argument would surface here.
    /// </summary>
    [Fact]
    public async Task GetInstanceCorrelationAsync_HrefAddressesEachNodesOwnInstance()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubInstance(childId, ChildFlow);
        StubChildren(rootId, Correlation(rootId, childId, ChildFlow, "s1", SubFlowType.SubFlow.Code));
        StubChildren(childId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        var root = result.Value!.Root;
        root.Href.ShouldBe($"/{TestDomain}/workflows/{RootFlow}/instances/{rootId}");

        // The child's href uses the CHILD's flow and id — not the root's.
        var child = root.Children.ShouldHaveSingleItem();
        child.Href.ShouldBe($"/{TestDomain}/workflows/{ChildFlow}/instances/{childId}");
    }

    /// <summary>
    /// The outcome a UI actually branches on. <c>isCompleted</c> is true for all three, so a client
    /// colouring on it alone would paint a faulted or cancelled child as healthy.
    /// </summary>
    [Theory]
    [InlineData(SubItemTerminalOutcome.Faulted)]
    [InlineData(SubItemTerminalOutcome.Canceled)]
    public async Task GetInstanceCorrelationAsync_SurfacesNonSuccessTerminalOutcomes(
        SubItemTerminalOutcome outcome)
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubInstance(childId, ChildFlow);

        var correlation = Correlation(rootId, childId, ChildFlow, "s1", SubFlowType.SubFlow.Code);
        correlation.ApplyTerminalOutcome(outcome, DateTime.UtcNow);
        StubChildren(rootId, correlation);
        StubChildren(childId);

        var result = await _service.GetInstanceCorrelationAsync(Input(rootId), CancellationToken.None);

        var child = result.Value!.Root.Children.ShouldHaveSingleItem();
        child.TerminalOutcome.ShouldBe(outcome);
        child.IsCompleted.ShouldBeTrue();   // ended — but NOT necessarily successfully
    }

    /// <summary>
    /// The hard-break rename (AB-20): the wire key is <c>instance-correlation</c>, matching the kebab
    /// style of the other system-function keys. The old <c>hierarchy</c> spelling is gone, no alias.
    /// </summary>
    [Fact]
    public void FunctionKey_IsTheKebabCaseInstanceCorrelation()
    {
        FunctionTypeConst.InstanceCorrelation.ShouldBe("instance-correlation");
        // The telemetry read-kind is camelCase like every other multi-word kind in that file
        // (incidentActive, humanTasks, transitionMetrics); only the URL key is kebab.
        InstanceReadKinds.InstanceCorrelation.ShouldBe("instanceCorrelation");
        InstanceUrlTemplates.InstanceCorrelationTemplate
            .ShouldEndWith("/functions/instance-correlation");
    }

    private static GetInstanceCorrelationInput Input(Guid instanceId) => new()
    {
        Domain = TestDomain,
        Workflow = RootFlow,
        Instance = instanceId.ToString()
    };

    /// <summary>
    /// Stubs the instance row. <paramref name="ownState"/> is put on the instance itself (via
    /// <see cref="Instance.ChangeState"/>) because <see cref="Instance.Create"/> leaves
    /// <c>CurrentState</c> null — without this the OwnState assertions would pass vacuously on null.
    /// </summary>
    private Instance StubInstance(Guid id, string flow, string ownState = "its-own-state")
    {
        var instance = Instance.Create(id, flow, "1.0.0");
        instance.ChangeState(State.Create(ownState, StateType.Intermediate, StateSubType.None, "Minor"));
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);
        _instances[id] = instance;
        return instance;
    }

    private void StubChildren(Guid parentId, params InstanceCorrelation[] correlations)
    {
        _childrenByParent[parentId] = [.. correlations];
    }

    private static InstanceCorrelation Correlation(
        Guid parentId, Guid childId, string childFlow, string parentState, string subFlowType) =>
        InstanceCorrelation.Create(
            Guid.NewGuid(), parentId, parentState, childId, subFlowType, TestDomain, childFlow, "1.0.0");
}
