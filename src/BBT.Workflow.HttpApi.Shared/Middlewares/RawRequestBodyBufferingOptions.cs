namespace BBT.Workflow.Middlewares;

/// <summary>
/// Configuration options for <see cref="RawRequestBodyBufferingMiddleware"/>.
/// Bind from the <c>Kestrel:Limits</c> appsettings section so the raw-body cap
/// lives alongside <c>MaxRequestBodySize</c> and other Kestrel limits.
/// </summary>
public sealed class RawRequestBodyBufferingOptions
{
    /// <summary>appsettings section this class binds to.</summary>
    public const string SectionPath = "Kestrel:Limits";

    /// <summary>
    /// Maximum request body size in bytes. Shared with Kestrel's own <c>MaxRequestBodySize</c> limit —
    /// both are read from the same <c>Kestrel:Limits</c> section so a single config value governs both.
    /// Requests with a known <c>Content-Length</c> above this value skip raw-body capture.
    /// Requests with an unknown content length are read fully first, then capture is
    /// skipped if the actual body exceeds this limit.
    /// Default: 5 MB.
    /// </summary>
    public long MaxRequestBodySize { get; set; } = 10 * 1024 * 1024;
}
