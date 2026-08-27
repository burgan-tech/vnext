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
}
