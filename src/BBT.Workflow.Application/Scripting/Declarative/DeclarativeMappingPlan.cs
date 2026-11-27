using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;
using BBT.Workflow.Tasks.Mapping;
using DynamicExpresso;
using ConditionFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Rules.RuleJsonDynamic?,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance?, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser?, BBT.Workflow.Scripting.Rules.ExpressoRuleContext?,
    BBT.Workflow.Scripting.Expressions.ExpressionHeaders?, object>;
using InputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Rules.RuleJsonDynamic?,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance?, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser?, BBT.Workflow.Scripting.Rules.ExpressoRuleContext?,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Expressions.ExpressionConfig?,
    BBT.Workflow.Scripting.Expressions.ExpressionHeaders?, object>;
using OutputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Rules.RuleJsonDynamic?,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance?, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser?, BBT.Workflow.Scripting.Rules.ExpressoRuleContext?,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Expressions.ExpressionConfig?,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Expressions.ExpressionHeaders?, object>;
using ResultFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Rules.RuleJsonDynamic?,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance?, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser?, BBT.Workflow.Scripting.Rules.ExpressoRuleContext?,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic?, BBT.Workflow.Scripting.Expressions.ExpressionHeaders?, object>;

namespace BBT.Workflow.Scripting.Declarative;

/// <summary>
/// One <see cref="MappingBlock"/> compiled for one <see cref="DeclarativeSiteKind"/>: parsed paths checked against the
/// site's target rule, the select source, and every <c>set</c> expression compiled through the shared
/// <see cref="IExpressionCompiler"/> against the site's signature (<see cref="ExpressionSignatures.Mapping"/>).
/// Immutable once built; safe to share across executions and threads.
/// </summary>
internal sealed class DeclarativeMappingPlan
{
    internal static readonly JsonPath DefaultTaskOutputSelect = JsonPath.Parse("$.body").Value!;
    internal static readonly JsonPath DefaultSubFlowOutputSelect = JsonPath.Parse("$.data").Value!;

    private static readonly string[] SubFlowInputRoots = ["attributes", "headers", "key", "tags"];

    private DeclarativeMappingPlan(
        DeclarativeSiteKind kind, IReadOnlyList<Parameter> parameters, JsonPath? select, JsonPath? into, SetEntry[] set,
        bool needsResult, Parameter? selectVariable, JsonPath? selectTail, IReadOnlySet<string> partiallyTargeted,
        JsonPath? resultSelect)
    {
        Kind = kind;
        ResultSelectPath = resultSelect;
        ResultSelect = kind == DeclarativeSiteKind.TaskOutput && resultSelect is not null
            ? TaskResultView.Selector.For(resultSelect)
            : null;
        PartiallyTargeted = partiallyTargeted;
        Parameters = parameters;
        Select = select;
        Into = into;
        Set = set;
        NeedsResult = needsResult;
        SelectVariable = selectVariable;
        SelectTail = selectTail;
        SelectUsed = selectVariable is null ? null : new HashSet<string>(StringComparer.Ordinal) { selectVariable.Name };
    }

    /// <summary>The site this plan was built for.</summary>
    public DeclarativeSiteKind Kind { get; }

    /// <summary>The signature's parameters, in delegate order.</summary>
    public IReadOnlyList<Parameter> Parameters { get; }

    /// <summary>The <c>select</c> path as authored (variable-prefixed, or under the result document).</summary>
    public JsonPath? Select { get; }

    /// <summary>The <c>into</c> path.</summary>
    public JsonPath? Into { get; }

    /// <summary>The <c>set</c> entries in declaration order.</summary>
    public IReadOnlyList<SetEntry> Set { get; }

    /// <summary>Task input only: the task-config fields some target writes a path INSIDE of (not the field as a whole).</summary>
    public IReadOnlySet<string> PartiallyTargeted { get; }

    /// <summary>Result sites only: whether the <c>result</c> document is read (by the select step or an expression).</summary>
    public bool NeedsResult { get; }

