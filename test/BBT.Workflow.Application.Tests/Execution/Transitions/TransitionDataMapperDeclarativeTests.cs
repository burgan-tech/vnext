using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions;

/// <summary>
/// A transition's declarative <c>output</c> block (Phase C1): it replaces the payload exactly where a
/// <c>transitions[].mapping</c> script result goes — the delta starts empty, only what <c>select</c> / <c>into</c> /
/// <c>set</c> write is kept, an empty delta is null, a stored transition with both runs the script, and a failing block
/// fails with the error a failing script returns.
/// </summary>
public sealed class TransitionDataMapperDeclarativeTests
{
    private const string Payload = """{"name":"Ada","amount":5,"nested":{"k":"v"}}""";

    private readonly IScriptEngine _scriptEngine = Substitute.For<IScriptEngine>();
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly ICallerAccessorFactory _callerFactory = Substitute.For<ICallerAccessorFactory>();
    private readonly CapturingLogger _logger = new();
    private readonly Instance _instance;

    public TransitionDataMapperDeclarativeTests()
    {
        _instance = Instance.Create(Guid.NewGuid(), "mapping-flow", "1.0.0", "ctx-key");
        var row = new InstanceData(Guid.NewGuid(), _instance.Id, "1.0.0", new JsonData("""{"customer":{"id":7}}"""), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        _instance.AcceptPersistedData(row);

        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithTransition(Arg.Any<Transition>()).Returns(builder);
        builder.WithBody(Arg.Any<object>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(_ => new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(_instance)
            .Build());
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);
    }

    private TransitionDataMapper CreateMapper(bool withCallerFactory = true) =>
        new(_scriptEngine, _scriptContextFactory, Substitute.For<IInstanceRepository>(), Substitute.For<IRuntimeInfoProvider>(),
            callerAccessorFactory: withCallerFactory ? _callerFactory : null, logger: _logger);

    [Fact]
    public async Task OutputBlock_ReplacesPayloadWithDelta()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.customerName", "input.name"), ("$.ref", "data.customer.id")]));

        var result = await Map(workflow, transition);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value).ShouldBe("""{"customerName":"Ada","ref":7}""");
    }

    [Fact]
    public async Task OutputBlock_SelectInputKeepsEverything()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(select: "$.input"));

        var result = await Map(workflow, transition);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        JsonNode.DeepEquals(JsonNode.Parse(Json(result.Value)), JsonNode.Parse(Payload)).ShouldBeTrue(Json(result.Value));
    }

    [Fact]
    public async Task OutputBlock_SelectIntoNestsTheRequest()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(select: "$.input.nested", into: "$.application"));

        var result = await Map(workflow, transition);

        Json(result.Value).ShouldBe("""{"application":{"k":"v"}}""");
    }

    [Fact]
    public async Task EmptyDelta_NoVersion_BodyUnchanged()
    {
        // Nothing was written (a missing select source writes nothing): null, exactly as a script returning null.
        var (workflow, transition) = WorkflowWithTransition(output: Block(select: "$.input.missing", into: "$.x"));

        var result = await Map(workflow, transition);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task BothScriptAndBlock_RunsScript()
    {
        var mapping = Substitute.For<ITransitionMapping>();
        mapping.Handler(Arg.Any<ScriptContext>()).ReturnsForAnyArgs(Task.FromResult<dynamic>(new { fromScript = true }));
        _scriptEngine.CompileToInstanceAsync<ITransitionMapping>(Arg.Any<ScriptCode>()).ReturnsForAnyArgs(Task.FromResult(mapping));
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.fromBlock", "true")]), withScript: true);

        var result = await Map(workflow, transition);

        result.IsSuccess.ShouldBeTrue();
        JsonSerializer.Serialize(result.Value).ShouldContain("fromScript");
        JsonSerializer.Serialize(result.Value).ShouldNotContain("fromBlock");
    }

    [Fact]
    public async Task OutputBlockFailure_FailsLikeScriptFailure()
    {
        // The error a failing mapping script returns today (TransitionDataMapperTests pins it).
        var scriptError = ExecutionErrors.TransitionMappingFailed("boom", nameof(ArgumentException));
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.x", "int.Parse(\"not-a-number\")")]));
        Exception? callback = null;

        var result = await CreateMapper().MapTransitionDataAsync(
            JsonDocument.Parse(Payload).RootElement.Clone(), transition, workflow, _instance,
            onScriptFailure: ex => callback = ex, actor: ExecutionActor.User);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(scriptError.Code);
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExecutionStepFailed);
        result.Error.Message!.ShouldStartWith("Failed to execute transition mapping: ");
        result.Error.Message.ShouldContain("set['$.x']");
        result.Error.Detail.ShouldNotBeNullOrWhiteSpace();
        callback.ShouldBeNull("a block has no exception to hand to the incident; its failure is logged instead");
        _logger.Entries.ShouldContain(e => e.EventId == 10277 && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task OutputBlockThatDoesNotBuild_FailsLikeScriptFailure()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.x", "result.data")]));

        var result = await Map(workflow, transition);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExecutionStepFailed);
        result.Error.Message!.ShouldStartWith("Failed to execute transition mapping: ");
        _logger.Entries.ShouldContain(e => e.EventId == 10277);
    }

    [Fact]
    public async Task OutputBlock_UserReadsTheHopCaller()
    {
        var caller = Substitute.For<ICallerAccessor>();
        caller.IsAvailable.Returns(true);
        caller.RolesAsync().Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "approver" }));
        caller.HasRoleAsync(Arg.Any<string>()).Returns(call => Task.FromResult(call.Arg<string>() == "approver"));
        _callerFactory.ForHop(ExecutionActor.User, Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(caller);
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.approver", "user.hasRole(\"approver\")")]));

        var result = await Map(workflow, transition, ExecutionActor.User);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value).ShouldBe("""{"approver":true}""");
    }

    [Fact]
    public async Task OutputBlock_WithoutCallerFactory_UserIsUnavailable()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.available", "user.available")]));

        var result = await CreateMapper(withCallerFactory: false).MapTransitionDataAsync(
            JsonDocument.Parse(Payload).RootElement.Clone(), transition, workflow, _instance, actor: ExecutionActor.User);

        Json(result.Value).ShouldBe("""{"available":false}""");
    }

    [Fact]
    public async Task OutputBlock_RoleReadWithoutCaller_FailsClosed()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.r", "user.hasRole(\"x\")")]));

        var result = await CreateMapper(withCallerFactory: false).MapTransitionDataAsync(
            JsonDocument.Parse(Payload).RootElement.Clone(), transition, workflow, _instance);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExecutionStepFailed);
    }

    [Fact]
    public async Task OutputBlock_RunsUnderTheScriptExecuteSpan_LikeTheScript()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.customerName", "input.name")]));
        using var spans = new BBT.Workflow.Application.Tests.Telemetry.TraceSpanCollector("BBT.Workflow.Scripting");

        (await Map(workflow, transition)).IsSuccess.ShouldBeTrue();

        var span = spans.Spans.Where(a => a.DisplayName == "Script.Execute").ShouldHaveSingleItem();
        span.GetTagItem(BBT.Workflow.Logging.TelemetryConstants.TagNames.ScriptKind).ShouldBe("transitionMapping");
        span.Status.ShouldNotBe(System.Diagnostics.ActivityStatusCode.Error);
    }

    [Fact]
    public async Task OutputBlockFailure_MarksTheScriptExecuteSpanAsError()
    {
        var (workflow, transition) = WorkflowWithTransition(output: Block(set: [("$.x", "int.Parse(\"not-a-number\")")]));
        using var spans = new BBT.Workflow.Application.Tests.Telemetry.TraceSpanCollector("BBT.Workflow.Scripting");

        (await Map(workflow, transition)).IsSuccess.ShouldBeFalse();

        var span = spans.Spans.Where(a => a.DisplayName == "Script.Execute").ShouldHaveSingleItem();
        span.Status.ShouldBe(System.Diagnostics.ActivityStatusCode.Error);
    }

    private Task<BBT.Aether.Results.Result<object?>> Map(
        Definitions.Workflow workflow, Transition transition, ExecutionActor? actor = null) =>
        CreateMapper().MapTransitionDataAsync(
            JsonDocument.Parse(Payload).RootElement.Clone(), transition, workflow, _instance, actor: actor);

    private static string Json(object? value) => value switch
    {
        null => "null",
        JsonElement e => e.GetRawText(),
        JsonNode n => n.ToJsonString(),
        _ => JsonSerializer.Serialize(value)
    };

    internal static MappingBlock Block(string? select = null, string? into = null, (string Path, string Expr)[]? set = null) =>
        new(select, into, set is null
            ? null
            : new OrderedStringMap(set.Select(e => new KeyValuePair<string, string>(e.Path, e.Expr))));

    internal static void SetOutput(Transition transition, MappingBlock block) =>
        typeof(Transition).GetProperty(nameof(Transition.Output))!.SetValue(transition, block);

    private static (Definitions.Workflow Workflow, Transition Transition) WorkflowWithTransition(
        MappingBlock output, bool withScript = false)
    {
        const string json = """
        {
            "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
            "states": [
                { "key": "state1", "stateType": "Intermediate", "transitions": [
                    { "key": "tr1", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch" } ] },
                { "key": "state2", "stateType": "Intermediate", "transitions": [] }
            ],
            "sharedTransitions": [], "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        })!;
        workflow.SetReference(new Reference("mapping-flow", "test-domain", "sys-flows", "1.0.0"));
        var transition = workflow.GetState("state1").Value!.Transitions.First(t => t.Key == "tr1");
        SetOutput(transition, output);
        if (withScript)
            transition.SetMapping(ScriptCode.FromNative("mapping"));
        return (workflow, transition);
    }

    private sealed class CapturingLogger : ILogger<TransitionDataMapper>
    {
        public List<(int EventId, LogLevel Level)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel));
    }
}
