using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;
using BBT.Workflow.Tasks.Executors;
using DynamicExpresso;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Configuration;
using InputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Expressions.ExpressionConfig,
    BBT.Workflow.Scripting.Expressions.ExpressionHeaders, object>;
using OutputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Expressions.ExpressionConfig,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Expressions.ExpressionHeaders, object>;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Runs a binding's declarative <c>input</c> / <c>output</c> blocks (<see cref="MappingBlock"/>): <c>select</c> +
/// <c>into</c> first, then the <c>set</c> entries in declaration order, each value a Dynamic Expresso expression over
/// <c>data</c>, <c>input</c>, <c>instance</c>, <c>now</c>, <c>user</c>, <c>context</c>, <c>task</c>, <c>config</c> (and
/// <c>result</c> in the output phase) bound lazily by <see cref="ExpressionScope"/>. No Roslyn compile is involved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input.</b> The targets are paths into the task's own <c>config</c> JSON — the object authored in the task
/// definition (<c>$.url</c>, <c>$.body.customer</c>, <c>$.headers.x-channel</c>, <c>$.timeoutSeconds</c>), so every field
/// a C# mapping could change through a setter is reachable without a per-type surface. The block writes into a copy of
/// <see cref="WorkflowTask.Config"/> (fields it does not touch keep their configured value), the copy goes back through
/// <see cref="WorkflowTask.ApplyConfig"/>, i.e. the task type's own <c>Configure</c> parser, and a value that parser
/// rejects fails the mapping. A path INTO a field whose configured value is not an object fails instead of replacing it;
/// a <c>headers</c> object's values are written as strings. A field name the task type does not read is ignored, as it
/// would be in the task definition (the binding's task type is not resolvable at publish, and the field set is the
/// type's parser). An input <c>select</c> reads the variable scope: its first segment names the variable. The returned
/// <see cref="ScriptResponse"/> is empty — the values are not echoed into the task journal.
/// </para>
/// <para>
/// <b>Output.</b> One <see cref="JsonObject"/> delta is built and returned as the output <c>Data</c>, so the engine's
/// single <c>AppendAsync</c> (merge, versioning, ETag, <c>x-encryption</c>) is unchanged. <c>select</c> reads
/// <c>result</c> (<c>{ body, headers, statusCode }</c>; root <c>$</c> is <c>result</c>, default <c>$.body</c>);
/// without <c>into</c> the selected object is merged at the data root. A missing source writes nothing (RF-1): a missing
/// <c>select</c> path, a null value, and a member read through a missing intermediate (<c>data.customer.id</c> with no
/// <c>customer</c> — the binder's null-reference failure, or a member/index a borrowed view cannot apply). A delta with
/// nothing in it returns null (no empty data version). Each skipped <c>select</c> or <c>set</c> entry raises the Debug
/// <c>TaskDeclarativeMappingEntrySkipped</c> with its path or key (never the value).
/// </para>
/// <para>
/// <b>Lifetime.</b> Every value is materialized into an owned <see cref="JsonNode"/> before the scope's borrowed
/// documents (the <see cref="TaskResultView"/>'s documents, a <see cref="JsonElement"/> input) go away. <b>Failures</b> — a caller-role provider
/// failure (<c>Authorization:110006</c>, transient), a role read on a hop with no caller (a non-transient mapping failure
/// telling the author to guard with <c>user.available</c>), any other exception an expression throws,
/// a path that cannot be written, a response <c>Data</c> that cannot be serialized — fail the mapping as a <see cref="Result"/>,
/// never "write nothing", and raise <c>TaskDeclarativeMappingFailed</c>. Cancellation of the mapping's own token
/// propagates. Compiled plans are memoized per binding on the
/// <see cref="TaskExecutorContext"/>; their delegates come from the shared compiler and are safe across parallel branches.
/// </para>
/// </remarks>
/// <param name="compiler">The shared expression compiler.</param>
/// <param name="clock">Source of <c>now</c>.</param>
/// <param name="configuration">Backs the <c>config</c> variable; an empty configuration when null.</param>
public sealed class DeclarativeTaskMappingRunner(
    IExpressionCompiler compiler, TimeProvider clock, IConfiguration? configuration = null) : ITaskMappingRunner
{
    private const string InputPhase = "input";
    private const string OutputPhase = "output";

    internal static readonly JsonPath DefaultOutputSelect = JsonPath.Parse("$.body").Value;

    /// <inheritdoc />
    public async Task<Result<ScriptResponse?>> PrepareInputAsync(
        WorkflowTask task, TaskExecutorContext ctx, CancellationToken ct, TaskMappingSite? site = null)
    {
        var block = ctx.OnExecuteTask.Input;
        if (block is null)
            return Result<ScriptResponse?>.Ok(null);

        site ??= TaskMappingSite.For(task.GetTaskType());
        var applied = await ApplyInputAsync(task, ctx, block, site, ct);
        if (!applied.IsSuccess)
        {
            Log(site, task, ctx, InputPhase, applied.Error);
            return Result<ScriptResponse?>.Fail(applied.Error);
        }

        return Result<ScriptResponse?>.Ok(new ScriptResponse());
    }

    /// <inheritdoc />
    public async Task<Result<object?>> ProcessOutputAsync(
        WorkflowTask task, TaskInvocationResult result, TaskExecutorContext ctx, CancellationToken ct,
        TaskMappingSite? site = null)
    {
        var block = ctx.OnExecuteTask.Output;
        if (block is null)
            return Result<object?>.Ok(result.Data);

        site ??= TaskMappingSite.For(task.GetTaskType());
        var delta = await BuildOutputAsync(task, ctx, block, result, site, ct);
        if (!delta.IsSuccess)
        {
            Log(site, task, ctx, OutputPhase, delta.Error);
            return Result<object?>.Fail(delta.Error);
        }

        return Result<object?>.Ok(delta.Value);
    }

    private async Task<Result> ApplyInputAsync(
        WorkflowTask task, TaskExecutorContext ctx, MappingBlock block, TaskMappingSite site, CancellationToken ct)
    {
        var planResult = GetOrBuildPlan(ctx, block, ScopeKind.MappingInput, site);
        if (!planResult.IsSuccess)
            return Result.Fail(planResult.Error);
        var plan = planResult.Value!;

        try
        {
            // An editable copy of the task's configuration; the target paths address it directly ($.url, $.body.x).
            var request = task.Config.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(task.Config.GetRawText())!.AsObject()
                : new JsonObject();
            foreach (var field in plan.PartiallyTargeted)
            {
                var prepared = PrepareFieldForPath(request, field);
                if (prepared is not null)
                    return Fail(site, InputPhase, prepared);
            }

            var scope = ExpressionScope.Create(ctx.ScriptContext, clock, ctx.Caller, ScopeKind.MappingInput)
                .WithTask(task.Config, configuration);
            if (plan.Into is not null || plan.Select is not null)
            {
                if (plan.Into is null || plan.SelectVariable is null)
                    return Fail(site, InputPhase, "an input 'select' and 'into' must be authored together.");

                var selected = await ReadInputSelectAsync(plan, scope, site);
                if (!selected.IsSuccess)
                    return Result.Fail(selected.Error);
                if (selected.Value is not null)
                    plan.Into.Set(request, selected.Value);
                else
                    LogSkipped(site, task, InputPhase, plan.Select!.Text, MissingSelectReason);
            }

            var set = await ApplySetAsync(task, plan, scope, request, site, InputPhase, ct);
            if (!set.IsSuccess)
                return set;

            if (request[HeadersField] is JsonObject headers)
                request[HeadersField] = HeadersAsStrings(headers);
            task.ApplyConfig(JsonSerializer.SerializeToElement(request));
        }
        catch (Exception ex) when (!IsCancellation(ex, ct))
        {
            return Fail(site, InputPhase, Unwrap(ex).Message);
        }

        return Result.Ok();
    }

    private async Task<Result<JsonObject?>> BuildOutputAsync(
        WorkflowTask task, TaskExecutorContext ctx, MappingBlock block, TaskInvocationResult result, TaskMappingSite site,
        CancellationToken ct)
    {
        var planResult = GetOrBuildPlan(ctx, block, ScopeKind.MappingOutput, site);
        if (!planResult.IsSuccess)
            return Result<JsonObject?>.Fail(planResult.Error);
        var plan = planResult.Value!;

        var delta = new JsonObject();
        try
        {
            // The response view `result` borrows from; disposed when the delta (owned nodes only) is built.
            using var resultView = plan.NeedsResult ? new TaskResultView(result) : null;
            var scope = ExpressionScope.Create(
                ctx.ScriptContext, clock, ctx.Caller, ScopeKind.MappingOutput,
                resultView is null ? null : RuleJsonDynamic.BorrowObject(resultView))
                .WithTask(task.Config, configuration);

            if (plan.ResultSelect is { } selector)
            {
                var source = plan.Select ?? DefaultOutputSelect;
                var selected = resultView!.TryRead(selector, out var element)
                    ? DeclarativeValues.ToNode(element)
                    : null;
                if (selected is not null)
                {
                    if (plan.Into is not null)
                    {
                        plan.Into.Set(delta, selected);
                    }
                    else if (selected is JsonObject selectedObject)
                    {
                        foreach (var (name, value) in selectedObject.ToArray())
                        {
                            selectedObject.Remove(name);
                            delta[name] = value;
                        }
                    }
                    else
                    {
                        return Result<JsonObject?>.Fail(FailureError(site, OutputPhase,
                            $"'select' {source.Text} without 'into' must select a JSON object to merge at the data root, " +
                            $"but selected a {selected.GetValueKind()}."));
                    }
                }
                else
                {
                    LogSkipped(site, task, OutputPhase, source.Text, MissingSelectReason);
                }
            }

            var set = await ApplySetAsync(task, plan, scope, delta, site, OutputPhase, ct);
            if (!set.IsSuccess)
                return Result<JsonObject?>.Fail(set.Error);
        }
        catch (Exception ex) when (!IsCancellation(ex, ct))
        {
            return Result<JsonObject?>.Fail(FailureError(site, OutputPhase, Unwrap(ex).Message));
        }

        return Result<JsonObject?>.Ok(delta.Count == 0 ? null : delta);
    }

    private static async Task<Result> ApplySetAsync(
        WorkflowTask task, DeclarativeMappingPlan plan, ExpressionScope scope, JsonObject root, TaskMappingSite site, string phase,
        CancellationToken ct)
    {
        foreach (var entry in plan.Set)
        {
            ct.ThrowIfCancellationRequested();
            var prepared = await scope.PrepareAsync(entry.UsedIdentifiers);
            if (!prepared.IsSuccess)
                return prepared; // caller roles could not be resolved: kept as its own (retryable) error

            JsonNode? value;
            try
            {
                var args = scope.Bind(plan.Parameters, entry.UsedIdentifiers);
                value = DeclarativeValues.ToNode(entry.Invoke(args));
            }
            catch (Exception ex) when (!IsCancellation(ex, ct))
            {
                var cause = Unwrap(ex);
                switch (cause)
                {
                    case RuntimeBinderException binder when IsMemberAccessOnNull(binder):
                        // RF-1: a member read through a missing intermediate (data.customer.id with no customer) is a
                        // missing source, not an error — the entry writes nothing.
                        LogSkipped(site, task, phase, $"set['{entry.Key}']", MissingIntermediateReason);
                        continue;
                    case CallerRolesUnavailableException:
                        // The expression read user.roles / user.hasRole on a hop with no caller (a System hop): a
                        // deterministic authoring error, not a transient one — retrying the same hop fails the same way.
                        // A provider failure never reaches here: PrepareAsync reports it (Authorization:110006).
                        return Result.Fail(FailureError(site, phase, $"set['{entry.Key}']: {NoCallerDetail}"));
                    default:
                        return Result.Fail(FailureError(site, phase, $"set['{entry.Key}']: {cause.Message}"));
                }
            }

            if (value is not null)
                entry.Target.Set(root, value);
        }

        return Result.Ok();
    }

    /// <summary>
    /// True for the binder's "Cannot perform runtime binding on a null reference": a member or index read on a value
    /// that is null — in a declarative scope, a path through a missing intermediate. Every other binder failure (a member
    /// that does not exist on a CLR type, a bad conversion) stays an error.
    /// </summary>
    private static bool IsMemberAccessOnNull(RuntimeBinderException exception) =>
        exception.Message.Contains("null reference", StringComparison.OrdinalIgnoreCase);

    /// <summary>A cancellation of this mapping's own token propagates; anything else becomes a Result failure.</summary>
    private static bool IsCancellation(Exception exception, CancellationToken ct) =>
        exception is OperationCanceledException && ct.IsCancellationRequested;

    private static async Task<Result<JsonNode?>> ReadInputSelectAsync(
        DeclarativeMappingPlan plan, ExpressionScope scope, TaskMappingSite site)
    {
        var variable = plan.SelectVariable!;
        var used = plan.SelectUsed!;
        var prepared = await scope.PrepareAsync(used);
        if (!prepared.IsSuccess)
            return Result<JsonNode?>.Fail(prepared.Error);

        var value = scope.Bind([variable], used)[0];
        if (value is ExpressionUser user)
        {
            // `user` is read member by member: `$.user.available` never needs roles, and a roles read with no caller is
            // the same deterministic failure as in a `set` expression (never an empty set, never a missing source).
            var tail = plan.SelectTail!;
            if (tail.FirstSegment == "available")
                return Result<JsonNode?>.Ok(tail.TryRead(new JsonObject { ["available"] = user.available }, out var flag) ? flag : null);
            if (!user.available)
                return Result<JsonNode?>.Fail(FailureError(site, InputPhase, $"'select' {plan.Select!.Text}: {NoCallerDetail}"));
            var userNode = new JsonObject
            {
                ["available"] = true,
                ["roles"] = new JsonArray(user.roles.Select(role => (JsonNode?)JsonValue.Create(role)).ToArray())
            };
            return Result<JsonNode?>.Ok(tail.TryRead(userNode, out var read) ? read : null);
        }

        if (value is RuleJsonDynamic dynamicValue)
        {
            return Result<JsonNode?>.Ok(plan.SelectTail!.TryRead(dynamicValue.AsJsonElement(), out var element)
                ? DeclarativeValues.ToNode(element)
                : null);
        }

        var node = DeclarativeValues.ToNode(value);
        return Result<JsonNode?>.Ok(node is not null && plan.SelectTail!.TryRead(node, out var found) ? found : null);
    }

    private Result<DeclarativeMappingPlan> GetOrBuildPlan(
        TaskExecutorContext ctx, MappingBlock block, ScopeKind kind, TaskMappingSite site)
    {
        var phase = kind == ScopeKind.MappingInput ? InputPhase : OutputPhase;
        ctx.CompiledDeclarativeMappings ??= new Dictionary<(MappingBlock Block, string Phase), object>(BlockPhaseComparer.Instance);
        if (ctx.CompiledDeclarativeMappings.TryGetValue((block, phase), out var memo))
            return Result<DeclarativeMappingPlan>.Ok((DeclarativeMappingPlan)memo);

        var built = DeclarativeMappingPlan.Build(compiler, block, kind);
        if (!built.IsSuccess)
            return Result<DeclarativeMappingPlan>.Fail(FailureError(site, phase, built.Error.Message ?? "invalid block."));

        ctx.CompiledDeclarativeMappings[(block, phase)] = built.Value!;
        return built;
    }

    private const string HeadersField = "headers";

    /// <summary>
    /// Makes <paramref name="field"/> writable for a path INSIDE it: absent or JSON null becomes an empty object, an
    /// object is kept; any other value returns an error — silently replacing an array or scalar with an object would
    /// lose it.
    /// </summary>
    private static string? PrepareFieldForPath(JsonObject request, string field)
    {
        if (!request.TryGetPropertyValue(field, out var current) || current is null)
        {
            request[field] = new JsonObject();
            return null;
        }

        return current is JsonObject
            ? null
            : $"the task's '{field}' is a JSON {current.GetValueKind()}, not an object, so a path inside $.{field} " +
              $"cannot be written; target $.{field} as a whole or configure an object {field}.";
    }

    /// <summary>A <c>headers</c> object with every value as a string (strings verbatim, others as their JSON text).</summary>
    private static JsonObject HeadersAsStrings(JsonObject headers)
    {
        var copy = new JsonObject();
        foreach (var (name, item) in headers)
        {
            copy[name] = item switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var text) => JsonValue.Create(text),
                _ => JsonValue.Create(item.ToJsonString())
            };
        }

        return copy;
    }

    /// <summary>The exception an expression threw, without the reflection wrapper a dynamic invoke adds.</summary>
    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static Result Fail(TaskMappingSite site, string phase, string detail) =>
        Result.Fail(FailureError(site, phase, detail));

    private static Error FailureError(TaskMappingSite site, string phase, string detail) =>
        Error.Failure(WorkflowErrorCodes.TaskExecution,
            $"{site.TaskTypeName} task declarative {phase} mapping failed: {detail}");

    private const string MissingSelectReason = "select path not found";
    private const string MissingIntermediateReason = "missing intermediate";

    private const string NoCallerDetail =
        "user.roles / user.hasRole was read on a hop with no caller (a System hop: a timer, an automatic chain, an event); " +
        "guard the read with user.available.";

    /// <summary>RF-1 skip at Debug: the task, phase and the path or set key — never the value.</summary>
    private static void LogSkipped(TaskMappingSite site, WorkflowTask task, string phase, string path, string reason) =>
        site.Logger?.TaskDeclarativeMappingEntrySkipped(task.Key, phase, path, reason);

    private static void Log(TaskMappingSite site, WorkflowTask task, TaskExecutorContext ctx, string phase, Error error) =>
        site.Logger?.TaskDeclarativeMappingFailed(
            task.Key,
            site.TaskTypeName,
            phase,
            ctx.ScriptContext.Instance?.Id ?? Guid.Empty,
            error.Message ?? error.Code);
}

