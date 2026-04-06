namespace BBT.Workflow.Authorization;

/// <summary>
/// HTTP request context available at authorization time.
/// Populated from the HTTP request and passed through the authorize pipeline.
/// Enables dynamic role grants to reference <c>$.context.Headers.*</c>,
/// <c>$.context.QueryParameters.*</c>, and <c>$.context.RouteValues.*</c> paths.
/// </summary>
public sealed record AuthorizationRequestContext(
    IReadOnlyDictionary<string, string?>? Headers = null,
    IReadOnlyDictionary<string, string?>? QueryParameters = null,
    IReadOnlyDictionary<string, string?>? RouteValues = null);
