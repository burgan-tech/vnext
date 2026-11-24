using System.Dynamic;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using DynamicExpresso;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>The expression-slot families compiled through <see cref="IExpressionCompiler"/>.</summary>
public static class ExpressionSignatures
{
    private static readonly IReadOnlyDictionary<string, Delegate> NoFunctions = new Dictionary<string, Delegate>();

    /// <summary>
    /// Auto-transition and other condition rules: <c>context</c> (<see cref="ExpressoRuleContext"/>) in,
    /// <see cref="bool"/> out, no functions. Compile as <c>Func&lt;ExpressoRuleContext, bool&gt;</c>.
    /// </summary>
    public static readonly ExpressionSignature LegacyCondition = new(
        "legacy-condition",
        [new Parameter("context", typeof(ExpressoRuleContext))],
        typeof(bool),
        NoFunctions);

    /// <summary>
    /// String-valued expressions (cache keys): <c>context</c> in, <see cref="string"/> out, with the
    /// <c>sha256</c> and <c>varyKey</c> functions. Compile as <c>Func&lt;ExpressoRuleContext, string&gt;</c>.
    /// </summary>
    public static readonly ExpressionSignature LegacyValue = new(
        "legacy-value",
        [new Parameter("context", typeof(ExpressoRuleContext))],
        typeof(string),
        new Dictionary<string, Delegate>
        {
            ["sha256"] = (Func<string?, string>)DynamicExpressoValueEvaluator.Sha256Hex,
            ["varyKey"] = (Func<ExpressoRuleContext, string>)DynamicExpressoValueEvaluator.VaryKey
        });

    /// <summary>
    /// Agent inline-tool expressions: <c>args</c>, <c>data</c> (<see cref="ExpandoObject"/>) and <c>now</c>
    /// (<see cref="DateTime"/>) in, <see cref="object"/> out. Compile as
    /// <c>Func&lt;ExpandoObject, ExpandoObject, DateTime, object&gt;</c>.
    /// </summary>
    public static readonly ExpressionSignature AgentTool = new(
        "agent-tool",
        [
            new Parameter("args", typeof(ExpandoObject)),
            new Parameter("data", typeof(ExpandoObject)),
            new Parameter("now", typeof(DateTime))
        ],
        typeof(object),
        NoFunctions);
}
