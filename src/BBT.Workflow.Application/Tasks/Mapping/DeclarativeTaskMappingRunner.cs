using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;
using BBT.Workflow.Tasks.Executors;
using DynamicExpresso;
using InputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Rules.ExpressoInstanceView, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext, object>;
using OutputFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Rules.ExpressoInstanceView, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, object>;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Runs a binding's declarative <c>input</c> / <c>output</c> blocks (<see cref="MappingBlock"/>): <c>select</c> +
/// <c>into</c> first, then the <c>set</c> entries in declaration order, each value a Dynamic Expresso expression over
/// <c>data</c>, <c>input</c>, <c>instance</c>, <c>now</c>, <c>user</c>, <c>context</c> (and <c>result</c> in the output
/// phase) bound lazily by <see cref="ExpressionScope"/>. No Roslyn compile is involved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input.</b> Each surface a block targets (<c>$.body</c>, <c>$.headers</c>, <c>$.query</c>, <c>$.route</c>) starts from
/// the task's current value for it (its static configuration; an empty object when there is none or it is not an
/// object), is written in place, and goes back through the task's surface interface (<see cref="IHasBody"/>,
/// <see cref="IHasHeaders"/>, <see cref="IHasQuery"/>, <see cref="IHasRouteValues"/>), i.e. the task type's own setter.
/// A task type without the targeted surface fails the mapping with a clear error (publish cannot check it: a binding's
/// task type is not resolvable at publish). An input <c>select</c> reads the variable scope: its first segment names
/// the variable. The returned <see cref="ScriptResponse"/> is empty — the values are not echoed into the task journal.
/// </para>
/// <para>
/// <b>Output.</b> One <see cref="JsonObject"/> delta is built and returned as the output <c>Data</c>, so the engine's
/// single <c>AppendAsync</c> (merge, versioning, ETag, <c>x-encryption</c>) is unchanged. <c>select</c> reads
/// <c>result</c> (<c>{ body, headers, statusCode }</c>; root <c>$</c> is <c>result</c>, default <c>$.body</c>);
/// without <c>into</c> the selected object is merged at the data root. A missing source or a null value writes nothing;
/// a delta with nothing in it returns null (no empty data version).
/// </para>
/// <para>
/// <b>Lifetime.</b> Every value is materialized into an owned <see cref="JsonNode"/> before the scope's borrowed
/// documents (the result document, a <see cref="JsonElement"/> input) go away. <b>Failures</b> — a caller-role provider
/// failure for <c>user</c> (kept as its own retryable error), an expression that throws, a path that cannot be
/// written — fail the mapping as a <see cref="Result"/>, never "write nothing", and raise
/// <c>TaskDeclarativeMappingFailed</c>. Compiled plans are memoized per binding on the
/// <see cref="TaskExecutorContext"/>; their delegates come from the shared compiler and are safe across parallel branches.
/// </para>
/// </remarks>
public sealed class DeclarativeTaskMappingRunner(IExpressionCompiler compiler, TimeProvider clock) : ITaskMappingRunner
{
    private const string InputPhase = "input";
    private const string OutputPhase = "output";

    private static readonly JsonPath DefaultOutputSelect = JsonPath.Parse("$.body").Value;

    /// <inheritdoc />
    public async Task<Result<ScriptResponse?>> PrepareInputAsync(
        WorkflowTask task, TaskExecutorContext ctx, CancellationToken ct, TaskMappingSite? site = null)
    {
        var block = ctx.OnExecuteTask.Input;
        if (block is null)
            return Result<ScriptResponse?>.Ok(null);

        site ??= TaskMappingSite.For(task.GetTaskType());
        var applied = await ApplyInputAsync(task, ctx, block, site);
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
        var delta = await BuildOutputAsync(ctx, block, result, site);
        if (!delta.IsSuccess)
        {
            Log(site, task, ctx, OutputPhase, delta.Error);
            return Result<object?>.Fail(delta.Error);
        }

        return Result<object?>.Ok(delta.Value);
    }

