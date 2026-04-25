using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

/// <summary>
/// Response for instance list queries with optional groups
/// When groupBy is used, items array contains GroupSummary objects instead of instance items
/// </summary>
/// <typeparam name="T">Item type (typically GetInstanceOutput)</typeparam>
public sealed class InstanceListWithGroupsResponse<T>
{
    /// <summary>
    /// HATEOAS links for pagination
    /// </summary>
    [JsonPropertyName("links")]
    public object? Links { get; set; }

    /// <summary>
    /// List of items (instances or groups when groupBy is used)
    /// </summary>
    [JsonPropertyName("items")]
    public List<object> Items { get; set; } = new();

    /// <summary>
    /// Creates response from HateoasPagedList
    /// </summary>
    public static InstanceListWithGroupsResponse<T> FromPagedList(HateoasPagedList<T> pagedList, List<GroupSummary>? groups = null)
    {
        // If groups are provided, populate items with groups; otherwise use paged list items
        if (groups is { Count: > 0 })
        {
            return new InstanceListWithGroupsResponse<T>
            {
                Links = null, // Links will be set by Controller using linkGenerator.CreateHateoasResult
                Items = groups.Cast<object>().ToList()
            };
        }

        return new InstanceListWithGroupsResponse<T>
        {
            Links = null, // Links will be set by Controller using linkGenerator.CreateHateoasResult
            Items = pagedList.Items.Cast<object>().ToList()
        };
    }

    /// <summary>
    /// Creates response from groups directly
    /// </summary>
    public static InstanceListWithGroupsResponse<T> FromGroups(List<GroupSummary> groups)
    {
        return new InstanceListWithGroupsResponse<T>
        {
            Links = null, // Links will be set by Controller using linkGenerator.CreateHateoasResult
            Items = groups.Cast<object>().ToList()
        };
    }

    /// <summary>
    /// Converts to HateoasPagedList for backward compatibility
    /// </summary>
    /// <param name="pageSize">Page size</param>
    /// <param name="currentPage">Current page number (defaults to 1)</param>
    /// <param name="totalItems">Total number of items across all pages (defaults to current page count if not provided)</param>
    public HateoasPagedList<T> ToPagedList(int pageSize, int currentPage = 1, int? totalItems = null)
    {
        var typedItems = Items.OfType<T>().ToList();
        var total = totalItems ?? typedItems.Count;
        var hasNext = (currentPage * pageSize) < total;
        return new HateoasPagedList<T>(typedItems, currentPage, total, hasNext);
    }
}

