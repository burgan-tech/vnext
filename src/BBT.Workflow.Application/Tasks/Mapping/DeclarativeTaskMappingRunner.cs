using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Declarative;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Configuration;

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
        var planResult = GetOrBuildPlan(ctx, block, DeclarativeSiteKind.TaskInput, site);
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
            var applied = await DeclarativeBlockEngine.ApplyAsync(
                plan, scope, request, result: null, Site(site, task, InputPhase), ct);
            if (!applied.IsSuccess)
                return applied;

            if (request[HeadersField] is JsonObject headers)
                request[HeadersField] = HeadersAsStrings(headers);
            task.ApplyConfig(JsonSerializer.SerializeToElement(request));
        }
        catch (Exception ex) when (!DeclarativeBlockEngine.IsCancellation(ex, ct))
        {
            return Fail(site, InputPhase, DeclarativeBlockEngine.Unwrap(ex).Message);
        }

        return Result.Ok();
    }

    private async Task<Result<JsonObject?>> BuildOutputAsync(
        WorkflowTask task, TaskExecutorContext ctx, MappingBlock block, TaskInvocationResult result, TaskMappingSite site,
        CancellationToken ct)
    {
        var planResult = GetOrBuildPlan(ctx, block, DeclarativeSiteKind.TaskOutput, site);
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

            var applied = await DeclarativeBlockEngine.ApplyTaskOutputAsync(
                plan, scope, delta, resultView, Site(site, task, OutputPhase), ct);
            if (!applied.IsSuccess)
                return Result<JsonObject?>.Fail(applied.Error);
        }
        catch (Exception ex) when (!DeclarativeBlockEngine.IsCancellation(ex, ct))
        {
            return Result<JsonObject?>.Fail(FailureError(site, OutputPhase, DeclarativeBlockEngine.Unwrap(ex).Message));
        }

        return Result<JsonObject?>.Ok(delta.Count == 0 ? null : delta);
    }

    /// <summary>The engine's view of this binding: the task key in skip logs, <c>"{TaskType} task"</c> in failures.</summary>
    private static DeclarativeSite Site(TaskMappingSite site, WorkflowTask task, string phase) =>
        new(task.Key, phase) { Logger = site.Logger, ErrorSubject = site.TaskTypeName + " task" };

    private Result<DeclarativeMappingPlan> GetOrBuildPlan(
        TaskExecutorContext ctx, MappingBlock block, DeclarativeSiteKind kind, TaskMappingSite site)
    {
        var phase = kind == DeclarativeSiteKind.TaskInput ? InputPhase : OutputPhase;
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

/// <summary>Keys the per-execution plan memo by the binding's block instance (reference identity) and phase.</summary>
internal sealed class BlockPhaseComparer : IEqualityComparer<(MappingBlock Block, string Phase)>
{
    public static readonly BlockPhaseComparer Instance = new();

    public bool Equals((MappingBlock Block, string Phase) x, (MappingBlock Block, string Phase) y) =>
        ReferenceEquals(x.Block, y.Block) && string.Equals(x.Phase, y.Phase, StringComparison.Ordinal);

    public int GetHashCode((MappingBlock Block, string Phase) obj) =>
        HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Block), obj.Phase);
}
