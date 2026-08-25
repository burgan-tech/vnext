using BBT.Workflow.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Middleware that automatically records comprehensive HTTP metrics for all requests.
/// This middleware captures request duration, response size, error rates, and other HTTP metrics.
/// </summary>
public sealed class HttpMetricsMiddleware(
    RequestDelegate next,
    IWorkflowMetrics workflowMetrics,
    ILogger<HttpMetricsMiddleware> logger)
{
    /// <summary>
    /// Processes HTTP request and records comprehensive metrics
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var method = context.Request.Method;
        var endpoint = GetNormalizedEndpoint(context.Request.Path);

        // Wrap the response stream in a pass-through counter: writes flow straight to the
        // client while the byte count accumulates for the response-size metric. The response
        // is never buffered, so streaming, chunking and time-to-first-byte are preserved and
        // large bodies never land on the LOH just to be measured.
        var originalBodyStream = context.Response.Body;
        var countingBody = new CountingStream(originalBodyStream);
        context.Response.Body = countingBody;

        try
        {
            await next(context);

            stopwatch.Stop();
            var statusCode = context.Response.StatusCode.ToString();

            // Record successful request metrics
            workflowMetrics.RecordHttpRequest(method, endpoint, statusCode);
            workflowMetrics.RecordHttpRequestDuration(method, endpoint, statusCode, stopwatch.Elapsed.TotalSeconds);
            workflowMetrics.RecordHttpResponseSize(method, endpoint, statusCode, countingBody.BytesWritten);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var statusCode = context.Response.StatusCode.ToString();
            var errorType = ex.GetType().Name;

            // Record HTTP error metrics
            workflowMetrics.RecordHttpRequest(method, endpoint, statusCode);
            workflowMetrics.RecordHttpError(method, endpoint, errorType);
            workflowMetrics.RecordHttpRequestDuration(method, endpoint, statusCode, stopwatch.Elapsed.TotalSeconds);

            // Record workflow error and exception metrics
            workflowMetrics.RecordWorkflowError("system", "high", "HttpMiddleware");
            workflowMetrics.RecordWorkflowException(errorType, "HttpMiddleware", $"{method} {endpoint}");

            logger.LogWarning(ex, "HTTP request failed: {Method} {Endpoint} with {ErrorType} after {Duration}ms",
                method, endpoint, errorType, stopwatch.ElapsedMilliseconds);

            throw;
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    /// <summary>
    /// Normalizes endpoint paths to reduce cardinality for metrics.
    /// Replaces path parameters with placeholders to prevent metric explosion.
    /// </summary>
    private static string GetNormalizedEndpoint(PathString path)
    {
        var pathValue = path.Value ?? "/";

        // Skip metrics endpoint to avoid self-monitoring
        if (pathValue.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return "/metrics";
        }

        // Skip health checks
        if (pathValue.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            return "/health";
        }

        // Normalize API paths by replacing common variable patterns
        return NormalizeApiPath(pathValue);
    }

    /// <summary>
    /// Precompiled normalization patterns: compiled once instead of interpreting five regexes
    /// (and allocating the pattern array) on every request.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] NormalizationPatterns =
    [
        // Instance IDs (GUIDs)
        (new Regex(@"/instances/[0-9a-fA-F-]{36}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "/instances/{instanceId}"),

        // Workflow/domain names (replace specific names with placeholder)
        (new Regex(@"/api/v[\d\.]+/([^/]+)/workflows/([^/]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "/api/v{version}/{domain}/workflows/{workflow}"),

        // Version patterns
        (new Regex(@"/api/v[\d\.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "/api/v{version}"),

        // Generic GUID patterns
        (new Regex(@"/[0-9a-fA-F-]{36}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "/{id}"),

        // Numeric IDs
        (new Regex(@"/\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "/{id}")
    ];

    /// <summary>
    /// Normalizes API paths by replacing path parameters with placeholders
    /// to prevent metric cardinality explosion.
    /// </summary>
    private static string NormalizeApiPath(string path)
    {
        var normalizedPath = path;
        foreach (var (pattern, replacement) in NormalizationPatterns)
        {
            normalizedPath = pattern.Replace(normalizedPath, replacement);
        }

        return normalizedPath;
    }

    /// <summary>
    /// Write-only pass-through stream that counts the bytes written to the inner stream.
    /// </summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            BytesWritten++;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }

        // The inner stream is the response body owned by the server; never dispose it here.
        protected override void Dispose(bool disposing)
        {
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
