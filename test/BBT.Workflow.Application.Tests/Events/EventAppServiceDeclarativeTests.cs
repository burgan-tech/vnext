using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Events;

/// <summary>
/// An event's declarative <c>input</c> block (Phase C2): it fills <c>{ key, attributes }</c> exactly where a mapping
/// script's <see cref="EventMappingResult"/> goes — <c>key</c> correlates (start: the new key; transition: the active
/// instance), <c>attributes</c> is the body, <c>input</c> is the unwrapped payload, a stored event with both runs the
/// script and a failing block fails like a failing script (<c>EventMappingFailed</c>).
/// </summary>
public sealed class EventAppServiceDeclarativeTests
{
    private const string Domain = "test-domain";
    private const string Flow = "event-flow";
    private const string TransitionKey = "tr1";
    private const string Payload = """{"orderId":"A-1","amount":5,"customer":{"id":7}}""";
    private const string ScriptJson = """{"location":"./event.csx","code":"YQ==","type":"L","encoding":"B64"}""";

    private readonly IComponentCacheStore _cacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IScriptEngine _scriptEngine = Substitute.For<IScriptEngine>();
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceCommandAppService _commands = Substitute.For<IInstanceCommandAppService>();
    private readonly IInstanceSelectorResolver _selectorResolver = Substitute.For<IInstanceSelectorResolver>();
    private readonly CapturingLogger _logger = new();

    private StartInstanceInput? _started;
    private (string Instance, string Key, TransitionInput Input)? _transitioned;

