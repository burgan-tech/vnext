using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>instance-correlation</c> system function.
/// </summary>
public sealed class InstanceCorrelationFunctionHandler(
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.InstanceCorrelation;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var input = new GetInstanceCorrelationInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance
        };

        var result = await queryAppService.GetInstanceCorrelationAsync(input, cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
