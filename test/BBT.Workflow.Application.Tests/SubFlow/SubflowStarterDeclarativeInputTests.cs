using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Shared;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// A SubFlow state's declarative <c>input</c> block (Phase C1): it builds the child start request
/// <c>{ attributes, headers, key, tags }</c> where the <c>mapping</c> script's <see cref="ScriptResponse"/> went, and
/// from there the existing path (headers → instance-call credential) runs unchanged. A failing block fails the start
/// with the error a failing input mapping script returns, before any child is started.
/// </summary>
public sealed class SubflowStarterDeclarativeInputTests
{
    private const string HopInput = """{"amount":5}""";

    private readonly IInstanceCommandGateway _gateway = Substitute.For<IInstanceCommandGateway>();
    private readonly IScriptEngine _engine = Substitute.For<IScriptEngine>();
    private readonly ICallerAccessorFactory _callerFactory = Substitute.For<ICallerAccessorFactory>();
    private readonly CapturingLogger _logger = new();
    private readonly Definitions.Workflow _workflow = WorkflowFactory.CreateDefault();
    private readonly Instance _parent;
    private StartInstanceInput? _captured;

    public SubflowStarterDeclarativeInputTests()
    {
        _parent = Instance.Create(Guid.NewGuid(), _workflow.Key, _workflow.Version, "parent-key");
        var row = new InstanceData(Guid.NewGuid(), _parent.Id, "1.0.0",
            new JsonData("""{"customer":{"id":7},"labels":["vip","tr"]}"""), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        _parent.AcceptPersistedData(row);

        _gateway.StartSubAsync(Arg.Do<StartInstanceInput>(i => _captured = i), Arg.Any<CancellationToken>())
            .Returns(_ => Result<StartInstanceOutput>.Ok(new StartInstanceOutput
            {
                Id = Guid.NewGuid(), Status = InstanceStatus.Active
            }));
    }

    [Fact]
    public async Task InputBlock_BuildsAttributesHeadersKeyTags()
    {
        var block = Block(select: "$.data.labels", into: "$.tags", set:
        [
            ("$.attributes.customer", "data.customer"),
            ("$.attributes.amount", "input.amount"),
            ("$.headers.x-trace", "\"t1\""),
            ("$.headers.x-num", "42"),
            ("$.headers.x-flag", "true"),
            ("$.key", "\"child-\" + instance.key")
        ]);

        var result = await StartAsync(block);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _captured.ShouldNotBeNull();
        JsonSerializer.Serialize(_captured!.Instance.Attributes).ShouldBe("""{"customer":{"id":7},"amount":5}""");
        _captured.Headers["x-trace"].ShouldBe("t1");
        _captured.Headers["x-num"].ShouldBe("42");
        _captured.Headers["x-flag"].ShouldBe("true");
        _captured.Instance.Key.ShouldBe("child-parent-key");
        _captured.Instance.Tags.ShouldContain("vip");
        _captured.Instance.Tags.ShouldContain("tr");
        _captured.Instance.Tags.ShouldContain($"parent.key:{_parent.Key}");
        // The caller's credential is still filled where the block wrote none.
        _captured.Headers["sub"].ShouldBe("u1");
    }

    [Fact]
    public async Task BlankCredentialHeader_StaysBlank()
    {
        var result = await StartAsync(Block(set: [("$.headers.sub", "\"\"")]));

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _captured!.Headers["sub"].ShouldBe("");
        _captured.Headers["act_sub"].ShouldBe("a1");
    }

    [Fact]
    public async Task TagsNotAnArray_FailsBeforeChildStarts()
    {
        var result = await StartAsync(Block(set: [("$.tags", "\"single\"")]));

        result.IsSuccess.ShouldBeFalse();
        var expected = WorkflowErrors.SubFlowInputMappingFailed("child-flow", "x");
        result.Error.Code.ShouldBe(expected.Code);
        result.Error.Message!.ShouldStartWith("SubFlow 'child-flow' input mapping failed: ");
        result.Error.Message.ShouldContain("tags");
        await _gateway.DidNotReceiveWithAnyArgs().StartSubAsync(default!, default);
        _logger.Entries.ShouldContain(e => e.EventId == 10278 && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task FailingExpression_FailsLikeAFailingScript()
    {
        var result = await StartAsync(Block(set: [("$.attributes.x", "int.Parse(\"not-a-number\")")]));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrors.SubFlowInputMappingFailed("child-flow", "x").Code);
        result.Error.Message!.ShouldStartWith("SubFlow 'child-flow' input mapping failed: ");
        await _gateway.DidNotReceiveWithAnyArgs().StartSubAsync(default!, default);
        _logger.Entries.ShouldContain(e => e.EventId == 10278);
    }

    [Fact]
    public async Task NoMappingNoBlock_StartsWithEmptyAttributes()
    {
        var result = await StartAsync(block: null);

        result.IsSuccess.ShouldBeTrue();
        _captured!.Instance.Attributes.ShouldBeNull();
        _captured.Instance.Key.ShouldBe("parent-key");
        _captured.Headers["sub"].ShouldBe("u1");
    }

    [Fact]
    public async Task BothScriptAndBlock_RunsScript()
    {
        var mapping = Substitute.For<ISubFlowMapping>();
        mapping.InputHandler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse { Data = new Dictionary<string, object> { ["fromScript"] = true } }));
        _engine.CompileToInstanceAsync<ISubFlowMapping>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings?>(),
                Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mapping));

        var result = await StartAsync(Block(set: [("$.attributes.fromBlock", "true")]), withScript: true);

        result.IsSuccess.ShouldBeTrue();
        var attributes = JsonSerializer.Serialize(_captured!.Instance.Attributes);
        attributes.ShouldContain("fromScript");
        attributes.ShouldNotContain("fromBlock");
    }

    [Fact]
    public async Task CodelessStoredMappingAndBlock_RunsBlock()
    {
        // "Script present" is HasMappingCode on both SubFlow sides (SubflowOutputMappingService agrees).
        var result = await StartAsync(Block(set: [("$.attributes.fromBlock", "true")]),
            script: ScriptCode.FromNative(" "));

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        JsonSerializer.Serialize(_captured!.Instance.Attributes).ShouldBe("""{"fromBlock":true}""");
        await _engine.DidNotReceiveWithAnyArgs().CompileToInstanceAsync<ISubFlowMapping>(
            default!, default, default, default, default);
    }

    [Fact]
    public async Task InputBlock_OnSubProcess_DoesNotRun()
    {
        // Publish rejects blocks on a 'P'; a stored one is ignored at run time, as the output block is.
        var result = await StartAsync(Block(set: [("$.attributes.fromBlock", "true")]), type: "P");

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        _captured!.Instance.Attributes.ShouldBeNull();
    }

    [Fact]
    public async Task InputBlock_UserReadsTheParentHopCaller()
    {
        var caller = Substitute.For<ICallerAccessor>();
        caller.IsAvailable.Returns(true);
        caller.RolesAsync().Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "approver" }));
        caller.HasRoleAsync(Arg.Any<string>()).Returns(call => Task.FromResult(call.Arg<string>() == "approver"));
        IReadOnlyDictionary<string, string?>? hopHeaders = null;
        _callerFactory.ForHop(ExecutionActor.User, Arg.Do<IReadOnlyDictionary<string, string?>?>(h => hopHeaders = h),
                Arg.Any<CancellationToken>())
            .Returns(caller);

        var result = await StartAsync(Block(set: [("$.attributes.approver", "user.hasRole(\"approver\")")]),
            actor: ExecutionActor.User);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        JsonSerializer.Serialize(_captured!.Instance.Attributes).ShouldBe("""{"approver":true}""");
        hopHeaders.ShouldNotBeNull();
        hopHeaders!["sub"].ShouldBe("u1");
    }

    [Fact]
    public async Task InputBlock_RunsUnderTheSubflowInputMappingSpan()
    {
        using var spans = new BBT.Workflow.Application.Tests.Telemetry.TraceSpanCollector("BBT.Workflow.Scripting");

        (await StartAsync(Block(set: [("$.attributes.x", "int.Parse(\"bad\")")]))).IsSuccess.ShouldBeFalse();

        var span = spans.Spans.Where(a => a.DisplayName == "Script.Execute").ShouldHaveSingleItem();
        span.GetTagItem(TelemetryConstants.TagNames.ScriptKind).ShouldBe("subflowInputMapping");
        span.Status.ShouldBe(System.Diagnostics.ActivityStatusCode.Error);
    }

    private async Task<Result<string?>> StartAsync(
        MappingBlock? block, bool withScript = false, ExecutionActor? actor = null, ScriptCode? script = null,
        string type = "S")
    {
        var starter = new SubflowStarter(_gateway, new ConfigurationBuilder().Build(), _engine, _logger,
            callerAccessorFactory: _callerFactory);
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow(type, new Reference("child-flow", "remote", "sys-flows", "1.0.0"),
            script ?? (withScript ? ScriptCode.FromNative("// mapping") : null!), null);
        typeof(Definitions.SubFlow).GetProperty(nameof(Definitions.SubFlow.Input))!.SetValue(state.SubFlow, block);
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), _parent.Id, state.Key,
            Guid.NewGuid(), type, "remote", "child-flow", "1.0.0");

        return await starter.StartAsync(_workflow, _parent, state, TransitionFactory.CreateDefault(),
            correlation, Context(), ExecMode.Sync, actor);
    }

    private ScriptContext Context()
    {
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(_parent)
            .SetHeaders(new Dictionary<string, string> { ["sub"] = "u1", ["act_sub"] = "a1" })
            .Build();
        context.BindHopInput(JsonDocument.Parse(HopInput).RootElement.Clone());
        return context;
    }

    private static MappingBlock Block(string? select = null, string? into = null, (string Path, string Expr)[]? set = null) =>
        new(select, into, set is null
            ? null
            : new OrderedStringMap(set.Select(e => new KeyValuePair<string, string>(e.Path, e.Expr))));

    private sealed class CapturingLogger : ILogger<SubflowStarter>
    {
        public List<(int EventId, LogLevel Level)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel));
    }
}
