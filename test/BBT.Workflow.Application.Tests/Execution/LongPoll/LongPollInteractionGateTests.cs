using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Unit tests for <see cref="LongPollInteractionGate"/> — the single admission point both
/// interaction surfaces (State function emit, acknowledge) evaluate <c>interaction.longPoll</c>
/// authorization through. Pins the arm selection (rule, else roles, else allow), the fail-closed
/// rule contract (only a successful evaluation returning <c>true</c> admits), the lazy caller-role
/// resolution (the factory runs only when the roles arm applies), and that a role-resolution
/// failure propagates as a failure rather than collapsing into a denial.
/// </summary>
public class LongPollInteractionGateTests
{
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly IScriptContextBuilder _scriptContextBuilder = Substitute.For<IScriptContextBuilder>();
    private readonly ITaskConditionService _taskConditionService = Substitute.For<ITaskConditionService>();
    private readonly ITransitionAuthorizationManager _authorizationManager =
        Substitute.For<ITransitionAuthorizationManager>();
    private readonly LongPollInteractionGate _gate;

    public LongPollInteractionGateTests()
    {
        var builder = _scriptContextBuilder;
        builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithTransition(Arg.Any<string>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(new ScriptContext(Substitute.For<ILogger<ScriptContext>>()));
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _gate = new LongPollInteractionGate(
            _scriptContextFactory,
            Substitute.For<IInstanceRepository>(),
            Substitute.For<IRuntimeInfoProvider>(),
            _taskConditionService,
            _authorizationManager,
            Substitute.For<ILogger<LongPollInteractionGate>>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsAdmittedAsync_RuleArm_ReturnsConditionVerdict(bool verdict)
    {
        _taskConditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(verdict));
        var rolesResolved = false;

        var admitted = await _gate.IsAdmittedAsync(
            CreateInstance(), BuildWorkflow(), CreateRuleState(),
            headers: null, queryParameters: null,
            _ => { rolesResolved = true; return CallerRoles("never-used"); },
            surface: "state", CancellationToken.None);

        admitted.IsSuccess.ShouldBeTrue();
        admitted.Value.ShouldBe(verdict);
        // A rule state never pays the caller-role resolution cost, and roles are never consulted.
        rolesResolved.ShouldBeFalse();
        await _authorizationManager.DidNotReceiveWithAnyArgs().IsAnyRoleAllowedForGrantsAsync(
            default, default!, default, default, default);
        // No Body pre-materialization: this surface has no request payload, and filling Body with the
        // latest instance data cost a serialize+parse per poll — rules read context.Instance.Data.
        _scriptContextBuilder.DidNotReceiveWithAnyArgs().WithBody(default);
    }

    [Fact]
    public async Task IsAdmittedAsync_RuleArm_DeniesWhenEvaluationFails()
    {
        _taskConditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("Script:Compile", "boom")));

        var admitted = await _gate.IsAdmittedAsync(
            CreateInstance(), BuildWorkflow(), CreateRuleState(),
            headers: null, queryParameters: null,
            _ => CallerRoles(), surface: "ack", CancellationToken.None);

        // Fail-closed: a broken rule denies (Ok(false), not a failure) — the fallback timeout still
        // resumes the pipeline, so denial cannot strand the instance.
        admitted.IsSuccess.ShouldBeTrue();
        admitted.Value.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsAdmittedAsync_RolesArm_ReturnsEvaluatorVerdict(bool verdict)
    {
        var headers = new Dictionary<string, string?> { ["x-channel"] = "branch" };
        var query = new Dictionary<string, string?> { ["q"] = "1" };
        var instance = CreateInstance();
        _authorizationManager.IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(),
                Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(verdict);

        var admitted = await _gate.IsAdmittedAsync(
            instance, BuildWorkflow(), CreateRolesState(),
            headers, query, _ => CallerRoles("caller-role"), surface: "state", CancellationToken.None);

        admitted.IsSuccess.ShouldBeTrue();
        admitted.Value.ShouldBe(verdict);
        // The single role evaluator decides, and it must see the request context — omitting it
        // silently empties $.context.Headers/QueryParameters instead of failing closed.
        await _authorizationManager.Received(1).IsAnyRoleAllowedForGrantsAsync(
            Arg.Is<IReadOnlyCollection<string>?>(r => r!.Contains("caller-role")),
            Arg.Is<IReadOnlyCollection<RoleGrant>>(g => g.Count == 1),
            instance,
            Arg.Is<AuthorizationRequestContext?>(c =>
                ReferenceEquals(c!.Headers, headers) && ReferenceEquals(c.QueryParameters, query)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAdmittedAsync_RolesArm_PropagatesRoleResolutionFailure()
    {
        var error = Error.Failure("Roles:Resolve", "provider unreachable");

        var admitted = await _gate.IsAdmittedAsync(
            CreateInstance(), BuildWorkflow(), CreateRolesState(),
            headers: null, queryParameters: null,
            _ => Task.FromResult(Result<IReadOnlyCollection<string>>.Fail(error)),
            surface: "ack", CancellationToken.None);

        // A resolution failure is the caller's error to surface, not an access denial.
        admitted.IsSuccess.ShouldBeFalse();
        admitted.Error.Code.ShouldBe(error.Code);
        await _authorizationManager.DidNotReceiveWithAnyArgs().IsAnyRoleAllowedForGrantsAsync(
            default, default!, default, default, default);
    }

    [Fact]
    public async Task IsAdmittedAsync_NoArm_Admits()
    {
        var rolesResolved = false;
        Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>> factory =
            _ => { rolesResolved = true; return CallerRoles(); };

        // longPoll declared without roles or rule, state without interaction, and no entered state
        // at all (the acknowledge surface's FindState can answer null) — all default-allow.
        foreach (var state in new[] { CreateBareLongPollState(), CreatePlainState(), null })
        {
            var admitted = await _gate.IsAdmittedAsync(
                CreateInstance(), BuildWorkflow(), state,
                headers: null, queryParameters: null, factory, surface: "state", CancellationToken.None);

            admitted.IsSuccess.ShouldBeTrue();
            admitted.Value.ShouldBeTrue();
        }

        rolesResolved.ShouldBeFalse();
    }

    private static Task<Result<IReadOnlyCollection<string>>> CallerRoles(params string[] roles) =>
        Task.FromResult(Result<IReadOnlyCollection<string>>.Ok(roles));

    private static State CreateRuleState() => DeserializeState(
        """, "rule": { "location": "./gate.csx", "code": "cmV0dXJuIHRydWU7" }""");

    private static State CreateRolesState() => DeserializeState(
        """, "roles": [ { "role": "FullAuthorized", "grant": "allow" } ]""");

    private static State CreateBareLongPollState() => DeserializeState(longPollExtra: "");

    /// <summary>
    /// States with <c>interaction</c> are JSON-deserialized because the interaction value objects
    /// expose only private (JSON) constructors; <paramref name="longPollExtra"/> is appended inside
    /// the <c>longPoll</c> object (the arm under test).
    /// </summary>
    private static State DeserializeState(string longPollExtra) =>
        System.Text.Json.JsonSerializer.Deserialize<State>($$"""
        {
            "key": "review",
            "stateType": "intermediate",
            "subType": "none",
            "versionStrategy": "Patch",
            "interaction": { "longPoll": { "terminate": true{{longPollExtra}} } }
        }
        """, JsonSerializerConstants.JsonOptions)!;

    private static State CreatePlainState() =>
        State.Create("review", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreasePatch.Code);

    private static Instance CreateInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "test-key");
        instance.ChangeState(CreatePlainState());
        return instance;
    }

    private static Definitions.Workflow BuildWorkflow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");
        workflow.SetStartTransition(Transition.Create("start", null, "review", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.AddState(CreatePlainState());
        return workflow;
    }
}
