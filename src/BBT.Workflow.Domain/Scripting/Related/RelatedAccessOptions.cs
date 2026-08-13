namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Guardrails for related-instance access from mapping scripts.
/// Bound from the <c>Workflow:Scripting:RelatedAccess</c> configuration section.
/// </summary>
public sealed class RelatedAccessOptions
{
    /// <summary>Configuration section name this options class binds from.</summary>
    public const string SectionName = "Workflow:Scripting:RelatedAccess";

    /// <summary>
    /// Maximum number of distinct related instances a single ScriptContext may resolve.
    /// Memoized repeat reads do not count. Exceeding the cap throws
    /// <see cref="RelatedInstanceAccessException"/> — a script needing more than this is a design error.
    /// </summary>
    public int MaxResolutionsPerContext { get; set; } = 10;
}

/// <summary>
/// DI keys distinguishing the two <see cref="IRelatedInstanceReader"/> implementations behind the
/// routed reader.
/// </summary>
public static class RelatedReaderKeys
{
    /// <summary>Key of the same-domain reader.</summary>
    public const string Local = "related-instance-reader-local";

    /// <summary>Key of the cross-domain reader.</summary>
    public const string Remote = "related-instance-reader-remote";
}
