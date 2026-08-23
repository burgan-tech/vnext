using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

public sealed class ScriptConditionEvaluatorMetricsTests
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
    public async Task EvaluateAsync_Success_RecordsExecutionDuration()
    {
        var mapping = new FakeConditionMapping(true, null);
        var engine = MockEngine(mapping);
        var metrics = Substitute.For<IWorkflowMetrics>();
        var evaluator = new ScriptConditionEvaluator(engine, NullLogger<ScriptConditionEvaluator>.Instance, metrics);

        var script = ScriptCode.FromNative("return true;");
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await evaluator.EvaluateAsync(script, context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeTrue();
        metrics.Received(1).RecordScriptExecutionDuration(
            "condition", "csharp", "success", Arg.Is<double>(d => d >= 0));
        metrics.DidNotReceiveWithAnyArgs().RecordScriptRuntimeError(default!, default!, default!);
    }

    [Fact]
    public async Task EvaluateAsync_HandlerThrows_RecordsRuntimeErrorAndFailsWithoutThrowing()
    {
        var mapping = new FakeConditionMapping(false, new InvalidOperationException("boom"));
        var engine = MockEngine(mapping);
        var metrics = Substitute.For<IWorkflowMetrics>();
        var evaluator = new ScriptConditionEvaluator(engine, NullLogger<ScriptConditionEvaluator>.Instance, metrics);

        var script = ScriptCode.FromNative("throw;");
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await evaluator.EvaluateAsync(script, context, CancellationToken.None);

        // TryAsync swallows the exception into a failed Result - existing behavior preserved.
        result.IsSuccess.ShouldBeFalse();
        metrics.Received(1).RecordScriptRuntimeError(
            "condition", "csharp", nameof(InvalidOperationException));
        metrics.DidNotReceiveWithAnyArgs().RecordScriptExecutionDuration(
            default!, default!, default!, default);
    }
}
