using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting.Expressions;

public sealed class ExpressionCompilerTests
{
    private static Func<ExpressoRuleContext, bool> Condition(ExpressionCompiler compiler, string text) =>
        compiler.Compile<Func<ExpressoRuleContext, bool>>(text, ExpressionSignatures.LegacyCondition).Value.Invoke;

    [Fact]
    public void Compile_SameTextTwice_ReturnsCachedDelegate()
    {
        var compiler = new ExpressionCompiler(16);

        var first = compiler.Compile<Func<ExpressoRuleContext, bool>>("true", ExpressionSignatures.LegacyCondition);
        var second = compiler.Compile<Func<ExpressoRuleContext, bool>>("true", ExpressionSignatures.LegacyCondition);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        ReferenceEquals(first.Value.Invoke, second.Value.Invoke).ShouldBeTrue();
        compiler.CacheCount.ShouldBe(1);
    }

    [Fact]
    public void Cache_ExceedsCapacity_EvictsLeastRecentlyUsed()
    {
        var compiler = new ExpressionCompiler(2);
        Func<ExpressoRuleContext, bool> Get(string t) => Condition(compiler, t);

        var a = Get("1 == 1");
        Get("2 == 2");
        Get("3 == 3");

        compiler.CacheCount.ShouldBe(2);
        ReferenceEquals(Get("1 == 1"), a).ShouldBeFalse();
    }

    [Fact]
    public void Cache_RecentlyUsedEntry_SurvivesEviction()
    {
        var compiler = new ExpressionCompiler(2);
        var a = Condition(compiler, "1 == 1");
        Condition(compiler, "2 == 2");
        Condition(compiler, "1 == 1"); // touch A
        Condition(compiler, "3 == 3"); // evicts B

        ReferenceEquals(Condition(compiler, "1 == 1"), a).ShouldBeTrue();
    }

    [Fact]
    public void Compile_InvalidText_FailsAndIsNotCached()
    {
        var compiler = new ExpressionCompiler(4);

        var result = compiler.Compile<Func<ExpressoRuleContext, bool>>("context.Nope(", ExpressionSignatures.LegacyCondition);

        result.IsSuccess.ShouldBeFalse();
        compiler.CacheCount.ShouldBe(0);
    }

    [Fact]
    public void Compile_SameTextDifferentSignature_CachesSeparately()
    {
        var compiler = new ExpressionCompiler(4);
        compiler.Compile<Func<ExpressoRuleContext, bool>>("true", ExpressionSignatures.LegacyCondition);
        compiler.Compile<Func<ExpressoRuleContext, string>>("\"x\"", ExpressionSignatures.LegacyValue);
        compiler.Compile<Func<ExpressoRuleContext, string>>("\"x\"", ExpressionSignatures.LegacyValue);

        compiler.CacheCount.ShouldBe(2);
    }

    [Fact]
    public void Compile_UsedIdentifiers_ListsReferencedParametersAndFunctions()
    {
        var compiler = new ExpressionCompiler(4);

        var result = compiler.Compile<Func<ExpressoRuleContext, string>>(
            "sha256(context.Instance.Key)", ExpressionSignatures.LegacyValue);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UsedIdentifiers.ShouldContain("context");
        result.Value.UsedIdentifiers.ShouldContain("sha256");
        result.Value.UsedIdentifiers.ShouldNotContain("varyKey");
    }

    [Theory]
    [InlineData("context.Body[\"amount\"].AsDouble() > 100000", true)]
    [InlineData("context.Body[\"documents\"].AsArrayLength() == 0", false)]
    [InlineData("context.Body[\"flags\"][\"manualReviewRequired\"].AsBoolean() == false", true)]
    [InlineData("context.Body[\"approvers\"].Contains(\"u1\")", true)]
    public async Task LegacyCondition_ContextExpression_EvaluatesAsBefore(string expression, bool expected)
    {
        var script = ScriptCode.FromNative(expression, ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object>
            {
                ["amount"] = 150000,
                ["documents"] = new object[] { 1 },
                ["flags"] = new Dictionary<string, object> { ["manualReviewRequired"] = false },
                ["approvers"] = new[] { "u1", "u2" }
            })
            .Build();

        var evaluator = new DynamicExpressoConditionEvaluator(
            NullLogger<DynamicExpressoConditionEvaluator>.Instance, new ExpressionCompiler(16));
        var result = await evaluator.EvaluateAsync(script, context, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }
}
