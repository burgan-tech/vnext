using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

/// <summary>
/// Behavior pins for <see cref="ScriptConditionEvaluator"/> (formerly the metrics tests — the
/// prometheus metrics were removed, the evaluation semantics they rode along stay pinned).
/// </summary>
public sealed class ScriptConditionEvaluatorBehaviorTests
{
    private sealed class FakeConditionMapping(bool result, Exception? throwOnHandle) : IConditionMapping
    {
        public Task<bool> Handler(ScriptContext context)
            => throwOnHandle is not null
                ? throw throwOnHandle
                : Task.FromResult(result);
    }

    private static IScriptEngine MockEngine(IConditionMapping mapping)
    {
        var engine = Substitute.For<IScriptEngine>();
        engine.CompileToInstanceAsync<IConditionMapping>(
                Arg.Any<ScriptCode>(),
                Arg.Any<ScriptSettings>(),
                Arg.Any<IEnumerable<MetadataReference>>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mapping));
        return engine;
    }

    [Fact]
    public async Task EvaluateAsync_Success_ReturnsHandlerVerdict()
    {
        var mapping = new FakeConditionMapping(true, null);
        var evaluator = new ScriptConditionEvaluator(
            MockEngine(mapping), NullLogger<ScriptConditionEvaluator>.Instance);

        var script = ScriptCode.FromNative("return true;");
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await evaluator.EvaluateAsync(script, context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_HandlerThrows_FailsWithoutThrowing()
    {
        var mapping = new FakeConditionMapping(false, new InvalidOperationException("boom"));
        var evaluator = new ScriptConditionEvaluator(
            MockEngine(mapping), NullLogger<ScriptConditionEvaluator>.Instance);

        var script = ScriptCode.FromNative("throw;");
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await evaluator.EvaluateAsync(script, context, CancellationToken.None);

        // TryAsync swallows the exception into a failed Result — existing behavior preserved.
        result.IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// vnext#1268: a failing automatic rule records no incident, so the log line carries the
    /// exception itself — type, message and stack trace — while the Result stays what it was.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_HandlerThrows_LogsTheException_AndLeavesNoCauseOnTheContext()
    {
        var thrown = new InvalidOperationException("boom");
        var logger = new CapturingLogger();
        var evaluator = new ScriptConditionEvaluator(MockEngine(new FakeConditionMapping(false, thrown)), logger);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await evaluator.EvaluateAsync(ScriptCode.FromNative("throw;"), context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldBe("boom");
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.EventId.ShouldBe(10270);
        entry.Exception.ShouldBeSameAs(thrown);
        context.FailureCause.ShouldBeNull("a failing rule is not satisfied; no incident takes its cause");
    }

    [Fact]
    public async Task EvaluateAsync_CompileFails_LogsTheCompileException()
    {
        var thrown = new InvalidOperationException("CS0103: the name 'missing' does not exist");
        var engine = Substitute.For<IScriptEngine>();
        engine.CompileToInstanceAsync<IConditionMapping>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(), Arg.Any<IEnumerable<MetadataReference>>(),
                Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IConditionMapping>>(_ => throw thrown);
        var logger = new CapturingLogger();
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await new ScriptConditionEvaluator(engine, logger)
            .EvaluateAsync(ScriptCode.FromNative("broken"), context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        logger.Entries.ShouldHaveSingleItem().Exception.ShouldBeSameAs(thrown);
    }

    [Fact]
    public async Task EvaluateAsync_Cancelled_IsNotLoggedAsAScriptFailure()
    {
        var logger = new CapturingLogger();
        var evaluator = new ScriptConditionEvaluator(
            MockEngine(new FakeConditionMapping(false, new OperationCanceledException())), logger);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await evaluator.EvaluateAsync(ScriptCode.FromNative("wait"), context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        logger.Entries.ShouldBeEmpty();
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<ScriptConditionEvaluator>
    {
        public List<(int EventId, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((eventId.Id, exception));
    }
}
