using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

/// <summary>
/// vnext#1268: a timer script that throws leaves its exception on the script context it ran with —
/// the hop takes it from there for the fault incident — and reports the same error as before.
/// </summary>
public sealed class ScriptTimerEvaluatorFailureCauseTests
{
    private sealed class ThrowingTimer(Exception exception) : ITimerMapping
    {
        public Task<TimerSchedule> Handler(ScriptContext context) => throw exception;
    }

    private static ScriptTimerEvaluator EvaluatorThrowing(Exception exception)
    {
        var engine = Substitute.For<IScriptEngine>();
        engine.CompileToInstanceAsync<ITimerMapping>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(), Arg.Any<IEnumerable<MetadataReference>>(),
                Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ITimerMapping>(new ThrowingTimer(exception)));
        return new ScriptTimerEvaluator(engine, NullLogger<ScriptTimerEvaluator>.Instance);
    }

    [Fact]
    public async Task EvaluateAsync_HandlerThrows_FailsAsBefore_AndLeavesTheCauseOnTheContext()
    {
        var thrown = new InvalidOperationException("the timer script has no window");
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await EvaluatorThrowing(thrown).EvaluateAsync(ScriptCode.FromNative("throw;"), context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Unexpected");
        result.Error.Message.ShouldBe(thrown.Message);
        context.FailureCause.ShouldBeSameAs(thrown);
    }

    [Fact]
    public async Task EvaluateAsync_HandlerIsCancelled_FailsAsBefore_ButLeavesNoCause()
    {
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = await EvaluatorThrowing(new OperationCanceledException())
            .EvaluateAsync(ScriptCode.FromNative("wait"), context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        context.FailureCause.ShouldBeNull("a cancellation the script sees is not its failure");
    }
}
