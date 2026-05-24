using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Functions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

internal static class FunctionResponseActionResultMapper
{
    private static readonly HashSet<string> RestrictedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "content-length",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade"
    };

    public static IActionResult ToActionResult(
        Result<FunctionResponseOutput> result,
        HttpContext httpContext)
    {
        if (!result.IsSuccess)
            return result.ToActionResult(httpContext);

        var output = result.Value!;
        ApplyHeaders(output, httpContext);

        return new ObjectResult(output.Data)
        {
            StatusCode = output.StatusCode ?? StatusCodes.Status200OK
        };
    }

    private static void ApplyHeaders(FunctionResponseOutput output, HttpContext httpContext)
    {
        if (output.Headers is null || output.Headers.Count == 0)
            return;

        foreach (var (key, value) in output.Headers)
        {
            if (RestrictedResponseHeaders.Contains(key))
                continue;

            httpContext.Response.Headers[key] = value;
        }
    }
}
