namespace BBT.Workflow.Domain.Shared;

/// <summary>
/// HTTP header name constants for workflow API.
/// </summary>
public static class HeadersConstants
{
    /// <summary>Response header for representation ETag (cache validation).</summary>
    public const string ETag = "ETag";

    /// <summary>Response header for entity/row version (concurrency and write operations).</summary>
    public const string XEntityETag = "X-Entity-ETag";
}