    private async Task<Result> ApplyInputAsync(
        WorkflowTask task, TaskExecutorContext ctx, MappingBlock block, TaskMappingSite site)
    {
        var planResult = GetOrBuildPlan(ctx, block, ScopeKind.MappingInput, site);
        if (!planResult.IsSuccess)
            return Result.Fail(planResult.Error);
        var plan = planResult.Value!;

        // The request's surfaces, under the names the target paths start with ($.body.x → request["body"]["x"]).
        var request = new JsonObject();
        foreach (var surface in plan.Surfaces)
        {
            var loaded = LoadSurface(task, surface);
            if (loaded is null)
                return Fail(site, InputPhase,
                    $"task type {task.GetTaskType()} has no '{surface}' input surface; target only the surfaces it supports.");
            request[surface] = loaded;
        }

        var scope = ExpressionScope.Create(ctx.ScriptContext, clock, ctx.Caller, ScopeKind.MappingInput);
        try
        {
            if (plan.Into is not null || plan.Select is not null)
            {
                if (plan.Into is null || plan.SelectVariable is null)
                    return Fail(site, InputPhase, "an input 'select' and 'into' must be authored together.");

                var selected = await ReadInputSelectAsync(plan, scope);
                if (!selected.IsSuccess)
                    return Result.Fail(selected.Error);
                if (selected.Value is not null)
                    plan.Into.Set(request, selected.Value);
            }

            var set = await ApplySetAsync(plan, scope, request, site, InputPhase);
            if (!set.IsSuccess)
                return set;

            foreach (var surface in plan.Surfaces)
            {
                var written = WriteSurface(task, surface, request[surface]);
                if (written is not null)
                    return Fail(site, InputPhase, written);
            }
        }
        catch (Exception ex)
        {
            return Fail(site, InputPhase, Unwrap(ex).Message);
        }

        return Result.Ok();
    }

    private async Task<Result<JsonObject?>> BuildOutputAsync(
        TaskExecutorContext ctx, MappingBlock block, TaskInvocationResult result, TaskMappingSite site)
    {
        var planResult = GetOrBuildPlan(ctx, block, ScopeKind.MappingOutput, site);
        if (!planResult.IsSuccess)
            return Result<JsonObject?>.Fail(planResult.Error);
        var plan = planResult.Value!;

        // The response document `result` borrows from; disposed when the delta (owned nodes only) is built.
        using var resultDocument = plan.NeedsResult ? BuildResultDocument(result) : null;
        var scope = ExpressionScope.Create(
            ctx.ScriptContext, clock, ctx.Caller, ScopeKind.MappingOutput, resultDocument?.RootElement);
        var delta = new JsonObject();
        try
        {
            if (plan.Select is not null || plan.Into is not null)
            {
                var source = plan.Select ?? DefaultOutputSelect;
                var selected = source.TryRead(resultDocument!.RootElement, out var element)
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
            }

            var set = await ApplySetAsync(plan, scope, delta, site, OutputPhase);
            if (!set.IsSuccess)
                return Result<JsonObject?>.Fail(set.Error);
        }
        catch (Exception ex)
        {
            return Result<JsonObject?>.Fail(FailureError(site, OutputPhase, Unwrap(ex).Message));
        }

        return Result<JsonObject?>.Ok(delta.Count == 0 ? null : delta);
    }

    private static async Task<Result> ApplySetAsync(
        DeclarativeMappingPlan plan, ExpressionScope scope, JsonObject root, TaskMappingSite site, string phase)
    {
        foreach (var entry in plan.Set)
        {
            var prepared = await scope.PrepareAsync(entry.UsedIdentifiers);
            if (!prepared.IsSuccess)
                return prepared; // caller roles could not be resolved: kept as its own (retryable) error

            JsonNode? value;
            try
            {
                var args = scope.Bind(plan.Parameters, entry.UsedIdentifiers);
                value = DeclarativeValues.ToNode(entry.Invoke(args));
            }
            catch (Exception ex)
            {
                return Result.Fail(FailureError(site, phase, $"set['{entry.Key}']: {Unwrap(ex).Message}"));
            }

            if (value is not null)
                entry.Target.Set(root, value);
        }

        return Result.Ok();
    }

    private static async Task<Result<JsonNode?>> ReadInputSelectAsync(DeclarativeMappingPlan plan, ExpressionScope scope)
    {
        var variable = plan.SelectVariable!;
        var used = plan.SelectUsed!;
        var prepared = await scope.PrepareAsync(used);
        if (!prepared.IsSuccess)
            return Result<JsonNode?>.Fail(prepared.Error);

        var value = scope.Bind([variable], used)[0];
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
        ctx.CompiledDeclarativeMappings ??= new Dictionary<MappingBlock, object>(ReferenceEqualityComparer.Instance);
        if (ctx.CompiledDeclarativeMappings.TryGetValue(block, out var memo))
            return Result<DeclarativeMappingPlan>.Ok((DeclarativeMappingPlan)memo);

        var built = DeclarativeMappingPlan.Build(compiler, block, kind);
        if (!built.IsSuccess)
            return Result<DeclarativeMappingPlan>.Fail(
                FailureError(site, kind == ScopeKind.MappingInput ? InputPhase : OutputPhase, built.Error.Message ?? "invalid block."));

        ctx.CompiledDeclarativeMappings[block] = built.Value!;
        return built;
    }

