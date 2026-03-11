using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

/// <summary>
/// Output for retrieving instance state with combined information
/// </summary>
public sealed class GetInstanceStateOutput
{
    /// <summary>
    /// Data href link with optional extensions
    /// </summary>
    public DataHref Data { get; set; } = new();

    /// <summary>
    /// View href link
    /// </summary>
    public ViewHref View { get; set; } = new();

    /// <summary>
    /// Current state of the instance
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Instance status
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// Active correlations with href links
    /// </summary>
    public List<ActiveCorrelationHref> ActiveCorrelations { get; set; } = [];

    /// <summary>
    /// Available transition items with href links
    /// </summary>
    public List<TransitionItem> Transitions { get; set; } = [];

    /// <summary>
    /// Representation ETag (RFC 7232 quoted) for cache validation.
    /// </summary>
    public string? ETag
    {
        get
        {
            if (string.IsNullOrEmpty(_etag))
                return null;
            var unquoted = _etag.Replace("\"", "");
            return $"\"{unquoted}\"";
        }
        set => _etag = value;
    }
    private string? _etag = string.Empty;

    /// <summary>
    /// Entity (DB row) version for concurrency, RFC 7232 quoted. Exposed as X-Entity-ETag response header.
    /// </summary>
    public string? EntityEtag
    {
        get
        {
            if (string.IsNullOrEmpty(_entityEtag))
                return null;
            var unquoted = _entityEtag.Replace("\"", "");
            return $"\"{unquoted}\"";
        }
        set => _entityEtag = value;
    }
    private string? _entityEtag = string.Empty;
}
