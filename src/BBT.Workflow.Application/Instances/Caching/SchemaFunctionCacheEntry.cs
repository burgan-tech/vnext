using BBT.Workflow.Instances.DTOs;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed-cache envelope for a master- or schema-function response. Stores the resolved
/// schema document output together with the fingerprint ETag it was built under. Validation on
/// a hit is a single ETag equality check against the ETag recomputed from the lightweight data
/// fingerprint. There is no entity ETag — schema documents are definition components, not
/// instance-data rows.
/// </summary>
public sealed class SchemaFunctionCacheEntry
{
    /// <summary>
    /// Unquoted fingerprint ETag the response was built under.
    /// </summary>
    public string Etag { get; set; } = string.Empty;

    /// <summary>
    /// The fully built schema response for the caller scope encoded in the cache key.
    /// </summary>
    public GetSchemaOutput Output { get; set; } = new();
}