    public EventAppServiceDeclarativeTests()
    {
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(builder);
        builder.WithTransition(Arg.Any<Transition>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>>()).Returns(builder);
        builder.WithEventPayload(Arg.Any<object>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(_ => new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .Build());
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _commands.StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _started = call.Arg<StartInstanceInput>();
                return Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Key = _started.Instance?.Key });
            });
        _commands.TransitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _transitioned = (call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<TransitionInput>(2));
                return Result<TransitionOutput>.Ok(new TransitionOutput());
            });
    }

    [Fact]
    public async Task StartEvent_InputBlock_StartsWithKeyAndAttributes()
    {
        UseWorkflow(startEvent: new Event(null, Block(
            select: "$.input", into: "$.attributes",
            set: [("$.key", "input.orderId"), ("$.attributes.customerId", "input.customer.id")])));

        var result = await Handle(EventAction.Start, Payload);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _started.ShouldNotBeNull();
        _started!.Instance!.Key.ShouldBe("A-1");
        _started.Instance.Attributes!.Value.GetRawText()
            .ShouldBe("""{"orderId":"A-1","amount":5,"customer":{"id":7},"customerId":7}""");
    }

    [Fact]
    public async Task StartEvent_EmptyAttributes_StartsEmpty()
    {
        // `attributes` starts empty: a block that writes only the key starts the instance with no data.
        UseWorkflow(startEvent: new Event(null, Block(set: [("$.key", "input.orderId")])));

        var result = await Handle(EventAction.Start, Payload);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _started!.Instance!.Key.ShouldBe("A-1");
        _started.Instance.Attributes.ShouldBeNull();
    }

    [Fact]
    public async Task TransitionEvent_KeyFromPayload_DrivesTransition()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "A-1");
        _instanceRepository.FindActiveByKeyAsync("A-1", Arg.Any<CancellationToken>()).Returns(instance);
        UseWorkflow(transitionEvent: new Event(null, Block(
            set: [("$.key", "input.orderId"), ("$.attributes.paid", "input.amount.AsInt32() > 1")])));

        var result = await Handle(EventAction.Transition, Payload);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _transitioned.ShouldNotBeNull();
        _transitioned!.Value.Instance.ShouldBe(instance.Id.ToString());
        _transitioned.Value.Key.ShouldBe(TransitionKey);
        _transitioned.Value.Input.Data!.Attributes!.Value.GetRawText().ShouldBe("""{"paid":true}""");
    }

    [Fact]
    public async Task TransitionEvent_NoKey_IsIgnored()
    {
        // No key and no Selector (C#-only): today's "instance not found, ignored" — success, nothing dispatched.
        UseWorkflow(transitionEvent: new Event(null, Block(set: [("$.attributes.paid", "true")])));

        var result = await Handle(EventAction.Transition, Payload);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBeNull();
        _transitioned.ShouldBeNull();
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindActiveByKeyAsync(default!, default);
        await _selectorResolver.DidNotReceiveWithAnyArgs().ResolveKeyAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task CloudEventEnvelope_IsUnwrappedForInput()
    {
        UseWorkflow(startEvent: new Event(null, Block(
            select: "$.input", into: "$.attributes", set: [("$.key", "input.orderId")])));
        var envelope = $$"""{"specversion":"1.0","id":"e-1","source":"test","type":"order.created","data":{{Payload}}}""";

        var result = await Handle(EventAction.Start, envelope);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _started!.Instance!.Key.ShouldBe("A-1");
        _started.Instance.Attributes!.Value.GetRawText().ShouldBe(Payload);
    }

    [Fact]
    public async Task BothScriptAndBlock_RunsScript()
    {
        // A stored definition with both members deserializes into both (publish rejects it; storage may predate that).
        var stored = JsonSerializer.Deserialize<Event>(
            "{\"mapping\":" + ScriptJson + ",\"input\":{\"set\":{\"$.key\":\"input.orderId\"}}}",
            JsonSerializerConstants.JsonOptions)!;
        stored.Mapping.ShouldNotBeNull();
        stored.Mapping!.Location.ShouldBe("./event.csx");
        stored.Input.ShouldNotBeNull();
        stored.Input!.Set!["$.key"].ShouldBe("input.orderId");

        var mapping = Substitute.For<IEventMapping>();
        mapping.Handler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new EventMappingResult { InstanceKey = "from-script" }));
        _scriptEngine.CompileToInstanceAsync<IEventMapping>(Arg.Any<ScriptCode>())
            .ReturnsForAnyArgs(Task.FromResult(mapping));
        UseWorkflow(startEvent: stored);

        var result = await Handle(EventAction.Start, Payload);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _started!.Instance!.Key.ShouldBe("from-script");
        await mapping.Received(1).Handler(Arg.Any<ScriptContext>());
    }

    [Fact]
    public async Task BlockFailure_ReturnsEventMappingFailed()
    {
        UseWorkflow(startEvent: new Event(null, Block(set: [("$.key", "int.Parse(\"not-a-number\").ToString()")])));
        using var spans = new BBT.Workflow.Application.Tests.Telemetry.TraceSpanCollector("BBT.Workflow.Scripting");

        var result = await Handle(EventAction.Start, Payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("EventMappingFailed");
        result.Error.Message!.ShouldContain("set['$.key']");
        _started.ShouldBeNull();
        _logger.Entries.ShouldContain(e => e.EventId == EventMappingFailedId && e.Level == LogLevel.Error);
        var span = spans.Spans.Where(a => a.DisplayName == "Script.Execute").ShouldHaveSingleItem();
        span.GetTagItem(BBT.Workflow.Logging.TelemetryConstants.TagNames.ScriptKind).ShouldBe("eventMapping");
        span.Status.ShouldBe(System.Diagnostics.ActivityStatusCode.Error);
    }

    [Fact]
    public async Task NonStringKey_FailsLikeScript()
    {
        UseWorkflow(startEvent: new Event(null, Block(set: [("$.key", "input.amount")])));

        var result = await Handle(EventAction.Start, Payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("EventMappingFailed");
        result.Error.Message!.ShouldContain("$.key");
        _started.ShouldBeNull();
    }

    [Fact]
    public async Task NeitherScriptNorBlock_IsEventDefinitionMissing()
    {
        UseWorkflow(startEvent: new Event(null));

        var result = await Handle(EventAction.Start, Payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("EventDefinitionMissing");
    }

    private static int EventMappingFailedId =>
        typeof(BBT.Workflow.Logging.WorkflowLogs).GetMethod(nameof(BBT.Workflow.Logging.WorkflowLogs.EventMappingFailed))!
            .GetCustomAttributes(typeof(LoggerMessageAttribute), false).Cast<LoggerMessageAttribute>().Single().EventId;

    private Task<Result<object?>> Handle(EventAction action, string payload) =>
        new EventAppService(
                _cacheStore, _scriptEngine, _scriptContextFactory, _instanceRepository, _commands, _selectorResolver,
                Substitute.For<IRuntimeInfoProvider>(), _logger)
            .HandleAsync(new EventInput
            {
                Domain = Domain,
                Workflow = Flow,
                Action = action,
                TransitionKey = action == EventAction.Transition ? TransitionKey : null,
                Payload = JsonDocument.Parse(payload).RootElement.Clone()
            });

    private static MappingBlock Block(string? select = null, string? into = null, (string Path, string Expr)[]? set = null) =>
        new(select, into, set is null
            ? null
            : new OrderedStringMap(set.Select(e => new KeyValuePair<string, string>(e.Path, e.Expr))));

    private void UseWorkflow(Event? startEvent = null, Event? transitionEvent = null)
    {
        const string json = """
        {
            "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
            "states": [
                { "key": "state1", "stateType": "Intermediate", "transitions": [
                    { "key": "tr1", "from": "state1", "target": "state2", "triggerType": "Event", "versionStrategy": "Patch" } ] },
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
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", "1.0.0"));
        typeof(Definitions.Workflow).GetProperty(nameof(Definitions.Workflow.Event))!.SetValue(workflow, startEvent);
        var transition = workflow.GetState("state1").Value!.Transitions.First(t => t.Key == TransitionKey);
        typeof(Transition).GetProperty(nameof(Transition.Event))!.SetValue(transition, transitionEvent);

        _cacheStore.GetFlowAsync(Domain, Flow, null, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }

    private sealed class CapturingLogger : ILogger<EventAppService>
    {
        public List<(int EventId, LogLevel Level)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel));
    }
}
