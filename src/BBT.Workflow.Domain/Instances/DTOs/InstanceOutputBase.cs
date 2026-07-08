using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances;

/// <summary>
/// Base output shared by instance operation responses (Start, Transition, etc.).
/// </summary>
public abstract class InstanceOutputBase
{
    /// <summary>
    /// The workflow instance identifier.
    /// </summary>
    public Guid Id { get; set; }

    public string? Key { get; set; }

    /// <summary>
    /// Instance status (Active, Busy, Completed, etc.)
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// Instance attributes filtered by master-schema role grants. Populated only when sync=true.
    /// </summary>
    public JsonElement? Attributes { get; set; }

    /// <summary>
    /// Representation ETag (SHA256 of canonical response JSON), returned with quotes per RFC 7232. Populated only when sync=true.
    /// </summary>
    public string? ETag
    {
        get => _etag is null ? null : $"\"{_etag.Replace("\"", "")}\"";
        set => _etag = value;
    }
    private string? _etag;

    /// <summary>
    /// Entity (DB row) version for concurrency, returned with quotes per RFC 7232. Populated only when sync=true.
    /// </summary>
    public string? EntityEtag
    {
        get => _entityEtag is null ? null : $"\"{_entityEtag.Replace("\"", "")}\"";
        set => _entityEtag = value;
    }
    private string? _entityEtag;

    /// <summary>
    /// Computed extension fields. Populated only when sync=true.
    /// </summary>
    public Dictionary<string, object>? Extensions { get; set; }

    /// <summary>
    /// Carries the pipeline's committed instance for sync enrichment.
    /// Avoids an additional DB round-trip when building the sync response.
    /// Not serialized — internal transport between pipeline and AppService only.
    /// </summary>
    [JsonIgnore]
    public Instance? PipelineInstance { get; set; }

    /// <summary>
    /// True when a workflow output script ran (sync=true, non-subflow, script configured).
    /// Signals the mapper to bypass the standard envelope and return the mapped payload
    /// directly — even when <see cref="OutputData"/> is null (intentional empty response).
    /// Not serialized — internal transport between AppService and the controller mapper only.
    /// </summary>
    [JsonIgnore]
    public bool HasOutputResponse { get; set; }

    /// <summary>Mapped payload returned as the raw response body. May be null. Not serialized.</summary>
    [JsonIgnore]
    public object? OutputData { get; set; }

    /// <summary>Optional HTTP status code from the output script. Not serialized.</summary>
    [JsonIgnore]
    public int? OutputStatusCode { get; set; }

    /// <summary>Optional response headers from the output script. Not serialized.</summary>
    [JsonIgnore]
    public Dictionary<string, string>? OutputHeaders { get; set; }
}
