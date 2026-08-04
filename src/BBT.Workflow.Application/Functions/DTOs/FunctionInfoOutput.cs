using BBT.Workflow.Shared;

namespace BBT.Workflow.Functions.DTOs;

/// <summary>
/// Discovery response for a single function: what the caller may invoke, how, and which view and
/// schema contracts apply to this request. Follows the same hyperlink shape as the state function -
/// the client never resolves component references itself, it follows the hrefs handed to it.
/// </summary>
/// <remarks>
/// Only returned to a caller that passed the function's scope and role gates, so its presence is
/// itself the "you may run this" answer; a denied caller gets 403 instead.
/// </remarks>
public sealed class FunctionInfoOutput
{
    /// <summary>The function key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The domain the function belongs to.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The resolved function version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Scope code the function declares: <c>D</c>, <c>F</c> or <c>I</c>.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// The executable endpoint for this function, carrying the verbs it accepts.
    /// </summary>
    public FunctionHref Function { get; set; } = new();

    /// <summary>
    /// Whether the function returns its task response unwrapped. Lets the client know the payload is
    /// not the usual <c>{ "functionKey": data }</c> envelope.
    /// </summary>
    public bool RawResponse { get; set; }

    /// <summary>
    /// Whether the function declares a read-through cache, so the client knows a response may be
    /// served from cache rather than freshly computed.
    /// </summary>
    public bool Cacheable { get; set; }

    /// <summary>
    /// The view the client renders to collect this function's input.
    /// <c>hasView</c> is false when the function declares none, or when its rules matched nothing for
    /// this request; the href is always present so the client can retry after conditions change.
    /// </summary>
    public ViewHref InputView { get; set; } = new();

    /// <summary>The view the client renders to present this function's output.</summary>
    public ViewHref OutputView { get; set; } = new();

    /// <summary>The schema describing this function's request body.</summary>
    public SchemaHref InputSchema { get; set; } = new();

    /// <summary>The schema describing this function's response body.</summary>
    public SchemaHref OutputSchema { get; set; } = new();
}
