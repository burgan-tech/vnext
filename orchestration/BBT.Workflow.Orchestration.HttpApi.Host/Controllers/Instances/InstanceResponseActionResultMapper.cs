using BBT.Aether.Results;
using BBT.Workflow.HttpApi.Results;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// Maps instance operation results to HTTP responses. When a workflow output script ran
/// (<see cref="InstanceOutputBase.HasOutputResponse"/>), the mapped payload is returned directly
/// — with the script's status code and headers — bypassing the standard instance envelope,
/// mirroring <c>FunctionResponseActionResultMapper</c>. Otherwise delegates to
/// <see cref="WorkflowResultActionResultMapper"/>.
/// </summary>
internal static class InstanceResponseActionResultMapper
{
    private static readonly HashSet<string> RestrictedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "content-length",
        "content-type",         // ObjectResult negotiates this via output formatters
        "date",                 // server writes its own
        "host",                 // upstream internal hostname must not leak
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "server",               // orchestrator sets its own Server header
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade"
    };

    internal static IActionResult ToActionResult<T>(Result<T> result, HttpContext httpContext)
        where T : InstanceOutputBase
    {
        if (result.IsSuccess && result.Value is { HasOutputResponse: true } output)
        {
            ApplyHeaders(output.OutputHeaders, httpContext);

            // OutputData may be null → empty body with the configured/default status code.
            return new ObjectResult(output.OutputData)
            {
                StatusCode = output.OutputStatusCode ?? StatusCodes.Status200OK
            };
        }

        return WorkflowResultActionResultMapper.ToActionResult(result, httpContext);
    }

    private static void ApplyHeaders(Dictionary<string, string>? headers, HttpContext httpContext)
    {
        if (headers is null || headers.Count == 0)
            return;

        foreach (var (key, value) in headers)
        {
            if (RestrictedResponseHeaders.Contains(key))
                continue;

            httpContext.Response.Headers[key] = value;
        }
    }
}
