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

    /// <summary>
    /// Functions of the mapping-phase signatures: <c>urlEncode(text)</c> percent-encodes one URL component
    /// (<see cref="Uri.EscapeDataString(string)"/>; null stays null), for a value spliced into <c>$.url</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Delegate> MappingFunctions = new Dictionary<string, Delegate>
    {
        ["urlEncode"] = (Func<object?, string?>)UrlEncode
    };

    private static readonly ExpressionSignature MappingCondition = BuildMapping("mapping-condition", phase: null);
    private static readonly ExpressionSignature MappingInput = BuildMapping("mapping-input", ScopeKind.MappingInput);
    private static readonly ExpressionSignature MappingOutput = BuildMapping("mapping-output", ScopeKind.MappingOutput);

    /// <summary>
    /// Declarative mapping / condition expressions bound by <see cref="ExpressionScope"/>, in this order:
    /// <c>data</c>, <c>input</c> (<see cref="RuleJsonDynamic"/>), <c>instance</c> (<see cref="ExpressionInstance"/>, camelCase members),
    /// <c>now</c> (<see cref="DateTimeOffset"/>), <c>user</c> (<see cref="ExpressionUser"/>), <c>context</c>
    /// (<see cref="ExpressoRuleContext"/>); in both mapping phases also <c>task</c> (<see cref="RuleJsonDynamic"/>, the task's
    /// <c>config</c>), <c>config</c> (<see cref="ExpressionConfig"/>) and the <c>urlEncode</c> function; plus <c>result</c>
    /// (<see cref="RuleJsonDynamic"/>) for <see cref="ScopeKind.MappingOutput"/> only. <see cref="object"/> out, assignment
    /// operators disabled (<see cref="AssignmentOperators.None"/>). Each kind has its own
    /// signature name, so the compile cache never mixes them.
    /// </summary>
    public static ExpressionSignature Mapping(ScopeKind kind) => kind switch
    {
        ScopeKind.Condition => MappingCondition,
        ScopeKind.MappingInput => MappingInput,
        ScopeKind.MappingOutput => MappingOutput,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static ExpressionSignature BuildMapping(string name, ScopeKind? phase)
    {
        List<Parameter> parameters =
        [
            new(ExpressionScope.DataVariable, typeof(RuleJsonDynamic)),
            new(ExpressionScope.InputVariable, typeof(RuleJsonDynamic)),
            new(ExpressionScope.InstanceVariable, typeof(ExpressionInstance)),
            new(ExpressionScope.NowVariable, typeof(DateTimeOffset)),
            new(ExpressionScope.UserVariable, typeof(ExpressionUser)),
            new(ExpressionScope.ContextVariable, typeof(ExpressoRuleContext))
        ];
        if (phase is not null)
        {
            parameters.Add(new Parameter(ExpressionScope.TaskVariable, typeof(RuleJsonDynamic)));
            parameters.Add(new Parameter(ExpressionScope.ConfigVariable, typeof(ExpressionConfig)));
        }

        if (phase == ScopeKind.MappingOutput)
            parameters.Add(new Parameter(ExpressionScope.ResultVariable, typeof(RuleJsonDynamic)));

        // Assignment is off: `data.x = 1` would write through a borrowed view instead of producing a value.
        return new ExpressionSignature(
            name, parameters, typeof(object), phase is null ? NoFunctions : MappingFunctions, AssignmentOperators.None);
    }

    /// <summary>
    /// <c>urlEncode</c>: the value's text (a JSON string's content, any other value's <c>ToString()</c>) percent-encoded
    /// as one URL component; null stays null.
    /// </summary>
    internal static string? UrlEncode(object? value) => value switch
    {
        null => null,
        string text => Uri.EscapeDataString(text),
        _ => value.ToString() is { } text ? Uri.EscapeDataString(text) : null
    };
}
