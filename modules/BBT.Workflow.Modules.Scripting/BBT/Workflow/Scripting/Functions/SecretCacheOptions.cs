namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Options for the in-process secret bundle cache used by <see cref="ScriptBase"/> secret
/// functions, bound from configuration (section <c>Scripting:SecretCache</c>).
///
/// The cache is deliberately in-process (not the distributed cache): secret material must never
/// transit Redis or any other shared cache store. A short TTL keeps vault load bounded under
/// high script throughput while limiting staleness after a secret rotation to at most
/// <see cref="TtlSeconds"/> seconds per process.
/// </summary>
public sealed class SecretCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Scripting:SecretCache";

    /// <summary>
    /// Master switch. Defaults to <c>true</c>. When <c>false</c>, every secret read goes
    /// straight to the Dapr secret store with no caching.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Time-to-live for a cached secret bundle, in seconds. Defaults to 30.
    /// A value of zero or less bypasses the cache entirely (same effect as
    /// <see cref="Enabled"/> = <c>false</c>).
    /// </summary>
    public int TtlSeconds { get; set; } = 30;
}
