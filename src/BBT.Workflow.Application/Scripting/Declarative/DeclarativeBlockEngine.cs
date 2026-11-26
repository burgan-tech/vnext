using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.CSharp.RuntimeBinder;

namespace BBT.Workflow.Scripting.Declarative;

/// <summary>
/// Runs one compiled declarative block (<see cref="DeclarativeMappingPlan"/>) against a target object, whatever the
/// site: the <c>select</c> step first (into <c>into</c>, or — at an output-style site without <c>into</c> — an object
/// merged at the target root), then every <c>set</c> entry in declaration order.
/// </summary>
/// <remarks>
/// <para>
/// A missing source writes nothing (RF-1): a missing <c>select</c> path, a null value, and a member read through a
/// missing intermediate (the binder's null-reference failure). Each such skip raises the Debug
/// <c>TaskDeclarativeMappingEntrySkipped</c> with the site name, phase and path or set key — never the value.
/// </para>
/// <para>
/// Failures — a caller-role provider failure (<c>Authorization:110006</c>, kept as its own retryable error), a role read
/// on a hop with no caller, any other exception an expression throws, a select without <c>into</c> that is not an
/// object, a path that cannot be written — return a failed <see cref="Result"/> naming the site
/// (<see cref="DeclarativeSite.Failure"/>). Cancellation of the caller's own token propagates. Every value is
/// materialized into an owned <see cref="JsonNode"/> (<see cref="DeclarativeValues"/>) before the scope's borrowed
/// documents go away. Stateless and thread-safe; the scope is the caller's, one per run.
/// </para>
/// </remarks>
internal static class DeclarativeBlockEngine
{
    internal const string MissingSelectReason = "select path not found";
    internal const string MissingIntermediateReason = "missing intermediate";

    internal const string NoCallerDetail =
        "user.roles / user.hasRole was read on a hop with no caller (a System hop: a timer, an automatic chain, an event); " +
        "guard the read with user.available.";

    /// <summary>
    /// Applies <paramref name="plan"/> to <paramref name="target"/>. <paramref name="result"/> is the document a
    /// result-site <c>select</c> reads (<see cref="DeclarativeSiteKind.SubFlowOutput"/>: <c>{ data, status }</c>;
    /// <see cref="DeclarativeSiteKind.TaskOutput"/>: <c>{ body, headers, statusCode }</c>;
    /// <see cref="DeclarativeSiteKind.FunctionOutput"/>: <c>{ &lt;task&gt;: { body, headers, statusCode } }</c>); ignored elsewhere. The
    /// <c>result</c> expression variable comes from <paramref name="scope"/>, which the caller built for the site's
    /// <see cref="DeclarativeMappingPlan.ScopeOf"/> kind.
    /// </summary>
    public static Task<Result> ApplyAsync(
        DeclarativeMappingPlan plan, ExpressionScope scope, JsonObject target, JsonElement? result, DeclarativeSite site,
        CancellationToken ct) =>
        ApplyCoreAsync(plan, scope, target, result, view: null, site, ct);

    /// <summary>
    /// Task output: as <see cref="ApplyAsync(DeclarativeMappingPlan, ExpressionScope, JsonObject, JsonElement?, DeclarativeSite, CancellationToken)"/>,
    /// with the <c>select</c> read through the task's <see cref="TaskResultView"/> (no result document is built).
    /// </summary>
    internal static Task<Result> ApplyTaskOutputAsync(
        DeclarativeMappingPlan plan, ExpressionScope scope, JsonObject target, TaskResultView? view, DeclarativeSite site,
        CancellationToken ct) =>
        ApplyCoreAsync(plan, scope, target, result: null, view, site, ct);

