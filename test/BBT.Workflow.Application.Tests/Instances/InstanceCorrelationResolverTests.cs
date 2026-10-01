using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Correlation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins what the BATCHED, parallel correlation walk does that the per-node walk could not
/// (vnext-client-sdk-core#58 AB-20): one read per level instead of two per node, one call per
/// cross-domain branch instead of none at all, and an explicit answer when a branch cannot be
/// completed.
/// </summary>
/// <remarks>
/// The behavioural contract of the tree itself — direction, recursion, link-scoped members, hrefs —
/// is pinned by <see cref="InstanceQueryAppServiceCorrelationTreeTests"/> against this same
/// resolver. This file is about the walk's SHAPE and its failure modes.
/// </remarks>
public sealed class InstanceCorrelationResolverTests : IDisposable
{
    private const string LocalDomain = "local-domain";
    private const string RemoteDomain = "partner-domain";
    private const string RootFlow = "root-flow";
    private const string ChildFlow = "child-flow";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceCorrelationRepository _correlationRepository =
        Substitute.For<IInstanceCorrelationRepository>();
    private readonly IInstanceCorrelationGateway _gateway = Substitute.For<IInstanceCorrelationGateway>();
    private readonly Dictionary<Guid, Instance> _instances = [];
    private readonly Dictionary<Guid, List<InstanceCorrelation>> _childrenByParent = [];
    private readonly InstanceCorrelationOptions _options = new();
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    private int _correlationReadCount;
    private int _instanceReadCount;

