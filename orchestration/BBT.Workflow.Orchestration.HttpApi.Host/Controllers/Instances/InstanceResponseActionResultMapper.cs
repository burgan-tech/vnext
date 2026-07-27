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
    /// <remarks>
    /// When <paramref name="async"/> is <c>true</c> the request runs durably in the background
    /// (<c>sync=false</c>): a successful result is returned as <c>202 Accepted</c> instead of
    /// <c>200 OK</c>, since the work has been accepted for processing rather than completed.
    /// Ignored for the custom output-response path (where the author sets the status explicitly)
    /// and for error results.
    /// </remarks>
    internal static IActionResult ToActionResult<T>(Result<T> result, HttpContext httpContext, bool async = false)
        where T : InstanceOutputBase
    {
        if (result.IsSuccess && result.Value is { HasOutputResponse: true } output)
        {
            ResponseOutputWriter.ApplyHeaders(output.OutputHeaders, httpContext);

            // OutputData may be null → empty body with the configured/default status code.
            var objectResult = new ObjectResult(output.OutputData)
            {
                StatusCode = output.OutputStatusCode ?? StatusCodes.Status200OK
            };
            objectResult.ContentTypes.Add(ResponseOutputWriter.ResolveContentType(output.OutputHeaders));

            return objectResult;
        }

        var actionResult = WorkflowResultActionResultMapper.ToActionResult(result, httpContext);

        // Durable (sync=false) success → 202 Accepted: the work is queued, not finished.
        if (async && result.IsSuccess &&
            actionResult is ObjectResult { StatusCode: null or StatusCodes.Status200OK } acceptedResult)
        {
            acceptedResult.StatusCode = StatusCodes.Status202Accepted;
        }

        return actionResult;
    }
}
