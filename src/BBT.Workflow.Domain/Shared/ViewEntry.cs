using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Definitions;

namespace BBT.Workflow;

/// <summary>
/// Represents a single view entry in a view definition with optional rule-based selection.
/// Each entry can have a conditional rule that determines when this view should be selected.
/// </summary>
public sealed class ViewEntry
{
    /// <summary>
    /// Optional rule for conditional view selection.
    /// If null or not defined, this entry acts as a fallback/default view.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("rule")]
    public ScriptCode? Rule { get; private set; }

    /// <summary>
    /// Reference to the view to be loaded.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("view")]
    public Reference View { get; private set; } = null!;

    /// <summary>
    /// Optional extensions to be loaded with the view.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("extensions")]
    public string[]? Extensions { get; private set; }

    /// <summary>
    /// Whether to load data when loading this view.
    /// If true, data will be loaded; otherwise only the view is loaded.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("loadData")]
    public bool? LoadData { get; private set; }

    /// <summary>
    /// Parameterless constructor for EF Core deserialization.
    /// </summary>
    public ViewEntry()
    {
    }

    [JsonConstructor]
    private ViewEntry(
        ScriptCode? rule,
        Reference view,
        string[]? extensions,
        bool? loadData)
    {
        Rule = rule;
        View = Check.NotNull(view, nameof(View));
        Extensions = extensions;
        LoadData = loadData;
    }

    /// <summary>
    /// Creates a new ViewEntry with a rule for conditional selection.
    /// </summary>
    public static ViewEntry CreateWithRule(
        Reference view,
        ScriptCode rule,
        string[]? extensions = null,
        bool? loadData = null)
    {
        return new ViewEntry(rule, view, extensions, loadData);
    }

    /// <summary>
    /// Creates a new ViewEntry without a rule (fallback/default view).
    /// </summary>
    public static ViewEntry CreateDefault(
        Reference view,
        string[]? extensions = null,
        bool? loadData = null)
    {
        return new ViewEntry(null, view, extensions, loadData);
    }
}
