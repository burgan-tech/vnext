using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// <see cref="TransitionAuthorizationManager.FilterAuthorizedTransitionKeysAsync"/> reads a child's stamped
/// parent override map ONCE per call and looks every key up in it. The authorized key set must be exactly
/// what the per-key surface (<see cref="ITransitionAuthorizationManager.IsTransitionAllowedInStateAsync"/>,
/// which still reads the stamp per key) answers — for a stamped child, a malformed stamp and no stamp.
/// </summary>
public sealed class TransitionAuthorizationManagerStampedOverrideBatchTests
{
    private const string Review = "review";

    private static readonly string[] Keys = ["approve", "reject", "note", "escalate", "assign", "shared-x"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// approve: narrowed to supervisor; reject: replaced by a predefined grant; note: views only (no roles → the
    /// child's own grants apply); escalate: an empty roles array (→ own grants); assign / shared-x: not stamped.
    /// </summary>
    private const string Stamp = """
        {
          "approve": { "roles": [ { "role": "supervisor", "grant": "allow" } ] },
          "reject":  { "roles": [ { "role": "$InstanceStarter", "grant": "allow" },
                                  { "role": "blocked", "grant": "deny" } ] },
          "note":    { "views": { "v": { "key": "corp-v", "domain": "d", "flow": "sys-views", "version": "1.0.0" } } },
          "escalate":{ "roles": [] }
        }
        """;

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly TransitionAuthorizationManager _sut;

    public TransitionAuthorizationManagerStampedOverrideBatchTests()
    {
        var repo = Substitute.For<IInstanceTransitionRepository>();
        repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(_currentUser, repo);
    }

    public static TheoryData<string?, string?, string[]?> Cases()
    {
        var data = new TheoryData<string?, string?, string[]?>();
        string?[] stamps = [Stamp, "{ not json", null];
        string?[] actors = ["alice", "bob"];
        string[]?[] roleSets = [null, ["maker"], ["supervisor"], ["blocked"], ["maker", "blocked"], ["checker", "supervisor"]];
        foreach (var stamp in stamps)
        foreach (var actor in actors)
        foreach (var roles in roleSets)
            data.Add(stamp, actor, roles);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Batch_answer_equals_the_per_key_answer(string? stamp, string? actor, string[]? roles)
    {
        _currentUser.ActorUserName.Returns(actor);
        var workflow = BuildWorkflow();
        var state = workflow.FindState(Review)!;
        var instance = NewInstance(stamp);

        var batch = await _sut.FilterAuthorizedTransitionKeysAsync(
            workflow, state, instance, Keys, roles, cancellationToken: CancellationToken.None);

        var perKey = new List<string>();
        foreach (var key in Keys)
        {
            var transition = workflow.FindTransitionInContext(key)!;
            if (await _sut.IsTransitionAllowedInStateAsync(
                    workflow, transition, Review, instance, roles, cancellationToken: CancellationToken.None))
                perKey.Add(key);
        }

        batch.ShouldBe(perKey);
    }

    [Fact]
    public async Task Stamped_overrides_replace_the_childs_grants()
    {
        _currentUser.ActorUserName.Returns("alice");
        var workflow = BuildWorkflow();

        var maker = await _sut.FilterAuthorizedTransitionKeysAsync(
            workflow, workflow.FindState(Review)!, NewInstance(Stamp), Keys, ["maker"],
            cancellationToken: CancellationToken.None);
        var supervisor = await _sut.FilterAuthorizedTransitionKeysAsync(
            workflow, workflow.FindState(Review)!, NewInstance(Stamp), Keys, ["supervisor"],
            cancellationToken: CancellationToken.None);

        // approve: stamp narrows to supervisor. reject: stamp admits the instance starter (alice). note / escalate:
        // no stamped roles → own "maker" grant. assign / shared-x: unstamped, own grants.
        maker.ShouldBe(["reject", "note", "escalate", "assign", "shared-x"]);
        supervisor.ShouldBe(["approve", "reject", "shared-x"]);
    }

    private static Instance NewInstance(string? stamp)
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedBy = "alice";
        if (stamp is not null)
            instance.ExtraProperties[DomainConsts.MetaDataKeys.TransitionRoleOverrides] = stamp;
        return instance;
    }

    private static WorkflowDefinition BuildWorkflow()
    {
        static string T(string key, string roles) => $$"""
            { "key": "{{key}}", "target": "done", "versionStrategy": "Minor", "triggerType": 0,
              "labels": [{"label": "{{key}}", "language": "en"}], "roles": {{roles}} }
            """;

        const string maker = """[{"role":"maker","grant":"allow"}]""";
        var json = $$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                { "key": "{{Review}}", "stateType": 2, "versionStrategy": "Minor",
                  "labels": [{"label": "Review", "language": "en"}],
                  "transitions": [
                    {{T("approve", maker)}},
                    {{T("reject", maker)}},
                    {{T("note", maker)}},
                    {{T("escalate", maker)}},
                    {{T("assign", maker)}}
                  ] },
                { "key": "done", "stateType": 3, "versionStrategy": "Minor",
                  "labels": [{"label": "Done", "language": "en"}], "transitions": [] }
            ],
            "sharedTransitions": [
                { "key": "shared-x", "target": "$self", "versionStrategy": "Minor", "triggerType": 0,
                  "labels": [{"label": "Shared", "language": "en"}],
                  "availableIn": [ { "state": "{{Review}}", "roles": [{"role":"supervisor","grant":"allow"},{"role":"maker","grant":"allow"}] } ],
                  "roles": [{"role":"blocked","grant":"deny"}] }
            ],
            "startTransition": {
                "key": "start", "target": "{{Review}}", "versionStrategy": "Minor",
                "triggerType": 0, "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference("flow", "domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
