namespace BBT.Workflow.Instances;

/// <summary>
/// Output for retrieving instance view
/// </summary>
public sealed class GetViewOutput
{
    /// <summary>
    /// The view key
    /// </summary>
    public string Key { get; set; }
    
    /// <summary>
    /// The view content. When <see cref="Type"/> is Json, DeepLink, Http, or URN, this is a JSON object or array; when Html or Markdown, this is a string.
    /// </summary>
    public object? Content { get; set; }

    /// <summary>
    /// The view type
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// Display mode
    /// </summary>
    public string Display { get; set; }

    /// <summary>
    /// Localization label
    /// </summary>
    public string Label { get; set; }
}

