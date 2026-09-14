using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.LongPoll;
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
/// Unit tests for <see cref="LongPollRuleGate"/> — the single admission point both interaction
/// surfaces (State function emit, acknowledge) evaluate an <c>interaction.longPoll.rule</c> through.
/// Pins the fail-closed contract: only a successful evaluation returning <c>true</c> admits.
/// </summary>
public class LongPollRuleGateTests
{
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly ITaskConditionService _taskConditionService = Substitute.For<ITaskConditionService>();
    private readonly LongPollRuleGate _gate;

    public LongPollRuleGateTests()
    {
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithTransition(Arg.Any<string>()).Returns(builder);
        builder.WithBody(Arg.Any<object?>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(new ScriptContext(Substitute.For<ILogger<ScriptContext>>()));
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _gate = new LongPollRuleGate(
            _scriptContextFactory,
            Substitute.For<IInstanceRepository>(),
            Substitute.For<IRuntimeInfoProvider>(),
            _taskConditionService,
            Substitute.For<ILogger<LongPollRuleGate>>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsAdmittedAsync_ReturnsConditionVerdict(bool verdict)
    {
        _taskConditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(verdict));

        var admitted = await _gate.IsAdmittedAsync(
            Rule(), CreateInstance(), BuildWorkflow(), CreateState(),
            headers: null, queryParameters: null, surface: "state", CancellationToken.None);

        admitted.ShouldBe(verdict);
    }

    [Fact]
    public async Task IsAdmittedAsync_DeniesWhenEvaluationFails()
    {
        _taskConditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("Script:Compile", "boom")));

        var admitted = await _gate.IsAdmittedAsync(
            Rule(), CreateInstance(), BuildWorkflow(), CreateState(),
            headers: null, queryParameters: null, surface: "ack", CancellationToken.None);

        admitted.ShouldBeFalse();
    }

    private static ScriptCode Rule() =>
        System.Text.Json.JsonSerializer.Deserialize<ScriptCode>(
            """{ "location": "./gate.csx", "code": "cmV0dXJuIHRydWU7" }""",
            JsonSerializerConstants.JsonOptions)!;

    private static State CreateState() =>
        State.Create("review", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreasePatch.Code);

    private static Instance CreateInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "test-key");
        instance.ChangeState(CreateState());
        return instance;
    }

    private static Definitions.Workflow BuildWorkflow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");
        workflow.SetStartTransition(Transition.Create("start", null, "review", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.AddState(CreateState());
        return workflow;
    }
}
