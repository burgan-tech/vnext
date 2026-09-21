using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The descent, walked end to end over chains that span several levels and several domains.
/// <para>
/// Each domain is a separate "runtime" here — its own repository, its own component cache, its own
/// resolver — wired together by a gateway that routes on the target domain, exactly as
/// <c>RoutedHumanTaskLeafGateway</c> does. A hop that crosses a domain boundary is put through a
/// real JSON round-trip on the way out and on the way back, so the wire contract is under test too:
/// if <see cref="HumanTaskLeafRequest"/> or <see cref="HumanTaskLeafResult"/> could not survive
/// serialization, cross-domain descent would fail at runtime with nothing to catch it at compile
/// time.
/// </para>
/// </summary>
public class HumanTaskLeafDescentScenarioTests
{
    private const string Core = "core";
    private const string Partner = "partner";
    private const string Credit = "credit";
    private const string HumanState = "awaiting-human";
    private const string SubFlowState = "awaiting-sub";

    /// <summary>One link of a chain: the flow, the domain that owns it, and its human-task text.</summary>
    private sealed record Link(string Flow, string Domain, string? Title = null);

    private sealed class HopLog
    {
        public List<string> Local { get; } = [];
        public List<string> Remote { get; } = [];
    }

    /// <summary>A single domain's runtime: its own store of instances and definitions.</summary>
    private sealed class DomainRuntime(string domain)
    {
        public string Domain { get; } = domain;
        public Dictionary<Guid, Instance> Instances { get; } = [];
        public Dictionary<string, Definitions.Workflow> Flows { get; } = [];
        public HumanTaskLeafResolver Resolver { get; set; } = null!;
    }

