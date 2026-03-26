using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>hierarchy</c> system function.
/// </summary>
public sealed class HierarchyFunctionHandler(
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Hierarchy;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var input = new GetInstanceHierarchyInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance
        };

        var result = await queryAppService.GetInstanceHierarchyAsync(input, cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
