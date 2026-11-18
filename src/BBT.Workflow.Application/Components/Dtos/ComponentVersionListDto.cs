namespace BBT.Workflow.Components.Dtos;

/// <summary>
/// The published versions of one component and how a versionless request resolves among them — what a
/// domain team needs to explain which version a new instance starts on, without database access.
/// </summary>
/// <remarks>
/// Read straight from the database, never from the component cache: <see cref="Latest"/> is the answer the
/// cache must give for a versionless request once its entries expire, so comparing it with
/// <c>GET {domain}/components/{type}/{key}</c> (the cached answer) shows a stale cache entry.
/// </remarks>
public sealed class ComponentVersionListDto
{
    /// <summary>The component type URL token (e.g. <c>workflows</c>).</summary>
    public required string Type { get; init; }

    /// <summary>The runtime component-type key (e.g. <c>sys-flows</c>).</summary>
    public required string ComponentTypeKey { get; init; }

    /// <summary>The domain the component belongs to.</summary>
    public required string Domain { get; init; }

    /// <summary>The component key.</summary>
    public required string Key { get; init; }

    /// <summary>The number of distinct published versions version resolution chooses from.</summary>
    public required int TotalVersions { get; init; }

    /// <summary>
    /// The default-version pointer from <c>sys-version-registry</c>; <c>null</c> when none is set or the
    /// component type has no default-version policy (only workflows have one).
    /// </summary>
    public string? DefaultVersion { get; init; }

    /// <summary>What a request with no version (or <c>latest</c>) resolves to from the published versions.</summary>
    public required ComponentLatestResolutionDto Latest { get; init; }

    /// <summary>
    /// The published versions on the current page, one per version (as <see cref="TotalVersions"/> counts them),
    /// newest publish first.
    /// </summary>
    public required IReadOnlyList<ComponentVersionItemDto> Items { get; init; }

    /// <summary>The 1-based page number.</summary>
    public required int Page { get; init; }

    /// <summary>The page size used.</summary>
    public required int PageSize { get; init; }

    /// <summary>Whether a next page exists.</summary>
    public required bool HasNext { get; init; }
}
