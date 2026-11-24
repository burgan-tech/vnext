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

    private static readonly ExpressionSignature MappingCondition = BuildMapping("mapping-condition", withResult: false);
    private static readonly ExpressionSignature MappingInput = BuildMapping("mapping-input", withResult: false);
    private static readonly ExpressionSignature MappingOutput = BuildMapping("mapping-output", withResult: true);

    /// <summary>
    /// Declarative mapping / condition expressions bound by <see cref="ExpressionScope"/>, in this order:
    /// <c>data</c>, <c>input</c> (<see cref="RuleJsonDynamic"/>), <c>instance</c> (<see cref="ExpressoInstanceView"/>),
    /// <c>now</c> (<see cref="DateTimeOffset"/>), <c>user</c> (<see cref="ExpressionUser"/>), <c>context</c>
    /// (<see cref="ExpressoRuleContext"/>), plus <c>result</c> (<see cref="RuleJsonDynamic"/>) for
    /// <see cref="ScopeKind.MappingOutput"/> only; <see cref="object"/> out, no functions, assignment operators disabled
    /// (<see cref="AssignmentOperators.None"/>). Each kind has its own
    /// signature name, so the compile cache never mixes them.
    /// </summary>
    public static ExpressionSignature Mapping(ScopeKind kind) => kind switch
    {
        ScopeKind.Condition => MappingCondition,
        ScopeKind.MappingInput => MappingInput,
        ScopeKind.MappingOutput => MappingOutput,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static ExpressionSignature BuildMapping(string name, bool withResult)
    {
        List<Parameter> parameters =
        [
            new(ExpressionScope.DataVariable, typeof(RuleJsonDynamic)),
            new(ExpressionScope.InputVariable, typeof(RuleJsonDynamic)),
            new(ExpressionScope.InstanceVariable, typeof(ExpressoInstanceView)),
            new(ExpressionScope.NowVariable, typeof(DateTimeOffset)),
            new(ExpressionScope.UserVariable, typeof(ExpressionUser)),
            new(ExpressionScope.ContextVariable, typeof(ExpressoRuleContext))
        ];
        if (withResult)
            parameters.Add(new Parameter(ExpressionScope.ResultVariable, typeof(RuleJsonDynamic)));

        // Assignment is off: `data.x = 1` would write through a borrowed view instead of producing a value.
        return new ExpressionSignature(name, parameters, typeof(object), NoFunctions, AssignmentOperators.None);
    }
}
