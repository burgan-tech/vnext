using System.Text.Json;
using BBT.Workflow.Monitor.Components.DTOs;

namespace BBT.Workflow.Monitor.Components.Filters;

/// <summary>
/// Pure, stateless helper that applies an in-memory filter to a component summary list.
/// Called after <c>ProjectToSummary</c> and before pagination in <c>GetComponentSummaryAsync</c>.
/// </summary>
public static class MonitorComponentFilter
{
    /// <summary>
    /// Filters <paramref name="items"/> according to <paramref name="filter"/>.
    /// Only set (non-null) filter fields are evaluated; unset fields are skipped.
    /// </summary>
    public static IEnumerable<MonitorComponentSummaryItem> Apply(
        IEnumerable<MonitorComponentSummaryItem> items,
        MonitorComponentFilterInput filter)
    {
        if (filter.CreatedAtGte.HasValue)
            items = items.Where(x => x.CreatedAt.HasValue && x.CreatedAt.Value >= filter.CreatedAtGte.Value);

        if (filter.CreatedAtLte.HasValue)
            items = items.Where(x => x.CreatedAt.HasValue && x.CreatedAt.Value <= filter.CreatedAtLte.Value);

        if (filter.ModifiedAtGte.HasValue)
            items = items.Where(x => x.ModifiedAt.HasValue && x.ModifiedAt.Value >= filter.ModifiedAtGte.Value);

        if (filter.ModifiedAtLte.HasValue)
            items = items.Where(x => x.ModifiedAt.HasValue && x.ModifiedAt.Value <= filter.ModifiedAtLte.Value);

        if (filter.TagsContains is not null)
            items = items.Where(x => x.Tags is not null &&
                x.Tags.Contains(filter.TagsContains, StringComparer.OrdinalIgnoreCase));

        if (filter.FlowVersionEq is not null)
            items = items.Where(x => string.Equals(x.FlowVersion, filter.FlowVersionEq, StringComparison.OrdinalIgnoreCase));

        if (filter.FlowVersionContains is not null)
            items = items.Where(x => x.FlowVersion is not null &&
                x.FlowVersion.Contains(filter.FlowVersionContains, StringComparison.OrdinalIgnoreCase));

        if (filter.DefinitionType is not null)
            items = items.Where(x => MatchDefinitionType(x.Type, filter.DefinitionType));

        if (filter.Display is not null)
            items = items.Where(x => string.Equals(x.Display, filter.Display, StringComparison.OrdinalIgnoreCase));

        if (filter.Renderer is not null)
            items = items.Where(x => string.Equals(x.Renderer, filter.Renderer, StringComparison.OrdinalIgnoreCase));

        if (filter.Scope is not null)
            items = items.Where(x => string.Equals(x.Scope, filter.Scope, StringComparison.OrdinalIgnoreCase));

        if (filter.NameEq is not null)
            items = items.Where(x => string.Equals(x.Name, filter.NameEq, StringComparison.OrdinalIgnoreCase));

        if (filter.NameContains is not null)
            items = items.Where(x => x.Name is not null &&
                x.Name.Contains(filter.NameContains, StringComparison.OrdinalIgnoreCase));

        if (filter.KeyEq is not null)
            items = items.Where(x => string.Equals(x.Key, filter.KeyEq, StringComparison.OrdinalIgnoreCase));

        if (filter.KeyContains is not null)
            items = items.Where(x => x.Key is not null &&
                x.Key.Contains(filter.KeyContains, StringComparison.OrdinalIgnoreCase));

        if (filter.VersionEq is not null)
            items = items.Where(x => string.Equals(x.Version, filter.VersionEq, StringComparison.OrdinalIgnoreCase));

        if (filter.VersionContains is not null)
            items = items.Where(x => x.Version is not null &&
                x.Version.Contains(filter.VersionContains, StringComparison.OrdinalIgnoreCase));

        return items;
    }

    private static bool MatchDefinitionType(JsonElement? type, string value)
    {
        if (type is null) return false;
        var el = type.Value;
        if (el.ValueKind != JsonValueKind.String) return false;
        return string.Equals(el.GetString(), value, StringComparison.OrdinalIgnoreCase);
    }
}