    private static JsonNode? LoadSurface(WorkflowTask task, string surface) => surface switch
    {
        DeclarativeMappingPlan.BodySurface when task is IHasBody body => ObjectOrEmpty(body.CurrentBody),
        DeclarativeMappingPlan.HeadersSurface when task is IHasHeaders headers => ObjectOrEmpty(headers.CurrentHeaders),
        DeclarativeMappingPlan.QuerySurface when task is IHasQuery query => FromPairs(query.CurrentQuery),
        DeclarativeMappingPlan.RouteSurface when task is IHasRouteValues route => ObjectOrEmpty(route.CurrentRouteValues),
        _ => null
    };

    /// <summary>Writes a surface back through the task's setter; returns an error message when the value has the wrong shape.</summary>
    private static string? WriteSurface(WorkflowTask task, string surface, JsonNode? value)
    {
        switch (surface)
        {
            case DeclarativeMappingPlan.BodySurface:
                ((IHasBody)task).ApplyBody(JsonSerializer.SerializeToElement(value));
                return null;
            case DeclarativeMappingPlan.HeadersSurface:
                if (ToStringMap(value) is not { } headers)
                    return "'$.headers' must stay a JSON object of header values.";
                ((IHasHeaders)task).ApplyHeaders(headers);
                return null;
            case DeclarativeMappingPlan.QuerySurface:
                if (ToStringMap(value) is not { } query)
                    return "'$.query' must stay a JSON object of query values.";
                ((IHasQuery)task).ApplyQuery(query.ToList());
                return null;
            default:
                if (ToStringMap(value) is not { } route)
                    return "'$.route' must stay a JSON object of route values.";
                ((IHasRouteValues)task).ApplyRouteValues(route);
                return null;
        }
    }

    private static JsonObject ObjectOrEmpty(JsonElement? current) =>
        current is { ValueKind: JsonValueKind.Object } element
            ? JsonNode.Parse(element.GetRawText())!.AsObject()
            : new JsonObject();

    private static JsonObject FromPairs(IReadOnlyList<KeyValuePair<string, string?>> pairs)
    {
        var node = new JsonObject();
        foreach (var (name, value) in pairs)
            node[name] = value is null ? null : JsonValue.Create(value);
        return node;
    }

    /// <summary>A JSON object as name → string (strings verbatim, other values as their JSON text); null when not an object.</summary>
    private static Dictionary<string, string?>? ToStringMap(JsonNode? value)
    {
        if (value is not JsonObject obj)
            return null;

        var map = new Dictionary<string, string?>(obj.Count, StringComparer.Ordinal);
        foreach (var (name, item) in obj)
        {
            map[name] = item switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                _ => item.ToJsonString()
            };
        }

        return map;
    }

    /// <summary>
    /// The <c>result</c> document: <c>body</c> (the parsed response <c>Data</c>, else the raw <c>Body</c> text),
    /// <c>headers</c> and <c>statusCode</c>.
    /// </summary>
    private static JsonDocument BuildResultDocument(TaskInvocationResult result)
    {
        var body = result.Data is not null
            ? JsonSerializer.SerializeToNode(result.Data, result.Data.GetType(), JsonSerializerConstants.JsonOptions)
            : result.Body is null ? null : JsonValue.Create(result.Body);
        var response = new JsonObject
        {
            ["body"] = body,
            ["headers"] = result.Headers is null ? null : JsonSerializer.SerializeToNode(result.Headers),
            ["statusCode"] = result.StatusCode is { } status ? JsonValue.Create(status) : null
        };
        return JsonSerializer.SerializeToDocument(response);
    }

    /// <summary>The exception an expression threw, without the reflection wrapper a dynamic invoke adds.</summary>
    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static Result Fail(TaskMappingSite site, string phase, string detail) =>
        Result.Fail(FailureError(site, phase, detail));

    private static Error FailureError(TaskMappingSite site, string phase, string detail) =>
        Error.Failure(WorkflowErrorCodes.TaskExecution,
            $"{site.TaskTypeName} task declarative {phase} mapping failed: {detail}");

    private static void Log(TaskMappingSite site, WorkflowTask task, TaskExecutorContext ctx, string phase, Error error) =>
        site.Logger?.TaskDeclarativeMappingFailed(
            task.Key,
            site.TaskTypeName,
            phase,
            ctx.ScriptContext.Instance?.Id ?? Guid.Empty,
            error.Message ?? error.Code);
}

