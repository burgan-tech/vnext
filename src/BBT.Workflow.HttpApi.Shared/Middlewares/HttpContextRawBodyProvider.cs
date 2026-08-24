using BBT.Workflow.Scripting;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// HTTP-host <see cref="IRequestRawBodyProvider"/>. Resolves the raw request body from the ambient job
/// scope first — inside a background job the surrounding HTTP request is the Dapr job-callback transport,
/// not the original payload — then falls back to the live request body captured by
/// <see cref="RawRequestBodyBufferingMiddleware"/>.
/// </summary>
public sealed class HttpContextRawBodyProvider(IHttpContextAccessor httpContextAccessor) : IRequestRawBodyProvider
{
    /// <inheritdoc />
    public string? GetRawBody()
    {
        var ambient = RawBodyExecutionScope.Current;
        if (ambient != null)
            return ambient;

        var items = httpContextAccessor.HttpContext?.Items;
        if (items != null
            && items.TryGetValue(RawRequestBodyBufferingMiddleware.RawBodyItemsKey, out var value))
        {
            return value switch
            {
                // Middleware stores the capture lazily; the UTF-16 conversion happens here,
                // on first actual read, and is cached inside the capture.
                RawRequestBodyCapture capture => capture.Text,
                string text => text,
                _ => null
            };
        }

        return null;
    }
}
