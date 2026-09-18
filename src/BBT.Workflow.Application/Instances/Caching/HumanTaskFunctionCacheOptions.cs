using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Configuration for the <c>human-task</c> response cache.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the state- and data-function caches, this one has <b>no validation query</b>. Those store
/// a single instance's response beside a fingerprint and re-check it on every hit, so their TTL only
/// bounds the staleness of what the fingerprint does not cover. A list spans every workflow schema
/// in the domain and has no single-row fingerprint to re-check, so this is a plain TTL cache.
/// </para>
/// <para>
/// The concrete consequence, accepted deliberately: a task completed by colleague B is still offered
/// to colleague A for up to <see cref="TtlSeconds"/>, who opens it and gets an error.
/// <see cref="Enabled"/> is the kill switch, and it is honest only because the cache is keyed per
/// caller scope — nothing shared between callers is being retained.
/// </para>
/// </remarks>
public sealed class HumanTaskFunctionCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HumanTaskFunctionCache";

    /// <summary>
    /// Whether the cache is on. False forces a full fan-out on every request (kill switch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Entry lifetime. See the staleness note on this type.</summary>
    /// <remarks>
    /// Validated at startup. A non-positive TTL would not disable the cache — it would write
    /// entries that are already expired, so every request pays a full fan-out AND a pair of cache
    /// round trips. <see cref="Enabled"/> is the kill switch; this is not one.
    /// </remarks>
    [Range(1, 86_400, ErrorMessage = "TtlSeconds must be between 1 and 86400")]
    public int TtlSeconds { get; set; } = 60;

    /// <summary>
    /// Whether a client may ask for a rebuild with the cache-override header.
    /// </summary>
    /// <remarks>
    /// The override skips the cache <i>read</i> and still <i>writes</i> the result, behind the same
    /// single-flight gate as an ordinary miss. A full bypass — skipping the write too — would hand
    /// an unauthenticated caller a free way to make the endpoint the cache exists to protect more
    /// expensive than it is without one, and this route has no rate limiter of its own. Set false to
    /// ignore the header entirely.
    /// </remarks>
    public bool AllowClientOverride { get; set; } = true;
}
