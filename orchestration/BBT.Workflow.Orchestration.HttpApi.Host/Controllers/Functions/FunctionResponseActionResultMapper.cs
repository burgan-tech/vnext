using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Functions;
using BBT.Workflow.Orchestration.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

internal static class FunctionResponseActionResultMapper
{
    public static IActionResult ToActionResult(
        Result<FunctionResponseOutput> result,
        HttpContext httpContext)
    {
        if (!result.IsSuccess)
            return result.ToActionResult(httpContext);

        var output = result.Value!;
        ResponseOutputWriter.ApplyHeaders(output.Headers, httpContext);

        var objectResult = new ObjectResult(output.Data)
        {
            StatusCode = output.StatusCode ?? StatusCodes.Status200OK
        };
        objectResult.ContentTypes.Add(ResponseOutputWriter.ResolveContentType(output.Headers));

        return objectResult;
    }
}
