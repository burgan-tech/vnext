namespace BBT.Workflow.Scripting;

/// <summary>
/// Provides the original, unmodified request body (as a string) for the current execution so that
/// mappings can verify signatures (JWS / mTLS) over the exact payload bytes. Implementations resolve
/// from the ambient job scope first (background pipeline execution via <see cref="RawBodyExecutionScope"/>),
/// then from the live HTTP request when available.
/// </summary>
public interface IRequestRawBodyProvider
{
    /// <summary>
    /// Returns the raw request body for the current scope, or <c>null</c> when no raw body is available
    /// (e.g. purely internal executions with neither an HTTP request nor a job scope).
    /// </summary>
    string? GetRawBody();
}
