using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public class FanOutJoinEvaluatorTests
{
    private static FanOutItemResult Ok(int i) => new(i, i.ToString(), true, null, null, null, TimeSpan.Zero);
    private static FanOutItemResult Fail(int i) => new(i, i.ToString(), false, null, "Task:500", "boom", TimeSpan.Zero);

    [Theory]
    // policy, results (1=ok 0=fail), minSuccess, timedOut, expectedSuccess
    [InlineData(FanOutJoinPolicy.All,          "111", null, false, true)]
    [InlineData(FanOutJoinPolicy.All,          "101", null, false, false)]
    [InlineData(FanOutJoinPolicy.All,          "111", null, true,  false)]
    [InlineData(FanOutJoinPolicy.All,          "1",   null, false, true)]
    [InlineData(FanOutJoinPolicy.AllSettled,   "000", null, false, true)]
    [InlineData(FanOutJoinPolicy.AllSettled,   "101", null, true,  true)]
    [InlineData(FanOutJoinPolicy.Quorum,       "110", 2,    false, true)]
    [InlineData(FanOutJoinPolicy.Quorum,       "100", 2,    false, false)]
    [InlineData(FanOutJoinPolicy.Quorum,       "110", 2,    true,  true)]
    [InlineData(FanOutJoinPolicy.Quorum,       "100", 2,    true,  false)]
    [InlineData(FanOutJoinPolicy.FirstSuccess, "010", null, false, true)]
    [InlineData(FanOutJoinPolicy.FirstSuccess, "000", null, false, false)]
    [InlineData(FanOutJoinPolicy.FirstSuccess, "010", null, true,  true)]
    public void Evaluate_Should_Apply_Policy(
        FanOutJoinPolicy policy, string pattern, int? minSuccess, bool timedOut, bool expected)
    {
        var items = pattern.Select((c, i) => c == '1' ? Ok(i) : Fail(i)).ToList();

        var outcome = FanOutJoinEvaluator.Evaluate(policy, minSuccess, items, timedOut);

        outcome.IsSuccess.ShouldBe(expected);
        if (!expected) outcome.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Evaluate_Empty_Batch_Should_Succeed_Only_For_All_And_AllSettled()
    {
        // All / AllSettled succeed vacuously on an empty batch. Quorum and FirstSuccess are both
        // threshold policies (FirstSuccess is Quorum with minSuccess=1) and neither can be satisfied
        // by zero successes, so both fail - the two must agree here since they're the same rule.
        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.All, null, [], false).IsSuccess.ShouldBeTrue();
        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.AllSettled, null, [], false).IsSuccess.ShouldBeTrue();
        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.Quorum, 1, [], false).IsSuccess.ShouldBeFalse();
        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.FirstSuccess, null, [], false).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_Quorum_Exactly_At_MinSuccess_Should_Succeed()
    {
        var items = new List<FanOutItemResult> { Ok(0), Ok(1), Fail(2) };

        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.Quorum, 2, items, false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_Quorum_One_Below_MinSuccess_Should_Fail()
    {
        var items = new List<FanOutItemResult> { Ok(0), Fail(1), Fail(2) };

        var outcome = FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.Quorum, 2, items, false);

        outcome.IsSuccess.ShouldBeFalse();
        outcome.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Evaluate_All_Single_Item_Success_Should_Succeed()
    {
        var items = new List<FanOutItemResult> { Ok(0) };

        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.All, null, items, false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_ErrorMessage_Should_Mention_Policy_Name()
    {
        var items = new List<FanOutItemResult> { Ok(0), Fail(1) };

        var outcome = FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.All, null, items, false);

        outcome.ErrorMessage.ShouldContain("all");
    }
}
