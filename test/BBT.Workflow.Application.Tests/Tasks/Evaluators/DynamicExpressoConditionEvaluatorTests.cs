using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

public sealed class DynamicExpressoConditionEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_WhenAmountGreaterThanThreshold_ShouldReturnTrue()
    {
        var script = ScriptCode.FromNative(
            "context.Body[\"amount\"].AsDouble() > 100000",
            ConditionScriptLocations.DynamicExpresso);

        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object> { ["amount"] = 150000 })
            .Build();

        var evaluator = new DynamicExpressoConditionEvaluator(NullLogger<DynamicExpressoConditionEvaluator>.Instance);
        var result = await evaluator.EvaluateAsync(script, context, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WhenDocumentsEmpty_ShouldReturnTrue()
    {
        var script = ScriptCode.FromNative(
            "context.Body[\"documents\"].AsArrayLength() == 0",
            ConditionScriptLocations.DynamicExpresso);

        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object> { ["documents"] = Array.Empty<object>() })
            .Build();

        var evaluator = new DynamicExpressoConditionEvaluator(NullLogger<DynamicExpressoConditionEvaluator>.Instance);
        var result = await evaluator.EvaluateAsync(script, context, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WhenFlagFalse_ShouldReturnTrue()
    {
        var script = ScriptCode.FromNative(
            "context.Body[\"flags\"][\"manualReviewRequired\"].AsBoolean() == false",
            ConditionScriptLocations.DynamicExpresso);

        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object>
            {
                ["flags"] = new Dictionary<string, object> { ["manualReviewRequired"] = false }
            })
            .Build();

        var evaluator = new DynamicExpressoConditionEvaluator(NullLogger<DynamicExpressoConditionEvaluator>.Instance);
        var result = await evaluator.EvaluateAsync(script, context, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WhenApproversContainsUser_ShouldReturnTrue()
    {
        var script = ScriptCode.FromNative(
            "context.Body[\"approvers\"].Contains(\"u1\")",
            ConditionScriptLocations.DynamicExpresso);

        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object>
            {
                ["approvers"] = new[] { "a", "u1", "b" }
            })
            .Build();

        var evaluator = new DynamicExpressoConditionEvaluator(NullLogger<DynamicExpressoConditionEvaluator>.Instance);
        var result = await evaluator.EvaluateAsync(script, context, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WhenExpressionInvalid_ShouldFail()
    {
        var script = ScriptCode.FromNative(
            "this is not valid",
            ConditionScriptLocations.DynamicExpresso);

        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var evaluator = new DynamicExpressoConditionEvaluator(NullLogger<DynamicExpressoConditionEvaluator>.Instance);
        var result = await evaluator.EvaluateAsync(script, context, default);

        result.IsSuccess.ShouldBeFalse();
    }
}
