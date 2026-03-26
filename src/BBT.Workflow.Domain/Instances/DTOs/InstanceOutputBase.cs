using System.Text.Json;

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
}