    public InstanceCorrelationResolverTests()
    {
        var uow = Substitute.For<IUnitOfWorkManager>();
        uow.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));

        var services = new ServiceCollection();
        services.AddSingleton(uow);
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        _correlationRepository
            .GetByParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref _correlationReadCount);
                return ci.ArgAt<IReadOnlyCollection<Guid>>(0)
                    .SelectMany(id => _childrenByParent.TryGetValue(id, out var rows)
                        ? rows
                        : Enumerable.Empty<InstanceCorrelation>())
                    .ToList();
            });

        _instanceRepository
            .GetForCorrelationWalkAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref _instanceReadCount);
                return ci.ArgAt<IReadOnlyCollection<Guid>>(0)
                    .Where(_instances.ContainsKey)
                    .Select(id => _instances[id])
                    .ToList();
            });
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbient;

    // ---- Batching ------------------------------------------------------------------------------

    /// <summary>
    /// The point of the rewrite. Five siblings of one flow used to cost five correlation reads and
    /// five instance reads; they now cost one of each, because the LEVEL is the unit of work.
    /// </summary>
    [Fact]
    public async Task OneLevelOfManySiblings_CostsOneCorrelationReadAndOneInstanceRead()
    {
        var rootId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);

        var children = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var child in children)
        {
            StubInstance(child, ChildFlow);
        }

        StubChildren(rootId, [.. children.Select(c => Correlation(rootId, c, ChildFlow, LocalDomain))]);

        var resolver = BuildResolver(routeLocally: true);
        var result = await resolver.ResolveAsync(LocalDomain, RootFlow, Request(rootId));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Single().Children.Count.ShouldBe(5);

        // Level 0 (the root) + level 1 (all five siblings in ONE hop, same domain+flow) = 2 each.
        // The per-node walk would have been 1 + 5 correlation reads and 1 + 5 instance reads.
        _correlationReadCount.ShouldBe(2);
        _instanceReadCount.ShouldBe(2);
    }

    /// <summary>Two parents whose children share a (domain, flow) are expanded by ONE hop, not two.</summary>
    [Fact]
    public async Task SiblingsOfDifferentParents_SharingOneFlow_AreExpandedByASingleHop()
    {
        Guid parentA = Guid.NewGuid(), parentB = Guid.NewGuid();
        Guid childA = Guid.NewGuid(), childB = Guid.NewGuid();
        foreach (var id in new[] { parentA, parentB }) StubInstance(id, RootFlow);
        foreach (var id in new[] { childA, childB }) StubInstance(id, ChildFlow);

        StubChildren(parentA, Correlation(parentA, childA, ChildFlow, LocalDomain));
        StubChildren(parentB, Correlation(parentB, childB, ChildFlow, LocalDomain));

        var resolver = BuildResolver(routeLocally: true);
        var result = await resolver.ResolveAsync(
            LocalDomain, RootFlow, new CorrelationBatchRequest
            {
                InstanceIds = [parentA, parentB],
                RemainingDepth = _options.MaxDescentDepth
            });

        result.Value!.Count.ShouldBe(2);
        result.Value!.ShouldAllBe(r => r.Children.Count == 1);
        _correlationReadCount.ShouldBe(2); // the two parents in one read, the two children in one more
    }

    /// <summary>
    /// The feature's central claim, proved WITHOUT timing. Each of two hops blocks until the other
    /// has started: if the walk expanded them one after another the first would wait forever and
    /// this test would hang, so completion is itself the evidence that they overlapped.
    /// </summary>
    [Fact]
    public async Task SiblingHopsOfDifferentFlows_RunConcurrently()
    {
        var rootId = Guid.NewGuid();
        var childA = Guid.NewGuid();
        var childB = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubChildren(
            rootId,
            Correlation(rootId, childA, "flow-a", LocalDomain),
            Correlation(rootId, childB, "flow-b", LocalDomain));

        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _gateway
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CorrelationBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var flow = ci.ArgAt<string>(1);
                if (flow == "flow-a")
                {
                    aStarted.TrySetResult();
                    await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    bStarted.TrySetResult();
                    await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                return Result<IReadOnlyList<CorrelationBatchResult>>.Ok([]);
            });

        var resolver = BuildResolver(routeLocally: false);

        // Fails by TIMEOUT if the hops are serialised — which is precisely the regression to catch.
        var walk = resolver.ResolveAsync(LocalDomain, RootFlow, Request(rootId));
        var finished = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.ShouldBe(walk, "the two sibling hops did not overlap — the walk is serialised");
        (await walk).IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// ...and the concurrency is BOUNDED. With a width of one the same mutual barrier can never be
    /// satisfied, so the walk must fall back to running them in sequence rather than deadlocking.
    /// </summary>
    [Fact]
    public async Task TheFanOutIsBounded_ByFanoutParallelism()
    {
        _options.FanoutParallelism = 1;

        var rootId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubChildren(
            rootId,
            Correlation(rootId, Guid.NewGuid(), "flow-a", LocalDomain),
            Correlation(rootId, Guid.NewGuid(), "flow-b", LocalDomain));

        var inFlight = 0;
        var maxObserved = 0;
        _gateway
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CorrelationBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxObserved, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
                return Result<IReadOnlyList<CorrelationBatchResult>>.Ok([]);
            });

        var resolver = BuildResolver(routeLocally: false);
        await resolver.ResolveAsync(LocalDomain, RootFlow, Request(rootId));

        maxObserved.ShouldBe(1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    // ---- Cross-domain --------------------------------------------------------------------------

    /// <summary>
    /// The bug the batched walk exists to fix. A child in another domain used to be queried against
    /// the LOCAL schema regardless, so its row was never found and the node silently degraded to
    /// correlation-row data. It must now be routed to the domain that owns it.
    /// </summary>
    [Fact]
    public async Task ACrossDomainChild_IsRoutedToItsOwningDomain_AndEnriched()
    {
        var rootId = Guid.NewGuid();
        var remoteChildId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubChildren(rootId, Correlation(rootId, remoteChildId, ChildFlow, RemoteDomain));

        string? routedDomain = null;
        _gateway
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CorrelationBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                routedDomain = ci.ArgAt<string>(0);
                return Result<IReadOnlyList<CorrelationBatchResult>>.Ok(
                [
                    new CorrelationBatchResult
                    {
                        InstanceId = remoteChildId,
                        Resolved = true,
                        Key = "remote-key",
                        OwnState = "remote-state",
                        Status = InstanceStatus.Active,
                        Children = []
                    }
                ]);
            });

        var resolver = BuildResolver(routeLocally: false);
        var result = await resolver.ResolveAsync(LocalDomain, RootFlow, Request(rootId));

        routedDomain.ShouldBe(RemoteDomain);

        var node = result.Value!.Single().Children.Single();
        node.Domain.ShouldBe(RemoteDomain);
        // Read in the owning domain, which the old implementation could never do — these were null.
        node.Key.ShouldBe("remote-key");
        node.OwnState.ShouldBe("remote-state");
        node.Resolved.ShouldBeTrue();
    }

    // ---- Partial failure -----------------------------------------------------------------------

    /// <summary>
    /// An unreachable partner truncates its own branch and says so; it does not fail the tree. The
    /// sibling branch must still arrive intact.
    /// </summary>
    [Fact]
    public async Task AFailedHop_MarksOnlyItsOwnBranchUnresolved()
    {
        var rootId = Guid.NewGuid();
        var localChildId = Guid.NewGuid();
        var remoteChildId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubInstance(localChildId, ChildFlow);
        StubChildren(
            rootId,
            Correlation(rootId, localChildId, ChildFlow, LocalDomain),
            Correlation(rootId, remoteChildId, ChildFlow, RemoteDomain));

        var resolver = BuildResolver(routeLocally: true, remoteFails: true);
        var result = await resolver.ResolveAsync(LocalDomain, RootFlow, Request(rootId));

        result.IsSuccess.ShouldBeTrue(); // the call itself succeeds

        var children = result.Value!.Single().Children;
        var local = children.Single(c => c.Domain == LocalDomain);
        var remote = children.Single(c => c.Domain == RemoteDomain);

        local.Resolved.ShouldBeTrue();
        remote.Resolved.ShouldBeFalse();
        remote.UnresolvedReason.ShouldBe("hop-failed");
        // The node itself is still real and addressable — only its descendants are unknown.
        remote.Id.ShouldBe(remoteChildId);
        remote.Href.ShouldNotBeNullOrEmpty();
    }

    /// <summary>A thrown transport fault is absorbed the same way a failed Result is.</summary>
    [Fact]
    public async Task AThrowingHop_IsAbsorbedAsAnUnresolvedBranch()
    {
        var rootId = Guid.NewGuid();
        var remoteChildId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubChildren(rootId, Correlation(rootId, remoteChildId, ChildFlow, RemoteDomain));

        _gateway
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CorrelationBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Result<IReadOnlyList<CorrelationBatchResult>>>>(
                _ => throw new HttpRequestException("partner unreachable"));

        var resolver = BuildResolver(routeLocally: false);
        var result = await resolver.ResolveAsync(LocalDomain, RootFlow, Request(rootId));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Single().Children.Single().UnresolvedReason.ShouldBe("hop-failed");
    }

    // ---- Depth -----------------------------------------------------------------------------------

    /// <summary>
    /// The walk's only protection against a cyclic graph, and the reason a post-hoc correlation
    /// write API would not be able to hang this read path. Reported, never silently rendered as a
    /// childless leaf.
    /// </summary>
    [Fact]
    public async Task AtTheDepthBound_TheNodeIsMarkedRatherThanWalkedFurther()
    {
        // A two-level chain, but only one level of budget.
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubInstance(childId, ChildFlow);
        StubInstance(grandchildId, ChildFlow);
        StubChildren(rootId, Correlation(rootId, childId, ChildFlow, LocalDomain));
        StubChildren(childId, Correlation(childId, grandchildId, ChildFlow, LocalDomain));

        var resolver = BuildResolver(routeLocally: true);
        var result = await resolver.ResolveAsync(
            LocalDomain, RootFlow,
            new CorrelationBatchRequest { InstanceIds = [rootId], RemainingDepth = 1 });

        var child = result.Value!.Single().Children.Single();
        child.Id.ShouldBe(childId);
        child.Resolved.ShouldBeFalse();
        child.UnresolvedReason.ShouldBe("depth-exceeded");
        child.Children.ShouldBeEmpty();
    }

    /// <summary>
    /// A depth-truncated walk must LOG. Found live: the nodes were marked correctly on a six-level
    /// chain bounded at three, and the runtime logged nothing, because the only log sat on the
    /// entry guard — a branch a real walk never reaches, since the expansion stops one level
    /// earlier. A truncated tree that is invisible to operations is the failure mode here.
    /// </summary>
    [Fact]
    public async Task HittingTheDepthBound_IsLogged()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        StubInstance(rootId, RootFlow);
        StubInstance(childId, ChildFlow);
        StubChildren(rootId, Correlation(rootId, childId, ChildFlow, LocalDomain));

        var logger = Substitute.For<ILogger<InstanceCorrelationResolver>>();
        // Source-generated LoggerMessage checks IsEnabled first, and a substitute answers false.
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var resolver = BuildResolver(routeLocally: true, logger: logger);

        await resolver.ResolveAsync(
            LocalDomain, RootFlow,
            new CorrelationBatchRequest { InstanceIds = [rootId], RemainingDepth = 1 });

        // Type-agnostic: the generated LoggerMessage passes its own state struct, so a
        // Log<FormattedLogValues> specification would never match the real call.
        logger.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                        && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .ShouldBe(1);
    }

    /// <summary>
    /// A caller-supplied depth is CLAMPED to the runtime's own bound. The internal batch endpoint
    /// carries no authorization, so an unclamped RemainingDepth would let any caller walk straight
    /// past the walk's only protection against a cyclic graph.
    /// </summary>
    [Fact]
    public async Task ACallerSuppliedDepth_IsClampedToTheConfiguredMaximum()
    {
        _options.MaxDescentDepth = 2;

        // A 4-deep chain; the caller asks for 99 levels.
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) StubInstance(id, ChildFlow);
        for (var i = 0; i < ids.Length - 1; i++)
            StubChildren(ids[i], Correlation(ids[i], ids[i + 1], ChildFlow, LocalDomain));

        var resolver = BuildResolver(routeLocally: true);
        var result = await resolver.ResolveAsync(
            LocalDomain, RootFlow,
            new CorrelationBatchRequest { InstanceIds = [ids[0]], RemainingDepth = 99 });

        // Clamped to 2, so level 2 is marked rather than walked — not 99 levels deep.
        var lvl1 = result.Value!.Single().Children.ShouldHaveSingleItem();
        lvl1.Resolved.ShouldBeTrue();
        var lvl2 = lvl1.Children.ShouldHaveSingleItem();
        lvl2.Resolved.ShouldBeFalse();
        lvl2.UnresolvedReason.ShouldBe("depth-exceeded");
    }

    /// <summary>Depth is checked BEFORE any query — an exhausted budget must not pay for a level.</summary>
    [Fact]
    public async Task AnExhaustedDepthBudget_IssuesNoQueriesAtAll()
    {
        var resolver = BuildResolver(routeLocally: true);

        var result = await resolver.ResolveAsync(
            LocalDomain, RootFlow,
            new CorrelationBatchRequest { InstanceIds = [Guid.NewGuid()], RemainingDepth = 0 });

        result.Value!.Single().UnresolvedReason.ShouldBe("depth-exceeded");
        _correlationReadCount.ShouldBe(0);
        _instanceReadCount.ShouldBe(0);
    }

    // ---- Missing rows ------------------------------------------------------------------------

    /// <summary>
    /// READ COMMITTED lets a row vanish between the parent's correlation read and the child's own.
    /// That is an incomplete answer, not a childless one, and the caller must be able to tell.
    /// </summary>
    [Fact]
    public async Task AMissingInstanceRow_IsReportedRatherThanRenderedAsALeaf()
    {
        var rootId = Guid.NewGuid(); // deliberately NOT stubbed

        var resolver = BuildResolver(routeLocally: true);
        var result = await resolver.ResolveAsync(LocalDomain, RootFlow, Request(rootId));

        var answer = result.Value!.Single();
        answer.Resolved.ShouldBeFalse();
        answer.UnresolvedReason.ShouldBe("instance-missing");
    }

    [Fact]
    public async Task AnEmptyRequest_ShortCircuits()
    {
        var resolver = BuildResolver(routeLocally: true);

        var result = await resolver.ResolveAsync(
            LocalDomain, RootFlow, new CorrelationBatchRequest { InstanceIds = [], RemainingDepth = 5 });

        result.Value!.ShouldBeEmpty();
        _correlationReadCount.ShouldBe(0);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private CorrelationBatchRequest Request(Guid id) =>
        new() { InstanceIds = [id], RemainingDepth = _options.MaxDescentDepth };

    /// <param name="routeLocally">
    /// When true the gateway re-enters the resolver in-process, which is what
    /// <c>RoutedInstanceCorrelationGateway</c> does for a same-domain hop — so the recursion under
    /// test is the production recursion. A remote-domain hop is still answered by the substitute.
    /// </param>
    /// <param name="remoteFails">Makes any hop into <see cref="RemoteDomain"/> answer a failed Result.</param>
    private InstanceCorrelationResolver BuildResolver(
        bool routeLocally, bool remoteFails = false, ILogger<InstanceCorrelationResolver>? logger = null)
    {
        var currentSchema = Substitute.For<ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());

        var urlBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlBuilder
            .BuildInstanceUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => $"/{ci.ArgAt<string>(0)}/workflows/{ci.ArgAt<string>(1)}/instances/{ci.ArgAt<string>(2)}");

        var options = Options.Create(_options);
        InstanceCorrelationResolver? resolver = null;

        if (routeLocally)
        {
            _gateway
                .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CorrelationBatchRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var domain = ci.ArgAt<string>(0);
                    if (remoteFails && domain == RemoteDomain)
                    {
                        return Task.FromResult(Result<IReadOnlyList<CorrelationBatchResult>>.Fail(
                            Error.Transient("remote_network_error", "partner unreachable")));
                    }

                    return resolver!.ResolveAsync(
                        domain, ci.ArgAt<string>(1),
                        ci.ArgAt<CorrelationBatchRequest>(2), ci.ArgAt<CancellationToken>(3));
                });
        }

        resolver = new InstanceCorrelationResolver(
            _correlationRepository,
            _instanceRepository,
            currentSchema,
            urlBuilder,
            _ambient.GetRequiredService<IServiceScopeFactory>(),
            _gateway,
            new CorrelationHopLimiter(options),
            options,
            logger ?? Substitute.For<ILogger<InstanceCorrelationResolver>>());

        return resolver;
    }

    private void StubInstance(Guid id, string flow, string ownState = "its-own-state")
    {
        var instance = Instance.Create(id, flow, "1.0.0");
        instance.ChangeState(State.Create(ownState, StateType.Intermediate, StateSubType.None, "Minor"));
        _instances[id] = instance;
    }

    private void StubChildren(Guid parentId, params InstanceCorrelation[] correlations) =>
        _childrenByParent[parentId] = [.. correlations];

    private static InstanceCorrelation Correlation(
        Guid parentId, Guid childId, string childFlow, string childDomain) =>
        InstanceCorrelation.Create(
            Guid.NewGuid(), parentId, "parent-state", childId,
            SubFlowType.SubFlow.Code, childDomain, childFlow, "1.0.0");
}
