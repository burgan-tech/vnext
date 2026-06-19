namespace BBT.Workflow.Monitor.Components.Filters;

/// <summary>
/// Optional filter parameters for the component summary list endpoint.
/// All fields are nullable; set fields are validated against the allowed list for the requested component type.
/// </summary>
public sealed class MonitorComponentFilterInput
{
    /// <summary>Lower bound for the component's first-publish timestamp (inclusive).</summary>
    public DateTime? CreatedAtGte { get; set; }

    /// <summary>Upper bound for the component's first-publish timestamp (inclusive).</summary>
    public DateTime? CreatedAtLte { get; set; }

    /// <summary>Lower bound for the component's last-update timestamp (inclusive).</summary>
    public DateTime? ModifiedAtGte { get; set; }

    /// <summary>Upper bound for the component's last-update timestamp (inclusive).</summary>
    public DateTime? ModifiedAtLte { get; set; }

    /// <summary>Tag list-contains filter (case-insensitive). Matches if the tag list contains this value.</summary>
    public string? TagsContains { get; set; }

    /// <summary>Flow-stream format version exact-match filter (e.g. "1.0.0").</summary>
    public string? FlowVersionEq { get; set; }

    /// <summary>Flow-stream format version contains filter (case-insensitive, e.g. "1.0").</summary>
    public string? FlowVersionContains { get; set; }

    /// <summary>
    /// Definition type discriminator exact-match filter.
    /// Valid for: sys-flows, sys-tasks, sys-schemas, sys-views, sys-extensions.
    /// </summary>
    public string? DefinitionType { get; set; }

    /// <summary>
    /// Display identifier exact-match filter (e.g. "form", "list").
    /// Valid for: sys-views only.
    /// </summary>
    public string? Display { get; set; }

    /// <summary>
    /// Renderer identifier exact-match filter (e.g. "default").
    /// Valid for: sys-views only.
    /// </summary>
    public string? Renderer { get; set; }

    /// <summary>
    /// Scope exact-match filter (e.g. "global", "domain").
    /// Valid for: sys-functions and sys-extensions.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Display-name exact-match filter (case-insensitive).
    /// Valid for: sys-mappings only.
    /// </summary>
    public string? NameEq { get; set; }

    /// <summary>
    /// Display-name contains filter (case-insensitive).
    /// Valid for: sys-mappings only.
    /// </summary>
    public string? NameContains { get; set; }

    /// <summary>Component key exact-match filter (case-insensitive). Available for all component types.</summary>
    public string? KeyEq { get; set; }

    /// <summary>Component key contains filter (case-insensitive). Available for all component types.</summary>
    public string? KeyContains { get; set; }

    /// <summary>Component semantic version exact-match filter (case-insensitive, e.g. "1.0.0").</summary>
    public string? VersionEq { get; set; }

    /// <summary>Component semantic version contains filter (case-insensitive, e.g. "1.0").</summary>
    public string? VersionContains { get; set; }

    /// <summary>Returns true when no filter field has been set.</summary>
    public bool IsEmpty => !SetFields().Any();

    /// <summary>Returns the canonical field names of all set (non-null) filter properties.</summary>
    internal IEnumerable<string> SetFields()
    {
        if (CreatedAtGte.HasValue)           yield return "createdAt";
        if (CreatedAtLte.HasValue)           yield return "createdAt";
        if (ModifiedAtGte.HasValue)          yield return "modifiedAt";
        if (ModifiedAtLte.HasValue)          yield return "modifiedAt";
        if (TagsContains is not null)        yield return "tags";
        if (FlowVersionEq is not null)       yield return "flowVersion";
        if (FlowVersionContains is not null) yield return "flowVersion";
        if (DefinitionType is not null)      yield return "definitionType";
        if (Display is not null)             yield return "display";
        if (Renderer is not null)            yield return "renderer";
        if (Scope is not null)               yield return "scope";
        if (NameEq is not null)              yield return "name";
        if (NameContains is not null)        yield return "name";
        if (KeyEq is not null)               yield return "key";
        if (KeyContains is not null)         yield return "key";
        if (VersionEq is not null)           yield return "version";
        if (VersionContains is not null)     yield return "version";
    }
}
