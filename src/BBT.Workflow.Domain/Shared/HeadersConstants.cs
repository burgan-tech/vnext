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

    /// <summary>Request header carrying the caller's preferred languages (RFC 7231).</summary>
    public const string AcceptLanguage = "Accept-Language";

    /// <summary>
    /// Internal file-read response header: the x-storage handle (name, mimeType, size, eTag, owner) as base64 of
    /// its UTF-8 JSON, so a cross-domain reader gets the metadata without parsing other headers.
    /// </summary>
    public const string XFileHandle = "X-File-Handle";
}