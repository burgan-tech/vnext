using BBT.Workflow.Authorization;
using BBT.Workflow.CurrentUser;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Headers;

/// <summary>
/// Reads the caller's <c>position</c> from the ambient HTTP request.
/// <para>
/// The value is taken from the request header rather than from the headers dictionary the
/// authorization surfaces thread around, because several of those call sites pass no headers at all —
/// reading from them would let whichever surface asked first freeze a null position for the request.
/// </para>
/// <para>
/// When <c>position</c> lands on <c>ICurrentUser</c>, this class is the only thing that changes.
/// </para>
/// </summary>
internal sealed class HttpContextCallerPositionAccessor(IHttpContextAccessor httpContextAccessor)
    : ICallerPositionAccessor
{
    /// <inheritdoc />
    public string? GetPosition()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
            return null;

        return httpContext.Request.Headers.TryGetValue(CurrentUserHeaderKeys.Position, out var value)
            ? value.ToString() is { Length: > 0 } position ? position : null
            : null;
    }
}
