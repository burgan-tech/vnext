using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BBT.Aether;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Definitions;

/// <summary>
/// View definition
/// </summary>
public sealed class View : IDomainEntity, IViewReference, IReferenceSetter
{
    private View()
    {
        Flow = RuntimeSysSchemaInfo.Views;
    }

    [JsonConstructor]
    private View(
        ViewType type,
        object content,
        string display,
        LanguageLabel[]? labels) : this()
    {
        Type = type;
        Content = content;
        Display = display;
        Labels = labels ?? [];
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// It is the information on which stream the record is located.
    /// </summary>
    public string Flow { get; init; }

    /// <summary>
    /// Information about which domain the flow is working on and which domain it belongs to.
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    public string Version { get; private set; }

    /// <summary>
    /// <see cref="ViewType"/>
    /// </summary>
    public ViewType Type { get; private set; }

    /// <summary>
    /// Semantic Version
    /// </summary>
    public string SemanticVersion => Regex.Match(Version, @"^([^+]+)").Groups[1].Value;

    /// <summary>
    /// Content
    /// </summary>
    public object Content { get; private set; } = string.Empty;
    
    /// <summary>
    /// Display
    /// </summary>
    public string Display { get; private set; } = string.Empty;
    
    /// <summary>
    /// Display
    /// </summary>
    public LanguageLabel[]? Labels { get; private set; } = [];
    
    public string ComponentKey => RuntimeSysSchemaInfo.Views;

    public static string GenerateCacheKey(
        string domain,
        string flow,
        string key,
        string version)
    {
        return $"{nameof(View)}:{domain}:{flow}:{key}:{version}";
    }

    /// <summary>
    /// Returns content typed by <see cref="Type"/>: for Json, DeepLink, Http, URN attempts JSON parse (on failure returns original string);
    /// for Html, Markdown returns the content string. Used when exposing view content (e.g. view function response).
    /// </summary>
    /// <returns>Parsed JSON as <see cref="JsonElement"/> for JSON-structured types when parseable; otherwise the original content string.</returns>
    public object GetContentAsTyped()
    {
        return GetContentAsTypedFromObject(Content, Type.ToString());
    }

    /// <summary>
    /// Returns content typed by view type string: for Json, DeepLink, Http, URN attempts JSON parse (on failure returns original string);
    /// for Html, Markdown or other types returns the content string. Shared logic for instance and remote view content.
    /// </summary>
    /// <param name="content">Raw view content (e.g. JSON string or markup).</param>
    /// <param name="type">View type name (e.g. Json, Html, Markdown).</param>
    /// <returns>Parsed JSON as <see cref="JsonElement"/> for JSON-structured types when parseable; otherwise the original content string.</returns>
    public static object GetContentAsTypedFromObject(object? content, string? type)
    {
        if (content == null)
            return string.Empty;

        var typeUpper = (type ?? string.Empty).Trim().ToUpperInvariant();
        if (typeUpper is "JSON" or "DEEPLINK" or "HTTP" or "URN")
        {
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(content.ToString() ?? string.Empty);
            }
            catch (JsonException)
            {
                return content;
            }
        }

        return content;
    }

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), ViewConstants.MaxKeyLength);
    }

    private void SetDomain(string domain)
    {
        Domain = Check.NotNullOrWhiteSpace(domain, nameof(Domain), WorkflowConstants.MaxDomainLength);
    }

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    public void SetReference(IReference reference)
    {
        SetKey(reference.Key);
        SetDomain(reference.Domain);
        SetVersion(reference.Version);
    }
}