using System.Collections.Concurrent;
using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Memoizes <see cref="SensitiveSchemaParser.Parse"/> per schema identity.
/// <para>
/// The parse walks the whole schema, and the result is needed on hot paths (every script context
/// build) for something that is inert in most workflows. Schema components are immutable per
/// version — a change means a new version — so keying on domain/key/version is exact, and the
/// overwhelmingly common answer, "this schema annotates nothing", is cached as an empty map.
/// </para>
/// <para>
/// Unbounded on purpose: the key space is the set of published schema versions a host has
/// actually touched, which is small and bounded by the deployment rather than by traffic.
/// </para>
/// </summary>
public static class SensitiveSchemaCache
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, SensitiveFieldMetadata>> Cache
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the <c>x-sensitive</c> map for a schema component, parsing it at most once per
    /// identity.
    /// </summary>
    /// <param name="domain">Schema component domain.</param>
    /// <param name="key">Schema component key.</param>
    /// <param name="version">Schema component version.</param>
    /// <param name="schemaRoot">The schema body, parsed only on a cache miss.</param>
    /// <returns>Path → metadata; empty when the schema annotates nothing.</returns>
    public static IReadOnlyDictionary<string, SensitiveFieldMetadata> GetOrParse(
        string domain,
        string key,
        string version,
        JsonElement schemaRoot)
    {
        var cacheKey = $"{domain}:{key}:{version}";
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var parsed = SensitiveSchemaParser.Parse(schemaRoot);
        Cache[cacheKey] = parsed;
        return parsed;
    }

    /// <summary>
    /// Drops all memoized entries. Exists for tests; production has no invalidation need because
    /// the key includes the version.
    /// </summary>
    public static void Clear() => Cache.Clear();
}
