using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow;

/// <summary>
/// Interface for view definitions that support rule-based view selection.
/// </summary>
public interface IViewDefinition
{
    /// <summary>
    /// Array of view entries with optional rules for conditional selection.
    /// Views are evaluated in order, and the first matching view is returned.
    /// </summary>
    IReadOnlyList<ViewEntry> Views { get; }
}

/// <summary>
/// Interface for setting view definitions.
/// </summary>
public interface IViewDefinitionSetter
{
    void SetViewDefinition(IViewDefinition viewDefinition);
}

/// <summary>
/// Helper class for deserializing old view format (backward compatibility).
/// </summary>
internal sealed class OldViewFormat
{
    [JsonPropertyName("view")]
    public Reference? View { get; set; }
    
    [JsonPropertyName("extensions")]
    public string[]? Extensions { get; set; }
    
    [JsonPropertyName("loadData")]
    public bool? LoadData { get; set; }
}

/// <summary>
/// Represents a view definition with rule-based view selection support.
/// Contains an array of view entries, each with an optional rule for conditional selection.
/// Supports both old single-view format and new views array format for backward compatibility.
/// </summary>
public sealed class ViewDefinition : IViewDefinition
{
    /// <summary>
    /// Array of view entries with optional rules for conditional selection.
    /// Views are evaluated in order, and the first matching view is returned.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("views")]
    public IReadOnlyList<ViewEntry> Views { get; private set; } = Array.Empty<ViewEntry>();

    /// <summary>
    /// Parameterless constructor for EF Core deserialization.
    /// </summary>
    public ViewDefinition()
    {
        Views = Array.Empty<ViewEntry>();
    }

    /// <summary>
    /// Constructor that supports both old and new view formats for backward compatibility.
    /// If both formats are provided, the new 'views' format takes precedence.
    /// </summary>
    [JsonConstructor]
    private ViewDefinition(
        List<ViewEntry>? views,
        OldViewFormat? oldView)
    {
        // New format takes precedence if both are provided
        if (views != null && views.Count > 0)
        {
            // New format - use directly
            Views = views.AsReadOnly();
        }
        else if (oldView != null && oldView.View != null)
        {
            // Old format - convert to new format
            var entry = ViewEntry.CreateDefault(
                oldView.View,
                oldView.Extensions,
                oldView.LoadData);
            Views = new List<ViewEntry> { entry }.AsReadOnly();
        }
        else
        {
            Views = Array.Empty<ViewEntry>();
        }
    }

    /// <summary>
    /// Creates a new ViewDefinition with a single default view entry (no rule).
    /// </summary>
    public static ViewDefinition CreateDefault(
        Reference view,
        string[]? extensions = null,
        bool? loadData = null)
    {
        var entry = ViewEntry.CreateDefault(view, extensions, loadData);
        return new ViewDefinition([entry], null);
    }

    /// <summary>
    /// Creates a new ViewDefinition with multiple view entries.
    /// </summary>
    public static ViewDefinition CreateWithViews(params ViewEntry[] entries)
    {
        return new ViewDefinition(entries.ToList(), null);
    }
}