    /// <summary>
    /// Result sites only (<see cref="DeclarativeSiteKind.TaskOutput"/>, <see cref="DeclarativeSiteKind.SubFlowOutput"/>):
    /// the path read under <c>result</c> — the <c>select</c>, or the site's default when only <c>into</c> is authored;
    /// null when the block has neither.
    /// </summary>
    public JsonPath? ResultSelectPath { get; }

    /// <summary>Task output only: <see cref="ResultSelectPath"/> split at its first segment for <see cref="TaskResultView"/>.</summary>
    public TaskResultView.Selector? ResultSelect { get; }

    /// <summary>Variable-select sites only: the variable the <c>select</c> reads.</summary>
    public Parameter? SelectVariable { get; }

    /// <summary>Variable-select sites only: the <c>select</c> path inside that variable.</summary>
    public JsonPath? SelectTail { get; }

    /// <summary>Variable-select sites only: the used-identifier set naming <see cref="SelectVariable"/>.</summary>
    public IReadOnlySet<string>? SelectUsed { get; }

    /// <summary>
    /// True when the block reads the expression variable <paramref name="identifier"/> — in a <c>set</c> expression or
    /// as the variable a <c>select</c> reads. A site that resolves something ahead of time for one variable (the SubFlow
    /// output's caller-role warm-up for <c>user</c>) asks this instead of resolving for every block.
    /// </summary>
    public bool Reads(string identifier)
    {
        if (SelectVariable?.Name == identifier)
            return true;

        for (var i = 0; i < Set.Count; i++)
        {
            if (Set[i].UsedIdentifiers.Contains(identifier))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True for the input-style sites (<see cref="DeclarativeSiteKind.TaskInput"/>, <see cref="DeclarativeSiteKind.SubFlowInput"/>):
    /// <c>select</c> and <c>into</c> are authored together, never merged at the root.
    /// </summary>
    public bool IsInputStyle => Kind is DeclarativeSiteKind.TaskInput or DeclarativeSiteKind.SubFlowInput;

    /// <summary>One compiled <c>set</c> entry.</summary>
    internal sealed record SetEntry(
        string Key, JsonPath Target, Func<object?[], object?> Invoke, IReadOnlySet<string> UsedIdentifiers);

    /// <summary>The expression scope kind (variable list) of a site.</summary>
    public static ScopeKind ScopeOf(DeclarativeSiteKind kind) => kind switch
    {
        DeclarativeSiteKind.TaskInput => ScopeKind.MappingInput,
        DeclarativeSiteKind.TaskOutput => ScopeKind.MappingOutput,
        DeclarativeSiteKind.TransitionOutput => ScopeKind.TransitionOutput,
        DeclarativeSiteKind.SubFlowInput => ScopeKind.SubFlowInput,
        DeclarativeSiteKind.SubFlowOutput => ScopeKind.SubFlowOutput,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>
    /// Builds the plan of <paramref name="block"/> for <paramref name="kind"/>. Target rule: task input — a task-config
    /// field; task / transition / SubFlow output — an instance-data path; SubFlow input — first segment
    /// <c>attributes</c>, <c>headers</c>, <c>key</c> or <c>tags</c> (<c>$.key</c> / <c>$.tags</c> only as a whole).
    /// Select: task output — the task result view (default <c>$.body</c>); SubFlow output — a path under <c>result</c>
    /// (default <c>$.data</c>); the others — a variable-prefixed path (<c>$.data…</c>, <c>$.input…</c>).
    /// </summary>
    public static Result<DeclarativeMappingPlan> Build(IExpressionCompiler compiler, MappingBlock block, DeclarativeSiteKind kind)
    {
        var signature = ExpressionSignatures.Mapping(ScopeOf(kind));
        var readsResult = kind is DeclarativeSiteKind.TaskOutput or DeclarativeSiteKind.SubFlowOutput;
        var partial = new HashSet<string>(StringComparer.Ordinal);

        JsonPath? select = null;
        Parameter? selectVariable = null;
        JsonPath? selectTail = null;
        if (block.Select is not null)
        {
            var parsed = Parse(block.Select, "select");
            if (!parsed.IsSuccess)
                return Result<DeclarativeMappingPlan>.Fail(parsed.Error);
            select = parsed.Value!;
            if (!readsResult)
            {
                selectVariable = signature.Parameters.FirstOrDefault(
                    p => p.Name == select.FirstSegment && IsSelectable(p.Name));
                if (selectVariable is null)
                    return Invalid($"{SelectWord(kind)} {select.Text} must start at a JSON variable ({string.Join(", ", SelectableVariables(signature))}).");
                selectTail = select.WithoutFirstSegment();
            }
        }

        JsonPath? into = null;
        if (block.Into is not null)
        {
            var target = Target(block.Into, "into", kind, partial);
            if (!target.IsSuccess)
                return Result<DeclarativeMappingPlan>.Fail(target.Error);
            into = target.Value!;
        }

        // A SubFlow input must catch at publish what a task input only fails at run time: one half of a select/into pair.
        if (kind == DeclarativeSiteKind.SubFlowInput && (select is null) != (into is null))
        {
            return Invalid(select is null
                ? $"'into' {into!.Text} needs a 'select': a SubFlow input has no default source."
                : $"'select' {select.Text} needs an 'into' naming the start-request path it copies to.");
        }

        if (kind == DeclarativeSiteKind.TransitionOutput && into is not null && select is null)
            return Invalid($"'into' {into.Text} needs a 'select': a transition output has no default source.");

        var entries = new List<SetEntry>();
        var resultSelect = readsResult && (select is not null || into is not null)
            ? select ?? (kind == DeclarativeSiteKind.TaskOutput ? DefaultTaskOutputSelect : DefaultSubFlowOutputSelect)
            : null;
        var needsResult = resultSelect is not null;
        foreach (var (key, expression) in block.Set ?? new Dictionary<string, string>())
        {
            var target = Target(key, $"set['{key}']", kind, partial);
            if (!target.IsSuccess)
                return Result<DeclarativeMappingPlan>.Fail(target.Error);

            if (string.IsNullOrWhiteSpace(expression)
                || expression.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
                return Invalid($"set['{key}']: the expression is empty or longer than {ConditionScriptLocations.MaxDynamicExpressoExpressionLength} characters.");

            var compiled = Compile(compiler, expression, signature, kind);
            if (!compiled.IsSuccess)
                return Invalid($"set['{key}']: the expression does not compile: {compiled.Error.Message}");

            var (invoke, used) = compiled.Value;
            entries.Add(new SetEntry(key, target.Value!, invoke, used));
            needsResult |= readsResult && used.Contains(ExpressionScope.ResultVariable);
        }

        return Result<DeclarativeMappingPlan>.Ok(new DeclarativeMappingPlan(
            kind, signature.Parameters, select, into, entries.ToArray(), needsResult,
            selectVariable, selectTail, partial, resultSelect));
    }

    /// <summary>One expression compiled against the site's delegate shape, invoked through a positional argument array.</summary>
    private static Result<(Func<object?[], object?> Invoke, IReadOnlySet<string> Used)> Compile(
        IExpressionCompiler compiler, string expression, ExpressionSignature signature, DeclarativeSiteKind kind)
    {
        switch (kind)
        {
            case DeclarativeSiteKind.TaskInput:
            {
                var compiled = compiler.Compile<InputFn>(expression, signature);
                if (!compiled.IsSuccess)
                    return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Fail(compiled.Error);
                var fn = compiled.Value!.Invoke;
                return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Ok((
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5],
                        (RuleJsonDynamic?)a[6], (ExpressionConfig?)a[7], (ExpressionHeaders?)a[8]),
                    compiled.Value.UsedIdentifiers));
            }
            case DeclarativeSiteKind.TaskOutput:
            {
                var compiled = compiler.Compile<OutputFn>(expression, signature);
                if (!compiled.IsSuccess)
                    return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Fail(compiled.Error);
                var fn = compiled.Value!.Invoke;
                return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Ok((
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5],
                        (RuleJsonDynamic?)a[6], (ExpressionConfig?)a[7], (RuleJsonDynamic?)a[8], (ExpressionHeaders?)a[9]),
                    compiled.Value.UsedIdentifiers));
            }
            case DeclarativeSiteKind.SubFlowOutput:
            {
                var compiled = compiler.Compile<ResultFn>(expression, signature);
                if (!compiled.IsSuccess)
                    return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Fail(compiled.Error);
                var fn = compiled.Value!.Invoke;
                return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Ok((
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5],
                        (RuleJsonDynamic?)a[6], (ExpressionHeaders?)a[7]),
                    compiled.Value.UsedIdentifiers));
            }
            default:
            {
                var compiled = compiler.Compile<ConditionFn>(expression, signature);
                if (!compiled.IsSuccess)
                    return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Fail(compiled.Error);
                var fn = compiled.Value!.Invoke;
                return Result<(Func<object?[], object?>, IReadOnlySet<string>)>.Ok((
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5],
                        (ExpressionHeaders?)a[6]),
                    compiled.Value.UsedIdentifiers));
            }
        }
    }

    /// <summary>
    /// The variables a variable-prefixed <c>select</c> may start at: every JSON-shaped one — not <c>config</c>, not
    /// <c>headers</c> (read a header with a <c>set</c> expression, <c>headers["x-channel"]</c>).
    /// </summary>
    internal static IEnumerable<string> SelectableVariables(ExpressionSignature signature) =>
        signature.Parameters.Select(p => p.Name).Where(IsSelectable);

    private static bool IsSelectable(string name) =>
        name is not (ExpressionScope.ConfigVariable or ExpressionScope.HeadersVariable);

    // The task input wording is kept verbatim: it is what a task binding's failure message has always said.
    private static string SelectWord(DeclarativeSiteKind kind) =>
        kind == DeclarativeSiteKind.TaskInput ? "input 'select'" : "'select'";

    private static Result<JsonPath> Target(string text, string what, DeclarativeSiteKind kind, HashSet<string> partial)
    {
        var parsed = Parse(text, what);
        if (!parsed.IsSuccess)
            return parsed;
        var path = parsed.Value!;
        if (!path.IsWritable || path.FirstSegment.Length == 0)
            return Result<JsonPath>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution,
                $"{what} {text} is not a writable property path."));

        // Only the input sites ask whether the path is the first member as a whole (an output target needs no check,
        // and WithoutFirstSegment allocates).
        switch (kind)
        {
            case DeclarativeSiteKind.TaskInput when !IsWholeMember(path):
                partial.Add(path.FirstSegment);
                break;
            case DeclarativeSiteKind.SubFlowInput when Array.IndexOf(SubFlowInputRoots, path.FirstSegment) < 0:
                return Result<JsonPath>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution,
                    $"{what} {text} must start at one of the start request's members ({string.Join(", ", SubFlowInputRoots)})."));
            case DeclarativeSiteKind.SubFlowInput when path.FirstSegment is "key" or "tags" && !IsWholeMember(path):
                return Result<JsonPath>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution,
                    $"{what} {text} must address $.{path.FirstSegment} as a whole."));
            default:
                // Output sites and whole-member input targets need no further check.
                break;
        }

        return parsed;
    }

    private static bool IsWholeMember(JsonPath path) => path.WithoutFirstSegment().Text == "$";

    private static Result<JsonPath> Parse(string text, string what)
    {
        var parsed = JsonPath.Parse(text);
        return parsed.IsSuccess
            ? parsed
            : Result<JsonPath>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution,
                $"{what} {text} is not a valid JSON path: {parsed.Error.Message}"));
    }

    private static Result<DeclarativeMappingPlan> Invalid(string message) =>
        Result<DeclarativeMappingPlan>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution, message));
}
