namespace BBT.Workflow.Instances;

/// <summary>
/// Per-client-mode display declaration returned alongside a view.
/// </summary>
public sealed class ViewDisplayModesDto
{
    /// <summary>
    /// Display value for SDI (single-document interface) clients, e.g. <c>full-page</c>, <c>popup</c>.
    /// Null when the view only declares an MDI display.
    /// </summary>
    public string? Sdi { get; set; }

    /// <summary>
    /// Display value for MDI (multi-document interface) clients, e.g. <c>tab</c>, <c>window</c>.
    /// Null when the view declares no MDI presentation.
    /// </summary>
    public string? Mdi { get; set; }
}