/// <summary>Keys the per-execution plan memo by the binding's block instance (reference identity) and phase.</summary>
internal sealed class BlockPhaseComparer : IEqualityComparer<(MappingBlock Block, string Phase)>
{
    public static readonly BlockPhaseComparer Instance = new();

    public bool Equals((MappingBlock Block, string Phase) x, (MappingBlock Block, string Phase) y) =>
        ReferenceEquals(x.Block, y.Block) && string.Equals(x.Phase, y.Phase, StringComparison.Ordinal);

    public int GetHashCode((MappingBlock Block, string Phase) obj) =>
        HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Block), obj.Phase);
}

/// <summary>
/// One <see cref="MappingBlock"/> compiled for one phase: parsed paths, the task-config fields written inside and every <c>set</c>
/// expression compiled through the shared <see cref="IExpressionCompiler"/>. Immutable once built.
/// </summary>
internal sealed class DeclarativeMappingPlan
{
    private DeclarativeMappingPlan(
        IReadOnlyList<Parameter> parameters, JsonPath? select, JsonPath? into, SetEntry[] set,
        bool needsResult, Parameter? selectVariable, JsonPath? selectTail, IReadOnlySet<string> partiallyTargeted,
        TaskResultView.Selector? resultSelect)
    {
        ResultSelect = resultSelect;
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

    /// <summary>The signature's parameters, in delegate order.</summary>
    public IReadOnlyList<Parameter> Parameters { get; }

    /// <summary>The <c>select</c> path (input: from the variable scope; output: from <c>result</c>).</summary>
    public JsonPath? Select { get; }

    /// <summary>The <c>into</c> path.</summary>
    public JsonPath? Into { get; }

    /// <summary>The <c>set</c> entries in declaration order.</summary>
    public SetEntry[] Set { get; }

    /// <summary>Input only: the task-config fields some target writes a path INSIDE of (not the field as a whole).</summary>
    public IReadOnlySet<string> PartiallyTargeted { get; }

    /// <summary>Output only: whether the <c>result</c> document must be built.</summary>
    public bool NeedsResult { get; }

    /// <summary>
    /// Output only: the <c>select</c> (or the default <c>$.body</c> when only <c>into</c> is authored) split at its first
    /// segment for <see cref="TaskResultView"/>; null when the block has neither.
    /// </summary>
    public TaskResultView.Selector? ResultSelect { get; }

    /// <summary>Input only: the variable an input <c>select</c> reads.</summary>
    public Parameter? SelectVariable { get; }

    /// <summary>Input only: the <c>select</c> path inside that variable.</summary>
    public JsonPath? SelectTail { get; }

    /// <summary>Input only: the used-identifier set naming <see cref="SelectVariable"/>.</summary>
    public IReadOnlySet<string>? SelectUsed { get; }

    /// <summary>One compiled <c>set</c> entry.</summary>
    internal sealed record SetEntry(
        string Key, JsonPath Target, Func<object?[], object?> Invoke, IReadOnlySet<string> UsedIdentifiers);

    public static Result<DeclarativeMappingPlan> Build(IExpressionCompiler compiler, MappingBlock block, ScopeKind kind)
    {
        var isInput = kind == ScopeKind.MappingInput;
        var signature = ExpressionSignatures.Mapping(kind);
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
            if (isInput)
            {
                selectVariable = signature.Parameters.FirstOrDefault(
                    p => p.Name == select.FirstSegment && IsSelectable(p.Name));
                if (selectVariable is null)
                    return Invalid($"input 'select' {select.Text} must start at a JSON variable ({string.Join(", ", SelectableInputVariables(signature))}).");
                selectTail = select.WithoutFirstSegment();
            }
        }

        JsonPath? into = null;
        if (block.Into is not null)
        {
            var target = Target(block.Into, "into", isInput, partial);
            if (!target.IsSuccess)
                return Result<DeclarativeMappingPlan>.Fail(target.Error);
            into = target.Value!;
        }

        var entries = new List<SetEntry>();
        var needsResult = !isInput && (select is not null || into is not null);
        foreach (var (key, expression) in block.Set ?? new Dictionary<string, string>())
        {
            var target = Target(key, $"set['{key}']", isInput, partial);
            if (!target.IsSuccess)
                return Result<DeclarativeMappingPlan>.Fail(target.Error);

            if (string.IsNullOrWhiteSpace(expression)
                || expression.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
                return Invalid($"set['{key}']: the expression is empty or longer than {ConditionScriptLocations.MaxDynamicExpressoExpressionLength} characters.");

            SetEntry entry;
            if (isInput)
            {
                var compiled = compiler.Compile<InputFn>(expression, signature);
                if (!compiled.IsSuccess)
                    return Invalid($"set['{key}']: the expression does not compile: {compiled.Error.Message}");
                var fn = compiled.Value!.Invoke;
                entry = new SetEntry(key, target.Value!,
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5],
                        (RuleJsonDynamic?)a[6], (ExpressionConfig?)a[7], (ExpressionHeaders?)a[8]),
                    compiled.Value.UsedIdentifiers);
            }
            else
            {
                var compiled = compiler.Compile<OutputFn>(expression, signature);
                if (!compiled.IsSuccess)
                    return Invalid($"set['{key}']: the expression does not compile: {compiled.Error.Message}");
                var fn = compiled.Value!.Invoke;
                entry = new SetEntry(key, target.Value!,
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5],
                        (RuleJsonDynamic?)a[6], (ExpressionConfig?)a[7], (RuleJsonDynamic?)a[8], (ExpressionHeaders?)a[9]),
                    compiled.Value.UsedIdentifiers);
                needsResult |= compiled.Value.UsedIdentifiers.Contains(ExpressionScope.ResultVariable);
            }

            entries.Add(entry);
        }

        return Result<DeclarativeMappingPlan>.Ok(new DeclarativeMappingPlan(
            signature.Parameters, select, into, entries.ToArray(), needsResult,
            selectVariable, selectTail, partial,
            !isInput && (select is not null || into is not null)
                ? TaskResultView.Selector.For(select ?? DeclarativeTaskMappingRunner.DefaultOutputSelect)
                : null));
    }

    /// <summary>
    /// The variables an input <c>select</c> may start at: every JSON-shaped one — not <c>config</c>, not <c>headers</c>
    /// (read a header with a <c>set</c> expression, <c>headers["x-channel"]</c>).
    /// </summary>
    internal static IEnumerable<string> SelectableInputVariables(ExpressionSignature signature) =>
        signature.Parameters.Select(p => p.Name).Where(IsSelectable);

    private static bool IsSelectable(string name) =>
        name is not (ExpressionScope.ConfigVariable or ExpressionScope.HeadersVariable);

    private static Result<JsonPath> Target(string text, string what, bool isInput, HashSet<string> partial)
    {
        var parsed = Parse(text, what);
        if (!parsed.IsSuccess)
            return parsed;
        var path = parsed.Value!;
        if (!path.IsWritable || path.FirstSegment.Length == 0)
            return Result<JsonPath>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution,
                $"{what} {text} is not a writable property path."));

        if (isInput && path.WithoutFirstSegment().Text != "$")
            partial.Add(path.FirstSegment);

        return parsed;
    }

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

