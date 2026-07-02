namespace BBT.Workflow.Monitor.Common.DTOs;

/// <summary>
/// Standard paged response envelope for all monitor list endpoints.
/// </summary>
/// <typeparam name="T">Item type returned in the list.</typeparam>
public sealed class MonitorPagedResponse<T>
{
    /// <summary>
    /// Pagination metadata. Present only when the response contains a paginated list.
    /// Absent (null/omitted) for grouped or aggregated results.
    /// </summary>
    public MonitorPaginationInfo? Pagination { get; set; }

    /// <summary>Items in the current page, or group summaries when groupBy is active.</summary>
    public List<T> Items { get; set; } = [];
}

/// <summary>
/// Cursor-free pagination metadata included in <see cref="MonitorPagedResponse{T}"/>.
/// </summary>
public sealed class MonitorPaginationInfo
{
    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Number of items requested per page.</summary>
    public int PageSize { get; set; }

    /// <summary>Whether a next page exists.</summary>
    public bool HasNext { get; set; }
}
