using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>view</c> system function.
/// </summary>
public sealed class ViewFunctionHandler(
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.View;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var input = new GetViewInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Version = request.Parameters.Version,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters
        };

        var result = await queryAppService.GetViewAsync(
            input,
            request.Parameters.TransitionKey ?? string.Empty,
            cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