/// <summary>
/// Turns what an expression returned into an owned <see cref="JsonNode"/> — never a view over a borrowed document — so
/// the value outlives the evaluation scope: a <see cref="RuleJsonDynamic"/> or <see cref="JsonElement"/> is copied from
/// its raw text, a <see cref="JsonNode"/> deep-cloned, anything else (strings, numbers, booleans, dates as the runtime's
/// normal ISO-8601 text, arrays) serialized with <see cref="JsonSerializerConstants.JsonOptions"/>. JSON null and
/// "missing" become null, which the runner skips.
/// </summary>
internal static class DeclarativeValues
{
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        RuleJsonDynamic dynamicValue => ToNode(dynamicValue.AsJsonElement()),
        JsonElement element => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : FromOwnedElement(CopySubtree(element)),
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), JsonSerializerConstants.JsonOptions)
    };

    /// <summary>
    /// A subtree-sized, owned copy of <paramref name="element"/>: its raw UTF-8 bytes parsed into a new, non-pooled
    /// document. Unlike <see cref="JsonElement.Clone"/> — a no-op for an element of a non-disposable document, such as
    /// the <c>JsonElement</c> an HTTP or Dapr invoker returns — the copy never references the source document, so a
    /// small selected value does not pin a large task response for the life of the delta.
    /// </summary>
    private static JsonElement CopySubtree(JsonElement element)
    {
        var reader = new Utf8JsonReader(JsonMarshal.GetRawUtf8Value(element));
        return JsonElement.ParseValue(ref reader);
    }

    /// <summary>
    /// The node <c>JsonNode.Parse(element.GetRawText())</c> would build — an object, array or value over the element —
    /// without the text round trip, over an element <see cref="CopySubtree"/> already made independent of its source.
    /// </summary>
    private static JsonNode? FromOwnedElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonObject.Create(element),
        JsonValueKind.Array => JsonArray.Create(element),
        _ => JsonValue.Create(element)
    };
}