    // One async frame per run (select + set inline): a declarative block is on every task binding's hot path, and each
    // extra synchronously-completing async layer costs a Task allocation.
    private static async Task<Result> ApplyCoreAsync(
        DeclarativeMappingPlan plan, ExpressionScope scope, JsonObject target, JsonElement? result, TaskResultView? view,
        DeclarativeSite site, CancellationToken ct)
    {
        try
        {
            Result selected;
            if (plan.ResultSelectPath is { } resultPath)
            {
                var found = view is not null
                    ? view.TryRead(plan.ResultSelect!, out var element)
                    : TryReadResult(result, resultPath, out element);
                selected = Write(plan, target, found ? DeclarativeValues.ToNode(element) : null, resultPath, site);
            }
            else if (plan.IsInputStyle && (plan.Into is not null || plan.Select is not null)
                     && (plan.Into is null || plan.SelectVariable is null))
            {
                selected = Result.Fail(site.Failure("an input 'select' and 'into' must be authored together."));
            }
            else if (plan.SelectVariable is not null)
            {
                var read = await ReadVariableSelectAsync(plan, scope, site);
                selected = read.IsSuccess ? Write(plan, target, read.Value, plan.Select!, site) : Result.Fail(read.Error);
            }
            else
            {
                selected = Result.Ok();
            }

            if (!selected.IsSuccess)
                return selected;

            for (var i = 0; i < plan.Set.Count; i++)
            {
                var entry = plan.Set[i];
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
                    var failure = SetFailure(entry, Unwrap(ex), site);
                    if (failure is null)
                        continue; // RF-1: a missing intermediate writes nothing
                    return Result.Fail(failure.Value);
                }

                if (value is not null)
                    entry.Target.Set(target, value);
            }

            return Result.Ok();
        }
        catch (Exception ex) when (!IsCancellation(ex, ct))
        {
            return Result.Fail(site.Failure(Unwrap(ex).Message));
        }
    }

    /// <summary>
    /// What a <c>set</c> entry's exception means: null for a member read through a missing intermediate
    /// (<c>data.customer.id</c> with no <c>customer</c> — RF-1, logged as a skip), otherwise the failure to return.
    /// </summary>
    private static Error? SetFailure(DeclarativeMappingPlan.SetEntry entry, Exception cause, DeclarativeSite site)
    {
        switch (cause)
        {
            case RuntimeBinderException binder when IsMemberAccessOnNull(binder):
                LogSkipped(site, $"set['{entry.Key}']", MissingIntermediateReason);
                return null;
            case CallerRolesUnavailableException:
                // The expression read user.roles / user.hasRole on a hop with no caller (a System hop): a deterministic
                // authoring error, not a transient one — retrying the same hop fails the same way. A provider failure
                // never reaches here: PrepareAsync reports it (Authorization:110006).
                return site.Failure($"set['{entry.Key}']: {NoCallerDetail}");
            default:
                return site.Failure($"set['{entry.Key}']: {cause.Message}");
        }
    }

    private static bool TryReadResult(JsonElement? result, JsonPath path, out JsonElement element)
    {
        if (result is { } document)
            return path.TryRead(document, out element);

        element = default;
        return false;
    }

    /// <summary>The select step's write: into <c>into</c>, or an object merged at the root of an output-style target.</summary>
    private static Result Write(DeclarativeMappingPlan plan, JsonObject target, JsonNode? selected, JsonPath source, DeclarativeSite site)
    {
        if (selected is null)
        {
            LogSkipped(site, source.Text, MissingSelectReason);
            return Result.Ok();
        }

        if (plan.Into is not null)
        {
            plan.Into.Set(target, selected);
            return Result.Ok();
        }

        if (selected is not JsonObject selectedObject)
        {
            return Result.Fail(site.Failure(
                $"'select' {source.Text} without 'into' must select a JSON object to merge at the data root, " +
                $"but selected a {selected.GetValueKind()}."));
        }

        foreach (var (name, value) in selectedObject.ToArray())
        {
            selectedObject.Remove(name);
            target[name] = value;
        }

        return Result.Ok();
    }

    private static async Task<Result<JsonNode?>> ReadVariableSelectAsync(
        DeclarativeMappingPlan plan, ExpressionScope scope, DeclarativeSite site)
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
                return Result<JsonNode?>.Fail(site.Failure($"'select' {plan.Select!.Text}: {NoCallerDetail}"));
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

    /// <summary>
    /// True for the binder's "Cannot perform runtime binding on a null reference": a member or index read on a value
    /// that is null — in a declarative scope, a path through a missing intermediate. Every other binder failure (a member
    /// that does not exist on a CLR type, a bad conversion) stays an error.
    /// </summary>
    private static bool IsMemberAccessOnNull(RuntimeBinderException exception) =>
        exception.Message.Contains("null reference", StringComparison.OrdinalIgnoreCase);

    /// <summary>A cancellation of this run's own token propagates; anything else becomes a Result failure.</summary>
    internal static bool IsCancellation(Exception exception, CancellationToken ct) =>
        exception is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>The exception an expression threw, without the reflection wrapper a dynamic invoke adds.</summary>
    internal static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    /// <summary>RF-1 skip at Debug: the site, phase and the path or set key — never the value.</summary>
    private static void LogSkipped(DeclarativeSite site, string path, string reason) =>
        site.Logger?.TaskDeclarativeMappingEntrySkipped(site.Name, site.Phase, path, reason);
}
