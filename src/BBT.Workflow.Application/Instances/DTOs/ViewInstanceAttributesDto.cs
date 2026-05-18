using System.Text.Json.Serialization;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// DTO for view instance attributes returned in GetInstanceOutput.Attributes when the instance represents a view (e.g. sys-views).
/// Used for model-based mapping from remote GetInstanceAsync response to GetViewOutput.
/// </summary>
public sealed class ViewInstanceAttributesDto
{
    /// <summary>
    /// View content (e.g. JSON or markup).
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>
    /// View type.
    /// </summary>
    [JsonPropertyName("type")]
    public ViewType? Type { get; set; }

    /// <summary>
    /// Display mode.
    /// </summary>
    [JsonPropertyName("display")]
    public string? Display { get; set; }

    /// <summary>
    /// Localization label.
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// Identifies which UI SDK / render engine should interpret the view content.
    /// </summary>
    [JsonPropertyName("renderer")]
    public string? Renderer { get; set; }
}
