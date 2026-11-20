using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Services;

/// <summary>
/// vnext#1268: a transition mapping that throws is reported as the same error as before, and its
/// exception goes to the caller's <c>onScriptFailure</c> — the pipeline turns it into the fault
/// incident's stack trace, the start path writes it to the log.
/// </summary>
public sealed class TransitionDataMapperTests
{
    private readonly IScriptEngine _scriptEngine = Substitute.For<IScriptEngine>();
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();

    public TransitionDataMapperTests()
    {
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithTransition(Arg.Any<Transition>()).Returns(builder);
        builder.WithBody(Arg.Any<object>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(new ScriptContext(Substitute.For<ILogger<ScriptContext>>()));
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);
    }

    private TransitionDataMapper CreateMapper() =>
        new(_scriptEngine, _scriptContextFactory, Substitute.For<IInstanceRepository>());

    [Fact]
    public async Task MappingThatThrows_ReportsTheSameError_AndHandsTheExceptionToTheCaller()
    {
        var thrown = new ArgumentException("the transition mapping found no customer");
        var mapping = Substitute.For<ITransitionMapping>();
        mapping.Handler(Arg.Any<ScriptContext>()).ReturnsForAnyArgs<Task<dynamic>>(_ => throw thrown);
        _scriptEngine.CompileToInstanceAsync<ITransitionMapping>(Arg.Any<ScriptCode>()).ReturnsForAnyArgs(Task.FromResult(mapping));
        var (workflow, transition) = WorkflowWithMapping();
        Exception? received = null;

        var result = await CreateMapper().MapTransitionDataAsync(
            new { amount = 1 }, transition, workflow, Instance.Create(Guid.NewGuid(), workflow.Key, "1.0.0"),
            Substitute.For<IRuntimeInfoProvider>(), onScriptFailure: ex => received = ex);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExecutionStepFailed);
        result.Error.Message.ShouldBe("Failed to execute transition mapping: " + thrown.Message);
        result.Error.Detail.ShouldBe(nameof(ArgumentException));
        received.ShouldBeSameAs(thrown);
    }

    [Fact]
    public async Task MappingThatSucceeds_NeverCallsTheFailureCallback()
    {
        var mapping = Substitute.For<ITransitionMapping>();
        mapping.Handler(Arg.Any<ScriptContext>()).ReturnsForAnyArgs(Task.FromResult<dynamic>(new { mapped = true }));
        _scriptEngine.CompileToInstanceAsync<ITransitionMapping>(Arg.Any<ScriptCode>()).ReturnsForAnyArgs(Task.FromResult(mapping));
        var (workflow, transition) = WorkflowWithMapping();
        var calls = 0;

        var result = await CreateMapper().MapTransitionDataAsync(
            new { amount = 1 }, transition, workflow, Instance.Create(Guid.NewGuid(), workflow.Key, "1.0.0"),
            Substitute.For<IRuntimeInfoProvider>(), onScriptFailure: _ => calls++);

        result.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(0);
    }

    private static (Definitions.Workflow Workflow, Transition Transition) WorkflowWithMapping()
    {
        const string json = """
        {
            "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
            "states": [
                { "key": "state1", "stateType": "Intermediate", "transitions": [
                    { "key": "tr1", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch",
                      "mapping": { "location": "inline", "code": "dummy", "type": "L", "encoding": "NAT" } } ] },
                { "key": "state2", "stateType": "Intermediate", "transitions": [] }
            ],
            "sharedTransitions": [], "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("mapping-flow", "test-domain", "sys-flows", "1.0.0"));
        return (workflow, workflow.GetState("state1").Value!.Transitions.First(t => t.Key == "tr1"));
    }

    [Fact]
    public async Task MappingThatIsCancelled_ReportsTheSameError_ButNeverCallsTheFailureCallback()
    {
        var mapping = Substitute.For<ITransitionMapping>();
        mapping.Handler(Arg.Any<ScriptContext>()).ReturnsForAnyArgs<Task<dynamic>>(_ => throw new OperationCanceledException());
        _scriptEngine.CompileToInstanceAsync<ITransitionMapping>(Arg.Any<ScriptCode>()).ReturnsForAnyArgs(Task.FromResult(mapping));
        var (workflow, transition) = WorkflowWithMapping();
        var calls = 0;

        var result = await CreateMapper().MapTransitionDataAsync(
            new { amount = 1 }, transition, workflow, Instance.Create(Guid.NewGuid(), workflow.Key, "1.0.0"),
            Substitute.For<IRuntimeInfoProvider>(), onScriptFailure: _ => calls++);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExecutionStepFailed);
        calls.ShouldBe(0, "a cancellation the script sees is not its failure");
    }
}
