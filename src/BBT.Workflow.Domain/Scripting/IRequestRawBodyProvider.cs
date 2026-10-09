using System.Text.Json;

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

    /// <summary>
    /// Replaces the business payload inside the raw body seen by the rest of the current request: the
    /// x-storage admission swaps file <c>content</c> for handles, and scripts must never see the bytes.
    /// When the captured body is a standard <c>{ key, tags, stage, attributes }</c> envelope only its
    /// <c>attributes</c> member is rewritten; a free-form (raw-mode) body is replaced by
    /// <paramref name="attributes"/> whole. Only the live request is affected, never an ambient job scope.
    /// Hosts without a request body keep the default no-op.
    /// </summary>
    void ReplaceRawBodyAttributes(JsonElement? attributes)
    {
    }
}
