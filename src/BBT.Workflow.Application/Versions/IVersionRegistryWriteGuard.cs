using System.Text.Json;
using BBT.Aether.Results;

namespace BBT.Workflow.Versions;

/// <summary>
/// Rejects an invalid default-version pointer BEFORE it is persisted to a <c>sys-version-registry</c>
/// instance. A pointer that matches no published version would fail closed every start of the flow
/// (<c>Instance:100052</c>), so the write is refused up front instead.
/// </summary>
/// <remarks>Only called for the registry flow itself; other flows never reach it.</remarks>
public interface IVersionRegistryWriteGuard
{
    /// <summary>
    /// Validates the registry payload written for <paramref name="flowKey"/> (the registry instance key).
    /// </summary>
    /// <param name="domain">The domain the registry instance lives in.</param>
    /// <param name="flowKey">The target flow key (the registry instance key).</param>
    /// <param name="data">The transition/start payload; null or without <c>defaultVersion</c> passes.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>Ok, or <c>Instance:100052</c> when the pointer is not acceptable.</returns>
    Task<Result> ValidateAsync(
        string domain,
        string flowKey,
        JsonElement? data,
        CancellationToken cancellationToken = default);
}
