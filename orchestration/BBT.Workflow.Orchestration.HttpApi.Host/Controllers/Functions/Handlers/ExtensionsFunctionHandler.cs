using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>extensions</c> system function.
/// </summary>
public sealed class ExtensionsFunctionHandler(
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Extensions;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var input = new GetExtensionsInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Version = request.Parameters.Version,
            Extensions = request.Parameters.Extensions,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters
        };

        var result = await queryAppService.GetExtensionsAsync(input, cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
