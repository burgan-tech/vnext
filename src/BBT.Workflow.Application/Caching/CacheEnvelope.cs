namespace BBT.Workflow.Caching;

/// <summary>
/// Wraps a cached entity with its reference metadata (domain, key, version).
/// Needed because domain entities use private setters for reference properties,
/// which System.Text.Json cannot deserialize by default. This envelope preserves
/// the reference info across Redis serialization round-trips.
/// </summary>
/// <typeparam name="T">The type of entity being cached</typeparam>
public sealed class CacheEnvelope<T> where T : class
{
    public string Domain { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Flow { get; set; } = string.Empty;
    public T? Entity { get; set; }
}
