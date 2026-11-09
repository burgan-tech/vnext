using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization.Legacy;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Differential test: the production <see cref="RoleGrantEvaluator"/> — which builds the transition-independent
/// authorization context once, resolves <c>Instance.Data.*</c> against the instance's own data element and
/// <c>Transition.*</c> against a small per-key element — must reach exactly the decisions of
/// <see cref="LegacyRoleGrantEvaluator"/>, the frozen copy that materialised one combined document (with a full
/// data copy) per transition key.
/// <para>
/// The matrix crosses every grant form (static, predefined, <c>$user.</c> / <c>$userBehalfOf.</c> / <c>$role.</c>
/// over every context root, case variants, wildcards, missing and degenerate paths) with allow / deny /
/// <c>allOf</c> / <c>anyOf</c> shapes, several callers, and N=10 transitions plus none — evaluated through ONE
/// evaluator per caller, as a state build does.
/// </para>
/// </summary>
public sealed class RoleGrantEvaluatorContextEquivalenceTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private const string Data = """
        {
          "customer": { "ownerUserId": "alice", "behalfOfId": "c-acme" },
          "Owner": "bob",
          "owner": "alice",
          "assignedUsers": [ { "userId": "carol" }, { "userId": "alice" }, { "other": 1 } ],
          "tags": [ "approver", "T3", 42 ],
          "amount": 42,
          "ratio": 1.50,
          "flag": true,
          "off": false,
          "nothing": null,
          "escaped": "a\"bé",
          "nested": [ { "list": [ { "name": "alice" }, { "name": "dave" } ] }, { "list": [] } ],
          "Data": { "inner": "alice" },
          "Transition": { "Key": "alice" }
        }
        """;

    private static readonly string[] ContextPaths =
    [
        "Instance.Data.customer.ownerUserId",
        "instance.data.CUSTOMER.OWNERUSERID",
        "Instance.Data.customer.behalfOfId",
        "Instance.Data.assignedUsers[*].userId",
        "Instance.Data.assignedUsers[*]",
        "Instance.Data.tags[*]",
        "Instance.Data.tags",
        "Instance.Data.amount",
        "Instance.Data.ratio",
        "Instance.Data.flag",
        "Instance.Data.off",
        "Instance.Data.nothing",
        "Instance.Data.escaped",
        "Instance.Data.Owner",
        "Instance.Data.owner",
        "Instance.Data.nested[*].list[*].name",
        "Instance.Data.Data.inner",
        "Instance.Data.Transition.Key",
        "Instance.Data",
        "Instance.Data[*]",
        "Instance.Data.missing.path",
        "Instance..Data..customer.ownerUserId",
        " Instance . Data . customer . ownerUserId ",
        "Instance",
        "Instance.Id",
        "Instance.Key",
        "Instance.Flow",
        "Instance.FlowVersion",
        "Instance.Status",
        "Instance.EffectiveStatus",
        "Instance.Type",
        "Instance.CurrentState",
        "Instance.EffectiveState",
        "Instance.EffectiveStateType",
        "Instance.CreatedBy",
        "instance.createdby",
        "Instance.CreatedByBehalfOf",
        "Instance.ModifiedBy",
        "Instance.[*]",
        "Instance.Missing",
        "Transition",
        "Transition.Key",
        "transition.KEY",
        "Transition.From",
        "Transition.Target",
        "Transition.TriggerType",
        "Transition[*].Key",
        "Transition.Key.extra",
        "Transition.Missing",
        "Workflow",
        "Workflow.Key",
        "Workflow.Flow",
        "Body",
        "Body.x",
        "Headers.x-user",
        "headers.X-USER",
        "Headers.x-role",
        "QueryParameters.q",
        "RouteValues.r",
        "Missing.Root",
        "[*]",
        "...",
    ];

    private static readonly string[] Qualifiers = ["$user.$.context.", "$userBehalfOf.$.context.", "$role.$.context."];

    private static readonly string[] PlainLeaves =
    [
        "approver", "APPROVER", "$InstanceStarter", "$PreviousUser", "$InstanceBehalfOfStarter", "$PreviousBehalfOfUser",
        "$user.$.notcontext.x", "$role.$.context.",
    ];

    private static readonly (string? Actor, string? Subject, string[]? Roles)[] Callers =
    [
        ("alice", "c-acme", null),
        ("alice", "c-acme", []),
        ("bob", "x", ["approver"]),
        ("u-ali", "c-acme", ["blocked", "approver"]),
        ("t3", "t3", ["T3", "state-3"]),
        ("Manual", "42", ["Manual", "42"]),
        ("hdr-alice", "q-val", ["hdr-role", "true", "1.50"]),
        ("carol", "dave", ["alice", "a\"bé"]),
        (null, null, ["wf-key", "flow", "Active (A)"]),
        ("u-prev", "c-prev", ["S", "R"]),
    ];

    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public RoleGrantEvaluatorContextEquivalenceTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>());
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Decisions_match_the_per_transition_document_evaluator(bool withData, bool withRequestContext)
    {
        var instance = NewInstance(withData);
        var workflow = Workflow();
        var request = withRequestContext
            ? new AuthorizationRequestContext(
                new Dictionary<string, string?> { ["x-user"] = "hdr-alice", ["x-role"] = "hdr-role", ["empty"] = null },
                new Dictionary<string, string?> { ["q"] = "q-val" },
                new Dictionary<string, string?> { ["r"] = "alice" })
            : null;
        var previous = PreviousTransition(instance.Id);
        var transitions = Transitions();
        var grantSets = GrantSets();

        var evaluated = 0;
        var allowed = 0;
        var mismatches = new List<string>();

        foreach (var (actor, subject, roles) in Callers)
        {
            // One evaluator per caller across every grant set and transition — the batch shape a state build uses.
            var current = new RoleGrantEvaluator(instance, workflow, request, previous, actor, subject);
            var legacy = new LegacyRoleGrantEvaluator(instance, workflow, request, previous, actor, subject);

            foreach (var transition in transitions)
            foreach (var (label, grants) in grantSets)
            {
                var expected = legacy.IsAnyRoleAllowed(roles, grants, transition);
                var actual = current.IsAnyRoleAllowed(roles, grants, transition);
                evaluated++;
                if (expected) allowed++;
                if (expected != actual)
                    mismatches.Add($"{label} | transition={transition?.Key ?? "<none>"} | actor={actor} subject={subject} roles=[{string.Join(",", roles ?? [])}] legacy={expected} current={actual}");
            }
        }

        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches.Take(20)));
        // The matrix must actually discriminate, not compare two constant answers.
        allowed.ShouldBeGreaterThan(evaluated / 20);
        (evaluated - allowed).ShouldBeGreaterThan(evaluated / 20);
    }

    [Fact]
    public void Transition_paths_resolve_per_transition_through_one_evaluator()
    {
        var instance = NewInstance(withData: true);
        var transitions = Transitions().OfType<Transition>().ToList();
        transitions.Count.ShouldBe(10);
        var grants = Grants("""[{"role":"$role.$.context.Transition.Target","grant":"allow"}]""");
        var evaluator = new RoleGrantEvaluator(instance, Workflow(), null, null, "alice", "c-acme");

        foreach (var transition in transitions)
        {
            foreach (var other in transitions)
            {
                evaluator.IsAnyRoleAllowed([other.Target], grants, transition)
                    .ShouldBe(other.Target == transition.Target, $"{transition.Key} vs {other.Target}");
            }
        }
    }

    [Fact]
    public void Data_paths_follow_the_instance_data_and_a_missing_latest_data_matches_nothing()
    {
        var grants = Grants("""[{"role":"$user.$.context.Instance.Data.customer.ownerUserId","grant":"allow"}]""");

        new RoleGrantEvaluator(NewInstance(withData: true), null, null, null, "alice", null)
            .IsAnyRoleAllowed(null, grants, Transitions()[0]).ShouldBeTrue();
        new RoleGrantEvaluator(NewInstance(withData: false), null, null, null, "alice", null)
            .IsAnyRoleAllowed(null, grants, Transitions()[0]).ShouldBeFalse();
    }

    private static List<(string Label, List<RoleGrant> Grants)> GrantSets()
    {
        var leaves = new List<string>(PlainLeaves);
        foreach (var qualifier in Qualifiers)
            leaves.AddRange(ContextPaths.Select(path => qualifier + path));

        var sets = new List<(string, List<RoleGrant>)>();
        foreach (var leaf in leaves)
        {
            var l = JsonSerializer.Serialize(leaf);
            sets.Add(($"allow {leaf}", Grants($$"""[{"role":{{l}},"grant":"allow"}]""")));
            sets.Add(($"deny {leaf}", Grants($$"""[{"role":{{l}},"grant":"deny"}]""")));
            sets.Add(($"allOf[{leaf}, approver]", Grants($$"""[{"allOf":[{"role":{{l}}},{"role":"approver"}],"grant":"allow"}]""")));
            sets.Add(($"anyOf[{leaf}, $InstanceStarter]", Grants($$"""[{"anyOf":[{"role":{{l}}},{"role":"$InstanceStarter"}],"grant":"allow"}]""")));
            sets.Add(($"approver - allOf[{leaf}, $InstanceStarter]", Grants($$"""
                [{"role":"approver","grant":"allow"},{"allOf":[{"role":{{l}}},{"role":"$InstanceStarter"}],"grant":"deny"}]
                """)));
            sets.Add(($"anyOf[{leaf}] - deny {leaf}", Grants($$"""
                [{"anyOf":[{"role":{{l}}},{"role":"$role.$.context.Transition.Key"}],"grant":"allow"},{"role":{{l}},"grant":"deny"}]
                """)));
        }

        return sets;
    }

    private static List<RoleGrant> Grants(string json) =>
        JsonSerializer.Deserialize<List<RoleGrant>>(json, JsonOptions)!;

    private static Instance NewInstance(bool withData)
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedBy = "u-ali";
        instance.CreatedByBehalfOf = "c-acme";
        instance.ModifiedBy = "alice";
        if (withData)
            instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom(Data), VersionStrategy.None);
        return instance;
    }

    private static InstanceTransition PreviousTransition(Guid instanceId)
    {
        var transition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "t0", "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"), JsonData.CreateFrom("{}"));
        transition.CreatedBy = "u-prev";
        transition.CreatedByBehalfOf = "c-prev";
        return transition;
    }

    private static WorkflowDefinition Workflow() =>
        JsonSerializer.Deserialize<WorkflowDefinition>("""
            {
              "key": "wf-key",
              "flow": "flow",
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": []
            }
            """, JsonOptions)!;

    /// <summary>N=10 transitions — distinct keys, sources, targets and trigger types — plus none.</summary>
    private static List<Transition?> Transitions()
    {
        var triggers = new[] { "Manual", "Automatic", "Scheduled", "Event" };
        var list = new List<Transition?>();
        for (var i = 0; i < 10; i++)
        {
            var from = i % 3 == 0 ? "null" : $"\"state-{i - 1}\"";
            var json = $$"""
                {
                  "key": "t{{i}}",
                  "from": {{from}},
                  "target": "state-{{i}}",
                  "triggerType": "{{triggers[i % triggers.Length]}}",
                  "versionStrategy": "None",
                  "labels": [],
                  "onExecutionTasks": [],
                  "roles": []
                }
                """;
            list.Add(JsonSerializer.Deserialize<Transition>(json, JsonOptions)!);
        }

        list.Add(null);
        return list;
    }
}