/// <summary>
/// One <see cref="MappingBlock"/> compiled for one phase: parsed paths, the touched input surfaces and every <c>set</c>
/// expression compiled through the shared <see cref="IExpressionCompiler"/>. Immutable once built.
/// </summary>
internal sealed class DeclarativeMappingPlan
{
    public const string BodySurface = "body";
    public const string HeadersSurface = "headers";
    public const string QuerySurface = "query";
    public const string RouteSurface = "route";

    private static readonly string[] InputSurfaces = [BodySurface, HeadersSurface, QuerySurface, RouteSurface];

    private DeclarativeMappingPlan(
        IReadOnlyList<Parameter> parameters, JsonPath? select, JsonPath? into, SetEntry[] set, string[] surfaces,
        bool needsResult, Parameter? selectVariable, JsonPath? selectTail)
    {
        Parameters = parameters;
        Select = select;
        Into = into;
        Set = set;
        Surfaces = surfaces;
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

    /// <summary>Input only: the surfaces (<c>body</c>, <c>headers</c>, ...) the targets write, in first-use order.</summary>
    public string[] Surfaces { get; }

    /// <summary>Output only: whether the <c>result</c> document must be built.</summary>
    public bool NeedsResult { get; }

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
        var surfaces = new List<string>();

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
                selectVariable = signature.Parameters.FirstOrDefault(p => p.Name == select.FirstSegment);
                if (selectVariable is null)
                    return Invalid($"input 'select' {select.Text} must start at a variable ({string.Join(", ", signature.Parameters.Select(p => p.Name))}).");
                selectTail = select.WithoutFirstSegment();
            }
        }

        JsonPath? into = null;
        if (block.Into is not null)
        {
            var target = Target(block.Into, "into", isInput, surfaces);
            if (!target.IsSuccess)
                return Result<DeclarativeMappingPlan>.Fail(target.Error);
            into = target.Value!;
        }

        var entries = new List<SetEntry>();
        var needsResult = !isInput && (select is not null || into is not null);
        foreach (var (key, expression) in block.Set ?? new Dictionary<string, string>())
        {
            var target = Target(key, $"set['{key}']", isInput, surfaces);
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
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressoInstanceView?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5]),
                    compiled.Value.UsedIdentifiers);
            }
            else
            {
                var compiled = compiler.Compile<OutputFn>(expression, signature);
                if (!compiled.IsSuccess)
                    return Invalid($"set['{key}']: the expression does not compile: {compiled.Error.Message}");
                var fn = compiled.Value!.Invoke;
                entry = new SetEntry(key, target.Value!,
                    a => fn((RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressoInstanceView?)a[2],
                        (DateTimeOffset)a[3]!, (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5],
                        (RuleJsonDynamic?)a[6]),
                    compiled.Value.UsedIdentifiers);
                needsResult |= compiled.Value.UsedIdentifiers.Contains(ExpressionScope.ResultVariable);
            }

            entries.Add(entry);
        }

        return Result<DeclarativeMappingPlan>.Ok(new DeclarativeMappingPlan(
            signature.Parameters, select, into, entries.ToArray(), surfaces.ToArray(), needsResult,
            selectVariable, selectTail));
    }

    private static Result<JsonPath> Target(string text, string what, bool isInput, List<string> surfaces)
    {
        var parsed = Parse(text, what);
        if (!parsed.IsSuccess)
            return parsed;
        var path = parsed.Value!;
        if (!path.IsWritable || path.FirstSegment.Length == 0)
            return Result<JsonPath>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution,
                $"{what} {text} is not a writable property path."));

        if (isInput)
        {
            if (!InputSurfaces.Contains(path.FirstSegment, StringComparer.Ordinal))
                return Result<JsonPath>.Fail(Error.Validation(WorkflowErrorCodes.TaskExecution,
                    $"{what} {text} must start with $.body, $.headers, $.query or $.route."));
            if (!surfaces.Contains(path.FirstSegment))
                surfaces.Add(path.FirstSegment);
        }

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
            : JsonNode.Parse(element.GetRawText()),
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), JsonSerializerConstants.JsonOptions)
    };
}
