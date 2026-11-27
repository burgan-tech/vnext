using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting.Expressions;

/// <summary>
/// Value slots outside task mappings honouring <c>location: "dynamicExpresso"</c> (transition timer, workflow timeout,
/// SubFlow timeout override, agent prompt, agent decision): the expression path, the <see cref="TimerSchedule"/>
/// conversion (decision D-2), the C# compatibility fallback with its warning, and the publish warning.
/// </summary>
public sealed class ValueSlotExpressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly ValueSlotExpressionEvaluator _sut = new(new ExpressionCompiler(64), new FakeTimeProvider(Now));

    private static ScriptCode Expr(string code) => ScriptCode.FromNative(code, ConditionLocation);

    private const string ConditionLocation = "dynamicExpresso";

    private static Instance InstanceWithData(string json)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "ctx-key");
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(json), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        instance.AcceptPersistedData(row);
        return instance;
    }

    private static ScriptContext Context(
        string json = """{"customer":{"name":"Ada"},"waitMinutes":15,"deadline":"PT5M","plain":"later"}""") =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(InstanceWithData(json))
            .Build();

    // ----- timer: expression → TimerSchedule -------------------------------------------------------------------------

    [Fact]
    public async Task Timer_TimeSpan_IsADuration()
    {
        var result = await _sut.EvaluateTimerAsync(Expr("TimeSpan.FromMinutes(data.waitMinutes.AsInt32())"), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.ScheduleType.ShouldBe(TimerScheduleType.Duration);
        result.Value.Duration.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Timer_DateTimeOffset_IsAnAbsoluteInstant()
    {
        var result = await _sut.EvaluateTimerAsync(Expr("now.AddHours(2)"), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.ScheduleType.ShouldBe(TimerScheduleType.DateTime);
        result.Value.ScheduledDateTime.ShouldBe(Now.AddHours(2).UtcDateTime);
    }

    [Theory]
    [InlineData("\"PT15M\"", 15)]
    [InlineData("data.deadline", 5)]
    public async Task Timer_IsoDurationString_IsADuration(string code, int minutes)
    {
        var result = await _sut.EvaluateTimerAsync(Expr(code), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.ScheduleType.ShouldBe(TimerScheduleType.Duration);
        result.Value.Duration.ShouldBe(TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public async Task Timer_IsoDateTimeString_IsAnAbsoluteInstant()
    {
        var result = await _sut.EvaluateTimerAsync(Expr("\"2026-12-01T10:30:00+03:00\""), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.ScheduleType.ShouldBe(TimerScheduleType.DateTime);
        result.Value.ScheduledDateTime.ShouldBe(new DateTime(2026, 12, 1, 7, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Timer_Null_IsImmediate()
    {
        var result = await _sut.EvaluateTimerAsync(Expr("null"), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.ScheduleType.ShouldBe(TimerScheduleType.Immediate);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("data.waitMinutes")]
    public async Task Timer_Number_IsRejected(string code)
    {
        var result = await _sut.EvaluateTimerAsync(Expr(code), Context());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("Numbers are rejected");
    }

    [Fact]
    public async Task Timer_UnparsableString_IsRejected()
    {
        var result = await _sut.EvaluateTimerAsync(Expr("data.plain"), Context());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("neither an ISO-8601 duration");
    }

    [Fact]
    public void ToTimerSchedule_DateTime_And_DateTimeOffset_AreAbsolute()
    {
        var utc = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        ValueSlotExpressionEvaluator.ToTimerSchedule(utc).Value!.ScheduledDateTime.ShouldBe(utc);
        ValueSlotExpressionEvaluator.ToTimerSchedule(new DateTimeOffset(utc)).Value!.ScheduleType.ShouldBe(TimerScheduleType.DateTime);
        ValueSlotExpressionEvaluator.ToTimerSchedule(1.5).IsSuccess.ShouldBeFalse();
    }

    // ----- timer evaluator: routing + compatibility ------------------------------------------------------------------

    [Fact]
    public async Task TimerEvaluator_Expression_DoesNotTouchTheScriptEngine()
    {
        var engine = Substitute.For<IScriptEngine>();
        var evaluator = new ScriptTimerEvaluator(engine, NullLogger<ScriptTimerEvaluator>.Instance, _sut);

        var result = await evaluator.EvaluateAsync(Expr("\"PT1H\""), Context());

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Duration.ShouldBe(TimeSpan.FromHours(1));
        await engine.DidNotReceiveWithAnyArgs().CompileToInstanceAsync<ITimerMapping>(default(ScriptCode)!);
    }

    [Fact]
    public async Task TimerEvaluator_LegacyCSharpLabelledDynamicExpresso_StillRuns_WithOneWarning()
    {
        var engine = Substitute.For<IScriptEngine>();
        var mapping = Substitute.For<ITimerMapping>();
        mapping.Handler(Arg.Any<ScriptContext>()).Returns(TimerSchedule.FromDuration(TimeSpan.FromMinutes(7)));
        engine.CompileToInstanceAsync<ITimerMapping>(default(ScriptCode)!).ReturnsForAnyArgs(mapping);
        var logger = new CapturingLogger<ScriptTimerEvaluator>();
        var evaluator = new ScriptTimerEvaluator(engine, logger, _sut);

        var result = await evaluator.EvaluateAsync(
            Expr("public class T : ITimerMapping { public Task<TimerSchedule> Handler(ScriptContext c) => null; }"), Context());

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Duration.ShouldBe(TimeSpan.FromMinutes(7));
        logger.EventIds.ShouldBe([10274]);
    }

    [Fact]
    public async Task TimerEvaluator_FailingAsBoth_ReportsTheExpressionError()
    {
        var engine = Substitute.For<IScriptEngine>();
        engine.CompileToInstanceAsync<ITimerMapping>(default(ScriptCode)!)
            .ReturnsForAnyArgs<Task<ITimerMapping>>(_ => throw new InvalidOperationException("CS1002: ; expected"));
        var logger = new CapturingLogger<ScriptTimerEvaluator>();
        var evaluator = new ScriptTimerEvaluator(engine, logger, _sut);

        var result = await evaluator.EvaluateAsync(Expr("data.customer."), Context());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ValueSlotExpressionEvaluator.EvaluationFailedCode);
        result.Error.Message.ShouldNotContain("CS1002");
        logger.EventIds.ShouldNotContain(10274);
    }

    [Fact]
    public async Task TimerEvaluator_NonExpressionLocation_RunsTheScript()
    {
        var engine = Substitute.For<IScriptEngine>();
        var mapping = Substitute.For<ITimerMapping>();
        mapping.Handler(Arg.Any<ScriptContext>()).Returns(TimerSchedule.Immediate());
        engine.CompileToInstanceAsync<ITimerMapping>(default(ScriptCode)!).ReturnsForAnyArgs(mapping);
        var evaluator = new ScriptTimerEvaluator(engine, NullLogger<ScriptTimerEvaluator>.Instance, _sut);

        var result = await evaluator.EvaluateAsync(ScriptCode.FromNative("\"PT1H\"", "./src/Timer.csx"), Context());

        result.Value!.ScheduleType.ShouldBe(TimerScheduleType.Immediate);
        await engine.ReceivedWithAnyArgs(1).CompileToInstanceAsync<ITimerMapping>(default(ScriptCode)!);
    }

    // ----- agent prompt / decision -------------------------------------------------------------------------------------

    [Fact]
    public async Task Prompt_Expression_ReturnsTheText()
    {
        var result = await _sut.EvaluatePromptAsync(Expr("\"Summarise the risk of \" + data.customer.name + \".\""), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe("Summarise the risk of Ada.");
    }

    [Fact]
    public async Task Prompt_Null_Fails()
    {
        var result = await _sut.EvaluatePromptAsync(Expr("null"), Context());

        result.IsSuccess.ShouldBeFalse();
    }

    private static readonly IReadOnlyList<AgentCandidate> Candidates =
    [
        new("approve", "the risk is acceptable", "hint"),
        new("reject", "the risk is too high", "description")
    ];

    private static AgentRunOutcome Outcome(string outputJson, string? decision, string? reason = "because") =>
        new(true, JsonDocument.Parse(outputJson).RootElement, decision, reason, null);

    [Theory]
    [InlineData("outcome.decision", "approve")]
    [InlineData("outcome.output.score.AsInt32() > 5 ? \"reject\" : \"approve\"", "reject")]
    [InlineData("candidates.has(outcome.decision) ? outcome.decision : null", "approve")]
    [InlineData("candidates[1].key", "reject")]
    [InlineData("candidates.find(\"approve\").whenSource", "hint")]
    [InlineData("candidates.count == 2 && outcome.completed ? candidates.keys[0] : null", "approve")]
    [InlineData("outcome.text.Contains(\"score\") ? \"reject\" : null", "reject")]
    [InlineData("outcome.reason == \"because\" && data.customer.name.ToString() == \"Ada\" ? \"approve\" : null", "approve")]
    public async Task Decision_SeesOutcomeAndCandidates(string code, string expected)
    {
        var result = await _sut.EvaluateDecisionAsync(Expr(code), Context(), Outcome("""{"score":9}""", "approve"), Candidates);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Decision_Null_IsNoDecision()
    {
        var result = await _sut.EvaluateDecisionAsync(Expr("null"), Context(), Outcome("{}", null), Candidates);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Decision_NonText_Fails()
    {
        var result = await _sut.EvaluateDecisionAsync(Expr("42"), Context(), Outcome("{}", null), Candidates);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("transition key");
    }

    [Fact]
    public async Task Fallback_ScriptSucceeds_ReturnsScriptValue_AndWarnsOnce()
    {
        var logger = new CapturingLogger<ValueSlotExpressionTests>();

        var result = await ValueSlotExpressionEvaluator.WithScriptFallbackAsync(
            () => _sut.EvaluateDecisionAsync(Expr("return \"approve\";"), Context(), Outcome("{}", null), Candidates),
            () => Task.FromResult<string?>("approve"),
            "agent decision",
            logger);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("approve");
        logger.EventIds.ShouldBe([10274]);
    }

    [Fact]
    public async Task Fallback_BothFail_ReportsTheExpressionError()
    {
        var result = await ValueSlotExpressionEvaluator.WithScriptFallbackAsync(
            () => _sut.EvaluatePromptAsync(Expr("data.customer."), Context()),
            () => throw new InvalidOperationException("CS1002"),
            "agent prompt",
            NullLogger.Instance);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldStartWith("Dynamic Expresso agent prompt expression failed");
    }

    [Fact]
    public async Task Fallback_ExpressionSucceeds_DoesNotRunTheScript()
    {
        var ran = false;
        var result = await ValueSlotExpressionEvaluator.WithScriptFallbackAsync(
            () => _sut.EvaluatePromptAsync(Expr("\"hi\""), Context()),
            () => { ran = true; return Task.FromResult("script"); },
            "agent prompt",
            NullLogger.Instance);

        result.Value.ShouldBe("hi");
        ran.ShouldBeFalse();
    }

    // ----- publish: warning, never an error ----------------------------------------------------------------------------

    [Theory]
    [InlineData(ValueSlotKind.Timer, "TimeSpan.FromMinutes(5.0)", true)]
    [InlineData(ValueSlotKind.Timer, "return TimerSchedule.Immediate();", false)]
    [InlineData(ValueSlotKind.AgentPrompt, "\"hello \" + data.name", true)]
    [InlineData(ValueSlotKind.AgentDecision, "candidates.has(outcome.decision) ? outcome.decision : null", true)]
    [InlineData(ValueSlotKind.AgentDecision, "task.x", false)]
    public void CompileError_MatchesTheSlotSignature(ValueSlotKind kind, string code, bool compiles)
    {
        (ValueSlotExpressionEvaluator.CompileError(new ExpressionCompiler(16), Expr(code), kind) is null).ShouldBe(compiles);
    }

    [Fact]
    public void Publish_TimerLabelledDynamicExpresso_ThatIsCSharp_IsAWarning_NotAnError()
    {
        static string Timer(string code) =>
            JsonSerializer.Serialize(new { location = "dynamicExpresso", code, encoding = "NAT" });
        var flow = JsonDocument.Parse($$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "wait", "stateType": "initial",
                    "labels": [{"label": "Wait", "language": "en"}],
                    "transitions": [
                        { "key": "late", "target": "done", "triggerType": "scheduled",
                          "labels": [{"label": "Late", "language": "en"}],
                          "timer": {{Timer("public class T : ITimerMapping { }")}} },
                        { "key": "soon", "target": "done", "triggerType": "scheduled",
                          "labels": [{"label": "Soon", "language": "en"}],
                          "timer": {{Timer("\"PT15M\"")}} }
                    ]
                },
                { "key": "done", "stateType": "finish", "labels": [{"label": "Done", "language": "en"}], "transitions": [] }
            ],
            "startTransition": { "key": "start", "target": "wait", "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}] }
        }
        """).RootElement;
        var result = new ComponentValidationResult();

        new MappingBlockValidator(new ExpressionCompiler(16)).Validate(RuntimeSysSchemaInfo.Flows, flow, result);

        result.ValidationErrors.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ErrorMessage!.ShouldStartWith("states[wait].transitions[late].timer: ");
    }

    /// <summary>Records the event ids of every log entry.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => EventIds.Add(eventId.Id);
    }
}
