using System.Collections.Generic;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

public sealed class DynamicExpressoValueEvaluatorTests
{
    private static DynamicExpressoValueEvaluator CreateEvaluator() =>
        new(NullLogger<DynamicExpressoValueEvaluator>.Instance);

    [Fact]
    public void Evaluate_LiteralExpression_ReturnsString()
    {
        var script = ScriptCode.FromNative("\"health:status\"", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("health:status");
    }

    [Fact]
    public void Evaluate_ComputesKeyFromContext()
    {
        var script = ScriptCode.FromNative(
            "\"customer:\" + context.Body[\"customerId\"].ToString() + \":profile\"",
            ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object> { ["customerId"] = "42" })
            .Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("customer:42:profile");
    }

    [Fact]
    public void Evaluate_WhenNotDynamicExpressoLocation_Fails()
    {
        var script = ScriptCode.FromNative("\"x\"");  // default location "inline"
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_WhenExpressionInvalid_Fails()
    {
        var script = ScriptCode.FromNative("this is not valid", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeFalse();
    }
}
