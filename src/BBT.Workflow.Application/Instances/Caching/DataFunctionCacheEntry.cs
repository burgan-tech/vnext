using System.Text.Json;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed-cache envelope for the DATA portion of a data-function response. Stores ONLY
/// the caller-scoped, field-filtered instance data — never extension output: a validated
/// entry supplies the response's Data (skipping the x-roles filtering step) while extensions
/// are always computed fresh on the build path. Validation on a hit is a single ETag equality
/// check: the current ETag is recomputed from the lightweight data fingerprint, so a matching
/// entry is guaranteed to reflect the instance's current latest-data version and flow version.
/// </summary>
public sealed class DataFunctionCacheEntry
{
    /// <summary>
    /// Unquoted fingerprint ETag the response was built under
    /// (hash of instance id + latest data ETag + flow version + caller scope).
    /// </summary>
    public string Etag { get; set; } = string.Empty;

    /// <summary>
    /// ETag of the instance-data row the response was built from (X-Entity-ETag header value).
    /// </summary>
    public string EntityEtag { get; set; } = string.Empty;

    /// <summary>
    /// The caller-scoped (x-roles filtered) instance data payload.
    /// </summary>
    public JsonElement? Data { get; set; }
}
