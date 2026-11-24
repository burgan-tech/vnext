using System.Text.Json;

namespace BBT.Workflow.Definitions;

/// <summary>
/// A task whose request carries a JSON body: the <c>$.body</c> surface of a declarative <c>input</c> block.
/// Implemented explicitly by each task type, delegating to that type's existing setter (for example
/// <see cref="HttpTask.SetBody"/>), so a declarative mapping changes a task exactly as a script mapping would.
/// </summary>
public interface IHasBody
{
    /// <summary>The body as currently configured (the component's static value, or what an earlier write set).</summary>
    JsonElement? CurrentBody { get; }

    /// <summary>Replaces the body through the task's own setter.</summary>
    void ApplyBody(JsonElement body);
}

/// <summary>
/// A task whose request carries string headers: the <c>$.headers</c> surface of a declarative <c>input</c> block.
/// Each implementation delegates to the task type's existing <c>SetHeaders</c>.
/// </summary>
public interface IHasHeaders
{
    /// <summary>The headers as currently configured (a JSON object of name to value), or null when none.</summary>
    JsonElement? CurrentHeaders { get; }

    /// <summary>Replaces the headers through the task's own setter.</summary>
    void ApplyHeaders(Dictionary<string, string?> headers);
}

/// <summary>
/// A task whose request carries query parameters: the <c>$.query</c> surface of a declarative <c>input</c> block.
/// Each implementation delegates to the task type's existing setter (for example <see cref="DaprServiceTask.SetQueryString"/>).
/// </summary>
public interface IHasQuery
{
    /// <summary>The query parameters as currently configured, in order; empty when none.</summary>
    IReadOnlyList<KeyValuePair<string, string?>> CurrentQuery { get; }

    /// <summary>Replaces the query parameters through the task's own setter.</summary>
    void ApplyQuery(IReadOnlyList<KeyValuePair<string, string?>> query);
}

/// <summary>
/// A task whose request carries route values: the <c>$.route</c> surface of a declarative <c>input</c> block.
/// No built-in task type exposes route values today; a binding that targets <c>$.route</c> on a task without this
/// surface fails with a clear error. The interface fixes the contract for the task type that adds one.
/// </summary>
public interface IHasRouteValues
{
    /// <summary>The route values as currently configured (a JSON object of name to value), or null when none.</summary>
    JsonElement? CurrentRouteValues { get; }

    /// <summary>Replaces the route values through the task's own setter.</summary>
    void ApplyRouteValues(Dictionary<string, string?> routeValues);
}
