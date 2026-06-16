using System.Text.Json.Nodes;

namespace BBT.Workflow.Mcp.Meta;

/// <summary>
/// In-memory accessor for the <c>vnext-meta</c> JSON files loaded from the npm package at startup.
/// No filesystem dependency — identical behavior under stdio and Streamable HTTP transports.
/// </summary>
public interface IMetaProvider
{
    /// <summary>True once the npm package has been fetched and parsed.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Returns the parsed JSON for a meta file (e.g. <c>features.json</c>), or <c>null</c> if the
    /// package is not loaded yet or the file is absent.
    /// </summary>
    JsonNode? Get(string fileName);

    /// <summary>
    /// Fetches the pinned npm package tarball, extracts the meta JSON files, and swaps them into the
    /// in-memory cache. Returns <c>true</c> on success; never throws (failures are logged and the
    /// provider stays in its previous state for a later retry).
    /// </summary>
    Task<bool> LoadAsync(CancellationToken cancellationToken = default);
}
