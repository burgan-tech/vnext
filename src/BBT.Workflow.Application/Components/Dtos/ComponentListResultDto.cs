namespace BBT.Workflow.Components.Dtos;

/// <summary>
/// Paged list of <see cref="ComponentSummaryDto"/> for a domain, optionally scoped to a
/// single component type.
/// </summary>
public sealed class ComponentListResultDto
{
    /// <summary>The component summaries on the current page.</summary>
    public required IReadOnlyList<ComponentSummaryDto> Items { get; init; }

    /// <summary>The 1-based page number.</summary>
    public required int Page { get; init; }

    /// <summary>The requested page size.</summary>
    public required int PageSize { get; init; }

    /// <summary>The total number of matching components across all pages.</summary>
    public required int TotalCount { get; init; }
}
