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
        string content,
        string display,
        LanguageLabel[]? labels,
        PlatformOverrides? platformOverrides) : this()
    {
        Type = type;
        Content = Check.NotNullOrWhiteSpace(content, nameof(Content));
        Display = display;
        Labels = labels ?? [];
        PlatformOverrides = platformOverrides;
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
    public string Content { get; private set; } = string.Empty;
    
    /// <summary>
    /// Display
    /// </summary>
    public string Display { get; private set; } = string.Empty;
    
    /// <summary>
    /// Display
    /// </summary>
    public LanguageLabel[]? Labels { get; private set; } = [];
    
    /// <summary>
    /// Platform-specific overrides for view content.
    /// This property is deprecated (Issue #56). Use rule-based view selection in State/Transition view definitions instead.
    /// </summary>
    [Obsolete("PlatformOverrides is deprecated. Use rule-based view selection in State/Transition view definitions instead. See Issue #56.")]
    public PlatformOverrides? PlatformOverrides { get; private set; }
    
    public string ComponentKey => RuntimeSysSchemaInfo.Views;

    public static string GenerateCacheKey(
        string domain,
        string flow,
        string key,
        string version)
    {
        return $"{nameof(View)}:{domain}:{flow}:{key}:{version}";
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