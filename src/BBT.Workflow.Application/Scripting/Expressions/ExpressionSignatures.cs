using System.Dynamic;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using DynamicExpresso;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>Delegate of <see cref="ExpressionSignatures.Condition"/>: data, input, instance, now, user, context, headers → bool.</summary>
public delegate bool ConditionDelegate(
    RuleJsonDynamic? data, RuleJsonDynamic? input, ExpressionInstance? instance, DateTimeOffset now, ExpressionUser? user,
    ExpressoRuleContext? context, ExpressionHeaders? headers);

/// <summary>Delegate of <see cref="ExpressionSignatures.Key"/>: data, input, instance, now, user, context, headers → string.</summary>
public delegate string KeyDelegate(
    RuleJsonDynamic? data, RuleJsonDynamic? input, ExpressionInstance? instance, DateTimeOffset now, ExpressionUser? user,
    ExpressoRuleContext? context, ExpressionHeaders? headers);

/// <summary>The expression-slot families compiled through <see cref="IExpressionCompiler"/>.</summary>
public static class ExpressionSignatures
{
    private static readonly IReadOnlyDictionary<string, Delegate> NoFunctions = new Dictionary<string, Delegate>();

    /// <summary>
    /// <c>headers</c>: always the LAST variable of a scope signature (after <c>result</c> in the output phase), so adding it
    /// left every other variable's position unchanged. The agent tool <c>binding.code</c> (<see cref="AgentTool"/>) does not
    /// have it. Declared first: the static signatures below read it during type initialization.
    /// </summary>
    private static readonly Parameter HeadersParameter = new(ExpressionScope.HeadersVariable, typeof(ExpressionHeaders));

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
    /// Condition rules evaluated by <see cref="DynamicExpressoConditionEvaluator"/> (auto-transition, view, schema /
    /// contract, long-poll and state-notification <c>rule</c>): the condition-kind scope — <c>data</c>, <c>input</c>,
    /// <c>instance</c>, <c>now</c>, <c>user</c>, <c>context</c>, <c>headers</c> (the <see cref="ValueSlot"/> variables, so every legacy
    /// <c>context.*</c> expression compiles as before) — <see cref="bool"/> out, no functions, assignment disabled.
    /// Compile as <see cref="ConditionDelegate"/>. Replaces <see cref="LegacyCondition"/> at run time; its own name, so
    /// its compile-cache entries never mix with the legacy or value-slot ones.
    /// </summary>
    public static readonly ExpressionSignature Condition = new(
        "condition",
        [.. MappingVariables(), HeadersParameter],
        typeof(bool),
        NoFunctions,
        AssignmentOperators.None);

    /// <summary>
    /// String keys evaluated by <see cref="DynamicExpressoValueEvaluator"/> (CacheAside <c>key</c>, function cache
    /// <c>keyExpression</c> / <c>generationKeyExpression</c>, transition <c>resourceLock.keyExpression</c>): the
    /// <see cref="Condition"/> variables, <see cref="string"/> out, with the <c>sha256</c> and <c>varyKey</c> functions of
    /// <see cref="LegacyValue"/>, assignment disabled. Compile as <see cref="KeyDelegate"/>. Replaces
    /// <see cref="LegacyValue"/> at run time.
    /// </summary>
    public static readonly ExpressionSignature Key = new(
        "key",
        [.. MappingVariables(), HeadersParameter],
        typeof(string),
        LegacyValue.Functions,
        AssignmentOperators.None);

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
    /// Value slots outside task mappings (transition timer, workflow timeout, SubFlow timeout override, agent prompt):
    /// the condition-kind scope — <c>data</c>, <c>input</c>, <c>instance</c>, <c>now</c>, <c>user</c>, <c>context</c>,
    /// <c>headers</c> — <see cref="object"/> out, assignment disabled. The same signature as <see cref="Mapping"/>(<see cref="ScopeKind.Condition"/>).
    /// </summary>
    public static ExpressionSignature ValueSlot => MappingCondition;

    /// <summary>Name of the agent-run variable of <see cref="AgentDecision"/>.</summary>
    public const string OutcomeVariable = "outcome";

    /// <summary>Name of the candidate-list variable of <see cref="AgentDecision"/>.</summary>
    public const string CandidatesVariable = "candidates";

    /// <summary>
    /// Agent decision in mode <c>script</c>: the <see cref="ValueSlot"/> variables plus <c>outcome</c>
    /// (<see cref="ExpressionAgentOutcome"/>) and <c>candidates</c> (<see cref="ExpressionAgentCandidates"/>);
    /// <see cref="object"/> out (a transition key, or null for no decision), assignment disabled. Its own name, so its
    /// compile-cache entries never mix with the value slots'.
    /// </summary>
    public static readonly ExpressionSignature AgentDecision = new(
        "agent-decision",
        [
            .. MappingCondition.Parameters,
            new Parameter(OutcomeVariable, typeof(ExpressionAgentOutcome)),
            new Parameter(CandidatesVariable, typeof(ExpressionAgentCandidates))
        ],
        typeof(object),
        NoFunctions,
        AssignmentOperators.None);

    /// <summary>
    /// Declarative mapping / condition expressions bound by <see cref="ExpressionScope"/>, in this order:
    /// <c>data</c>, <c>input</c> (<see cref="RuleJsonDynamic"/>), <c>instance</c> (<see cref="ExpressionInstance"/>, camelCase members),
    /// <c>now</c> (<see cref="DateTimeOffset"/>), <c>user</c> (<see cref="ExpressionUser"/>), <c>context</c>
    /// (<see cref="ExpressoRuleContext"/>); in both mapping phases also <c>task</c> (<see cref="RuleJsonDynamic"/>, the task's
    /// <c>config</c>), <c>config</c> (<see cref="ExpressionConfig"/>) and the <c>urlEncode</c> function; plus <c>result</c>
    /// (<see cref="RuleJsonDynamic"/>) for <see cref="ScopeKind.MappingOutput"/> only; and last, in every kind, <c>headers</c>
    /// (<see cref="ExpressionHeaders"/>, the incoming request's headers). <see cref="object"/> out, assignment
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


    /// <summary>The condition-kind variables before <c>headers</c>, in delegate order: data, input, instance, now, user, context.</summary>
    private static List<Parameter> MappingVariables() =>
    [
        new(ExpressionScope.DataVariable, typeof(RuleJsonDynamic)),
        new(ExpressionScope.InputVariable, typeof(RuleJsonDynamic)),
        new(ExpressionScope.InstanceVariable, typeof(ExpressionInstance)),
        new(ExpressionScope.NowVariable, typeof(DateTimeOffset)),
        new(ExpressionScope.UserVariable, typeof(ExpressionUser)),
        new(ExpressionScope.ContextVariable, typeof(ExpressoRuleContext))
    ];

    private static ExpressionSignature BuildMapping(string name, ScopeKind? phase)
    {
        var parameters = MappingVariables();
        if (phase is not null)
        {
            parameters.Add(new Parameter(ExpressionScope.TaskVariable, typeof(RuleJsonDynamic)));
            parameters.Add(new Parameter(ExpressionScope.ConfigVariable, typeof(ExpressionConfig)));
        }

        if (phase == ScopeKind.MappingOutput)
            parameters.Add(new Parameter(ExpressionScope.ResultVariable, typeof(RuleJsonDynamic)));

        parameters.Add(HeadersParameter);

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
