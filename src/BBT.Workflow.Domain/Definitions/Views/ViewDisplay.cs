using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Per-client-mode display declaration for a <see cref="View"/>.
/// A view may declare a display hint for SDI (single-document interface) clients,
/// for MDI (multi-document interface) clients, or both.
/// </summary>
/// <remarks>
/// In component JSON this is authored either as a bare string (legacy shape, interpreted as
/// <see cref="Sdi"/>) or as an object <c>{ "sdi": "...", "mdi": "..." }</c>.
/// <see cref="ViewDisplayJsonConverter"/> handles both shapes.
/// Well-known values are listed in <see cref="ViewDisplayMode"/>.
/// </remarks>
public sealed class ViewDisplay
{
    /// <summary>
    /// Creates a display declaration.
    /// </summary>
    /// <param name="sdi">Display hint for SDI clients. Well-known values in <see cref="ViewDisplayMode.Sdi"/>.</param>
    /// <param name="mdi">Display hint for MDI clients. Well-known values in <see cref="ViewDisplayMode.Mdi"/>.</param>
    [JsonConstructor]
    public ViewDisplay(string? sdi, string? mdi)
    {
        Sdi = sdi;
        Mdi = mdi;
    }

    /// <summary>
    /// Display hint for SDI (single-document interface) clients, e.g. <c>full-page</c>, <c>popup</c>.
    /// This is the value legacy string-form <c>display</c> declarations map to.
    /// </summary>
    [JsonPropertyName("sdi")]
    public string? Sdi { get; }

    /// <summary>
    /// Display hint for MDI (multi-document interface) clients, e.g. <c>tab</c>, <c>window</c>.
    /// Null when the view does not declare an MDI presentation.
    /// </summary>
    [JsonPropertyName("mdi")]
    public string? Mdi { get; }

    /// <summary>
    /// True when neither mode declares a display hint.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Sdi) && string.IsNullOrWhiteSpace(Mdi);

    /// <summary>
    /// Creates a declaration from a legacy string display value, which is always an SDI hint.
    /// </summary>
    public static ViewDisplay FromSdi(string? sdi) => new(sdi, null);
}
