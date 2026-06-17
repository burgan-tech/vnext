using System.Text;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Buffers the raw request body for body-bearing requests so the original, unmodified payload can be
/// exposed to mappings for signature verification (JWS / mTLS). Enables buffering, reads the body once
/// into a string stored on <see cref="HttpContext.Items"/>, then rewinds the stream so downstream
/// <c>[FromBody]</c> model binding still works. Requests whose known content length exceeds the cap are skipped.
/// </summary>
public sealed class RawRequestBodyBufferingMiddleware(RequestDelegate next)
{
    /// <summary>The <see cref="HttpContext.Items"/> key under which the captured raw request body is stored.</summary>
    public const string RawBodyItemsKey = "__vnext.RawRequestBody";

    // Cap to protect memory; these JSON APIs carry small payloads.
    private const long MaxBufferedBytes = 1024 * 1024; // 1 MB

    /// <summary>
    /// Captures the raw body (when applicable) and invokes the next middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldCapture(context.Request))
        {
            context.Request.EnableBuffering();

            using var reader = new StreamReader(
                context.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);

            var raw = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            context.Items[RawBodyItemsKey] = raw;
        }

        await next(context);
    }

    private static bool ShouldCapture(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)
            && !HttpMethods.IsPut(request.Method)
            && !HttpMethods.IsPatch(request.Method)
            && !HttpMethods.IsDelete(request.Method))
            return false;

        // Skip when a known content length exceeds the cap; unknown lengths are buffered
        // (EnableBuffering spills to disk beyond its own threshold, so memory stays bounded).
        return request.ContentLength is null or <= MaxBufferedBytes;
    }
}