    /// <summary>
    /// Routes a hop to the runtime that owns the target domain. Cross-domain hops go through the
    /// same serialization the real remote gateway performs.
    /// </summary>
    private sealed class DomainRoutingGateway(
        string selfDomain,
        Func<string, DomainRuntime> lookup,
        HopLog log) : IHumanTaskLeafGateway
    {
        public async Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
            string domain, string flow, HumanTaskLeafRequest request, CancellationToken cancellationToken = default)
        {
            var target = lookup(domain);

            if (string.Equals(domain, selfDomain, StringComparison.Ordinal))
            {
                log.Local.Add($"{domain}/{flow}");
                return await target.Resolver.ResolveAsync(domain, flow, request, cancellationToken);
            }

            log.Remote.Add($"{domain}/{flow}");

            var onTheWire = JsonSerializer.Deserialize<HumanTaskLeafRequest>(
                JsonSerializer.Serialize(request, JsonSerializerConstants.JsonOptions),
                JsonSerializerConstants.JsonOptions);
            onTheWire.ShouldNotBeNull();

            var answer = await target.Resolver.ResolveAsync(domain, flow, onTheWire!, cancellationToken);
            answer.IsSuccess.ShouldBeTrue();

            var backOverTheWire = JsonSerializer.Deserialize<List<HumanTaskLeafResult>>(
                JsonSerializer.Serialize(answer.Value, JsonSerializerConstants.JsonOptions),
                JsonSerializerConstants.JsonOptions);
            backOverTheWire.ShouldNotBeNull();

            return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok(backOverTheWire!);
        }
    }

    /// <summary>
    /// Builds one runtime per distinct domain in the chain, creates the chained instances in the
    /// runtime that owns each link, and returns the root's id plus the hop log.
    /// </summary>
    private static (DomainRuntime RootRuntime, Guid RootId, HopLog Hops) BuildChain(params Link[] chain)
    {
        var runtimes = chain
            .Select(l => l.Domain)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(d => d, d => new DomainRuntime(d), StringComparer.Ordinal);

        var hops = new HopLog();

        // Definitions. Every link's human state declares queryRoles, which is what the list is
        // decided by; the transition is kept for shape but no longer influences the answer.
        foreach (var link in chain)
        {
            var isLeaf = ReferenceEquals(link, chain[^1]);
            var stateKey = isLeaf ? HumanState : SubFlowState;

            runtimes[link.Domain].Flows[link.Flow] = BuildFlow(
                stateKey,
                isLeaf ? "intermediate" : "subflow",
                isLeaf ? "human" : "none");
        }

        // Instances, built leaf-first so each parent can point at its child.
        Instance? child = null;
        Link? childLink = null;
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            var link = chain[i];
            var isLeaf = i == chain.Length - 1;
            var stateKey = isLeaf ? HumanState : SubFlowState;

            var instance = Instance.Create(Guid.NewGuid(), link.Flow, "1.0.0", "CASE-1");
            instance.ChangeState(StateFactory.CreateDefault(
                stateKey,
                isLeaf ? StateType.Intermediate : StateType.SubFlow,
                isLeaf ? StateSubType.Human : StateSubType.None));

            if (link.Title is not null)
            {
                instance.SeedData(
                    Guid.NewGuid(),
                    new JsonData(JsonSerializer.SerializeToElement(
                        new { humanTask = new { title = link.Title, description = $"{link.Title} desc" } })));
            }

            if (child is not null && childLink is not null)
            {
                instance.AddCorrelation(InstanceCorrelation.Create(
                    Guid.NewGuid(),
                    instance.Id,
                    stateKey,
                    child.Id,
                    SubFlowType.SubFlow.Code,
                    childLink.Domain,
                    childLink.Flow,
                    "1.0.0"));
            }

            runtimes[link.Domain].Instances[instance.Id] = instance;
            child = instance;
            childLink = link;
        }

        foreach (var runtime in runtimes.Values)
            runtime.Resolver = BuildResolver(runtime, d => runtimes[d], hops);

        return (runtimes[chain[0].Domain], child!.Id, hops);
    }

    private static HumanTaskLeafResolver BuildResolver(
        DomainRuntime runtime, Func<string, DomainRuntime> lookup, HopLog hops)
    {
        var componentCache = Substitute.For<IComponentCacheStore>();
        componentCache
            .GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var target = lookup(call.ArgAt<string>(0));
                return target.Flows.TryGetValue(call.ArgAt<string>(1), out var wf)
                    ? Result<Definitions.Workflow>.Ok(wf)
                    : Result<Definitions.Workflow>.Fail(Error.NotFound("flow", call.ArgAt<string>(1)));
            });

        var repository = Substitute.For<IInstanceRepository>();
        repository
            .GetForHumanTaskDescentAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                return ids.Where(runtime.Instances.ContainsKey).Select(id => runtime.Instances[id]).ToList();
            });

        var currentSchema = Substitute.For<ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(_ => Substitute.For<IDisposable>());

        var evaluator = Substitute.For<IRoleGrantEvaluator>();
        evaluator.IsAnyRoleAllowed(
                Arg.Any<string[]>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(), Arg.Any<Transition?>())
            .Returns(true);

        var authorizationManager = Substitute.For<ITransitionAuthorizationManager>();
        authorizationManager.CreateEvaluatorAsync(
                Arg.Any<Instance>(), Arg.Any<Definitions.Workflow>(), Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<IEnumerable<RoleGrant>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(evaluator));

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.IsDomainMatch(Arg.Any<string>())
            .Returns(call => string.Equals(call.ArgAt<string>(0), runtime.Domain, StringComparison.Ordinal));

        return new HumanTaskLeafResolver(
            componentCache,
            repository,
            currentSchema,
            authorizationManager,
            new DomainRoutingGateway(runtime.Domain, lookup, hops),
            runtimeInfo,
            NullLogger<HumanTaskLeafResolver>.Instance);
    }

    private static async Task<HumanTaskLeafResult> DescendAsync(DomainRuntime root, Guid rootId, string rootFlow)
    {
        var result = await root.Resolver.ResolveAsync(
            root.Domain,
            rootFlow,
            new HumanTaskLeafRequest
            {
                InstanceIds = [rootId],
                CallerRoles = ["clerk"],
                RemainingDepth = 10
            });

        result.IsSuccess.ShouldBeTrue();
        return result.Value!.ShouldHaveSingleItem();
    }

    /// <summary>Senaryo 1 — A → B → C, all in core. The leaf is C.</summary>
    [Fact]
    public async Task Scenario1_ThreeLevelsInOneDomain_ResolvesToTheThirdLevel()
    {
        var (root, rootId, hops) = BuildChain(
            new Link("A", Core),
            new Link("B", Core),
            new Link("C", Core, Title: "C step"));

        var answer = await DescendAsync(root, rootId, "A");

        answer.InstanceId.ShouldBe(rootId, "the answer is addressed by the ROOT, never the leaf");
        answer.Resolved.ShouldBeTrue();
        answer.Authorized.ShouldBeTrue();
        answer.LeafDomain.ShouldBe(Core);
        answer.LeafFlow.ShouldBe("C");
        answer.Title.ShouldBe("C step");
        answer.Description.ShouldBe("C step desc");

        hops.Remote.ShouldBeEmpty();
        hops.Local.ShouldBe(["core/B", "core/C"]);
    }

    /// <summary>Senaryo 2 — A → B → C in core, then D in partner. The leaf is D.</summary>
    [Fact]
    public async Task Scenario2_CrossingIntoPartnerAtTheFourthLevel_ResolvesToD()
    {
        var (root, rootId, hops) = BuildChain(
            new Link("A", Core),
            new Link("B", Core),
            new Link("C", Core),
            new Link("D", Partner, Title: "D step"));

        var answer = await DescendAsync(root, rootId, "A");

        answer.InstanceId.ShouldBe(rootId);
        answer.Resolved.ShouldBeTrue();
        answer.Authorized.ShouldBeTrue();
        answer.LeafDomain.ShouldBe(Partner);
        answer.LeafFlow.ShouldBe("D");
        answer.Title.ShouldBe("D step");

        // One boundary crossed, so exactly one remote call — not one per level.
        hops.Remote.ShouldBe(["partner/D"]);
        hops.Local.ShouldBe(["core/B", "core/C"]);
    }

    /// <summary>
    /// Senaryo 3 — A → B → C in core, D in partner, E → F in credit. The leaf is F, two boundaries
    /// away from the root, and the second crossing is made by the PARTNER runtime rather than by
    /// the one the client called.
    /// </summary>
    [Fact]
    public async Task Scenario3_TwoBoundariesAndSixLevels_ResolvesToF()
    {
        var (root, rootId, hops) = BuildChain(
            new Link("A", Core),
            new Link("B", Core),
            new Link("C", Core),
            new Link("D", Partner),
            new Link("E", Credit),
            new Link("F", Credit, Title: "F step"));

        var answer = await DescendAsync(root, rootId, "A");

        answer.InstanceId.ShouldBe(rootId, "six levels down and two domains over, the row is still the root's");
        answer.Resolved.ShouldBeTrue();
        answer.Authorized.ShouldBeTrue();
        answer.LeafDomain.ShouldBe(Credit);
        answer.LeafFlow.ShouldBe("F");
        answer.Title.ShouldBe("F step");

        // Two boundaries, two remote calls. credit/F is LOCAL because the credit runtime made it.
        hops.Remote.ShouldBe(["partner/D", "credit/E"]);
        hops.Local.ShouldBe(["core/B", "core/C", "credit/F"]);
    }

    /// <summary>
    /// The depth bound is spent by the whole chain, not per domain — a remote hop decrements it and
    /// carries the remainder across the wire, so a deep chain cannot buy itself more budget by
    /// crossing a boundary.
    /// </summary>
    [Fact]
    public async Task TheDepthBudgetIsSharedAcrossDomains()
    {
        var (root, rootId, _) = BuildChain(
            new Link("A", Core),
            new Link("B", Core),
            new Link("C", Core),
            new Link("D", Partner),
            new Link("E", Credit),
            new Link("F", Credit, Title: "F step"));

        var result = await root.Resolver.ResolveAsync(
            root.Domain,
            "A",
            new HumanTaskLeafRequest
            {
                InstanceIds = [rootId],
                CallerRoles = ["clerk"],
                RemainingDepth = 3
            });

        var answer = result.Value!.ShouldHaveSingleItem();
        answer.InstanceId.ShouldBe(rootId);
        answer.Resolved.ShouldBeFalse();
        answer.DropReason.ShouldBe("depth-exceeded");
    }

    /// <summary>
    /// A root that is already the leaf needs no hop at all — the common case, and the one the old
    /// one-level descent got right.
    /// </summary>
    [Fact]
    public async Task ARootWithNoSubflowIsItsOwnLeaf()
    {
        var (root, rootId, hops) = BuildChain(new Link("A", Core, Title: "A step"));

        var answer = await DescendAsync(root, rootId, "A");

        answer.LeafFlow.ShouldBe("A");
        answer.Title.ShouldBe("A step");
        hops.Local.ShouldBeEmpty();
        hops.Remote.ShouldBeEmpty();
    }

    /// <summary>
    /// A flow built from JSON so its state can declare <c>queryRoles</c> — the gate the human-task
    /// list is decided by, and one with no public setter on the aggregate.
    /// </summary>
    private static Definitions.Workflow BuildFlow(string stateKey, string stateType, string subType) =>
        System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>($$"""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [
                {
                  "key": "{{stateKey}}",
                  "stateType": "{{stateType}}",
                  "subType": "{{subType}}",
                  "labels": [],
                  "queryRoles": [{"role":"ht-approver","grant":"allow"}],
                  "transitions": [
                    { "key": "act", "target": "done", "triggerType": "manual", "labels": [] }
                  ]
                }
              ],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": []
            }
            """, FlowJsonOptions)!;

    private static readonly System.Text.Json.JsonSerializerOptions FlowJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
