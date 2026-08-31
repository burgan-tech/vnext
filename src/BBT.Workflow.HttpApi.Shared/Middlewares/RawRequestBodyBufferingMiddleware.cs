using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Reads the raw request body into memory and stores the payload on <see cref="HttpContext.Items"/> for
/// signature verification (JWS / mTLS), then replaces the body stream so downstream
/// <c>[FromBody]</c> model binding still works. Requests whose known content length exceeds the cap are skipped.
/// </summary>
public sealed class RawRequestBodyBufferingMiddleware(
    RequestDelegate next,
    IOptions<RawRequestBodyBufferingOptions> options)
{
    /// <summary>The <see cref="HttpContext.Items"/> key under which the captured raw request body is stored.</summary>
    public const string RawBodyItemsKey = "__vnext.RawRequestBody";

    private readonly long _maxBufferedBytes = options.Value.MaxRequestBodySize;

    /// <summary>
    /// Captures the raw body (when applicable) and invokes the next middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldCapture(context.Request))
        {
            // Read directly into a MemoryStream instead of using EnableBuffering/FileBufferingReadStream,
            // which spills to /tmp when the buffer threshold is exceeded. /tmp is read-only in some
            // container environments. Dapr's CloudEventsMiddleware may also have already called
            // EnableBuffering with the default 30 KB threshold, making our threshold override a no-op.
            //
            // GetBuffer() is used instead of ToArray() to avoid a second byte[] allocation — ToArray()
            // copies the internal buffer, while GetBuffer() returns a direct reference to it.
            // ms must be kept alive (not disposed here) because replacementBody shares the same buffer.
            var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms, context.RequestAborted);

            // For requests with unknown Content-Length the cap is enforced post-read.
            // Kestrel's MaxRequestBodySize bounds the absolute worst-case allocation above this limit.
            if (ms.Length > _maxBufferedBytes)
            {
                ms.Position = 0;
                context.Request.Body = ms;
                context.Response.RegisterForDispose(ms);
            }
            else
            {
                var rawBuffer = ms.GetBuffer();
                var rawLength = (int)ms.Length; // safe: ms.Length <= _maxBufferedBytes (default 10 MB)

                // Store the capture lazily: decoding UTF-8 into a UTF-16 string roughly doubles the
                // payload's memory (LOH for large bodies), and most requests never read the raw body
                // (only script contexts that access RawBody do). The string is materialized on first
                // read and cached inside the capture, so consumers pay the conversion at most once.
                context.Items[RawBodyItemsKey] = new RawRequestBodyCapture(rawBuffer, rawLength);

                // Replace body with a read-only view over the same buffer — no extra allocation.
                var replacementBody = new MemoryStream(rawBuffer, 0, rawLength, writable: false);
                context.Response.RegisterForDispose(replacementBody);
                context.Response.RegisterForDispose(ms);
                context.Request.Body = replacementBody;
            }
        }

        await next(context);
    }

    private bool ShouldCapture(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)
            && !HttpMethods.IsPut(request.Method)
            && !HttpMethods.IsPatch(request.Method)
            && !HttpMethods.IsDelete(request.Method))
            return false;

        // Skip when a known content length exceeds the cap to keep memory bounded.
        // Unknown lengths (null) are allowed through; the post-read cap check in InvokeAsync handles them.
        return request.ContentLength is null || request.ContentLength <= _maxBufferedBytes;
    }
}
