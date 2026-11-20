using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Evaluation;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Evaluation;

/// <summary>
/// vnext#1268, the hop-level evaluators: an exception that escapes automatic-rule evaluation (not a
/// rule script that throws: that one counts as not satisfied) and a timer script that throws leave the
/// exception on the hop for the fault incident's stack trace — and report the same error as before.
/// </summary>
public sealed class ScriptFailureCauseTests
{
    private readonly ScriptContext _shared = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

    [Fact]
    public async Task AnExceptionEscapingRuleEvaluation_LeavesTheExceptionOnTheHop()
    {
        var conditions = Substitute.For<ITaskConditionService>();
        var thrown = new InvalidOperationException("the rule's script context could not be built");
        conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<bool>>>(_ => throw thrown);
        var evaluator = new AutoConditionEvaluator(
            conditions,
            Substitute.For<IScriptContextFactory>(),
            Substitute.For<IInstanceRepository>(),
            NullLogger<AutoConditionEvaluator>.Instance,
            Substitute.For<IRuntimeInfoProvider>());
        var rule = Transition.Create("to-approved", "review", "approved", TriggerType.Automatic, "Patch");
        rule.SetRule("./IsApprover.csx", "return true;");
        var context = Context();

        var result = await evaluator.EvaluateAsync(rule, context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message!.ShouldContain(thrown.Message);
        context.FailureCause.ShouldBeSameAs(thrown);
    }

    [Fact]
    public async Task RuleEvaluationThatIsCancelled_FailsAsBefore_ButLeavesNoCause()
    {
        var conditions = Substitute.For<ITaskConditionService>();
        conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<bool>>>(_ => throw new OperationCanceledException());
        var evaluator = new AutoConditionEvaluator(
            conditions,
            Substitute.For<IScriptContextFactory>(),
            Substitute.For<IInstanceRepository>(),
            NullLogger<AutoConditionEvaluator>.Instance,
            Substitute.For<IRuntimeInfoProvider>());
        var rule = Transition.Create("to-approved", "review", "approved", TriggerType.Automatic, "Patch");
        rule.SetRule("./IsApprover.csx", "return true;");
        var context = Context();

        var result = await evaluator.EvaluateAsync(rule, context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        context.FailureCause.ShouldBeNull("a cancellation the script sees is not its failure");
    }

    [Fact]
    public async Task RuleScriptThatThrows_IsNotSatisfied_AndLeavesNoCauseOnTheHop()
    {
        // The condition evaluator catches the rule script's exception, logs it (10270) and fails the
        // Result: the rule reads as not satisfied and the hop goes on. A cause left here would be taken
        // by a LATER, unrelated failure of the same hop as its stack trace.
        var conditions = Substitute.For<ITaskConditionService>();
        conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("Task:Execution", "the rule script threw")));
        var evaluator = new AutoConditionEvaluator(
            conditions,
            Substitute.For<IScriptContextFactory>(),
            Substitute.For<IInstanceRepository>(),
            NullLogger<AutoConditionEvaluator>.Instance,
            Substitute.For<IRuntimeInfoProvider>());
        var rule = Transition.Create("to-approved", "review", "approved", TriggerType.Automatic, "Patch");
        rule.SetRule("./IsApprover.csx", "return true;");
        var context = Context();

        var result = await evaluator.EvaluateAsync(rule, context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(AutoConditionStatus.NotSatisfied);
        context.FailureCause.ShouldBeNull();
        _shared.FailureCause.ShouldBeNull();
    }

    [Fact]
    public async Task TimerScriptThatThrows_MovesItsExceptionFromTheScriptContextToTheHop()
    {
        var thrown = new InvalidOperationException("the timer script has no window");
        var timers = Substitute.For<ITaskTimerService>();
        timers.ExecuteTimerAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<ScriptContext>().FailureCause = thrown;
                return Result<TimerSchedule>.Fail(Error.Failure("Unexpected", thrown.Message));
            });
        var context = Context();

        var result = await new CallerScopedTimerEvaluator(timers).EvaluateAsync(context, TimerTransition(), _shared);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Unexpected");
        context.FailureCause.ShouldBeSameAs(thrown);
        _shared.FailureCause.ShouldBeNull("taken, so it cannot reach the hop's task mappings on the shared context");
    }

    [Fact]
    public async Task TimerScriptThatSucceeds_LeavesNoCauseOnTheHop()
    {
        var timers = Substitute.For<ITaskTimerService>();
        timers.ExecuteTimerAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TimerSchedule>.Ok(TimerSchedule.FromDuration(TimeSpan.FromHours(1))));
        var context = Context();

        var result = await new CallerScopedTimerEvaluator(timers).EvaluateAsync(context, TimerTransition(), _shared);

        result.IsSuccess.ShouldBeTrue();
        context.FailureCause.ShouldBeNull();
    }

    private static Transition TimerTransition()
    {
        var transition = Transition.Create("expire", "wait", "expired", TriggerType.Scheduled, "Patch");
        transition.SetTimer("./Window.csx", "return null;");
        return transition;
    }

    private TransitionExecutionContext Context()
    {
        var context = new TransitionExecutionContext
        {
            InstanceId = Guid.NewGuid(),
            Domain = "d",
            WorkflowKey = "w",
            TransitionKey = "submit",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.System,
            CorrelationId = "c",
            ExecutionChainId = "e",
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, string?>(),
            TraceId = "t",
            SpanId = "s",
        };
        context.Cache["ScriptContext"] = _shared;
        return context;
    }
}
