using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>schema</c> system function.
/// </summary>
public sealed class SchemaFunctionHandler(
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Schema;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var input = new GetSchemaInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Version = request.Parameters.Version
        };

        var result = await queryAppService.GetSchemaAsync(
            input,
            request.Parameters.TransitionKey,
            cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
