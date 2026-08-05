using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Functions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>catalog</c> system function: lists the functions the instance's workflow declares,
/// each linked to its <c>info</c> endpoint. This is what the state response's <c>functions.href</c>
/// points at, so the state body carries only a flag and a link rather than the resolved list.
/// </summary>
public sealed class CatalogFunctionHandler(
    IFunctionInfoAppService functionInfoAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Catalog;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var result = await functionInfoAppService.GetCatalogByInstanceAsync(
            request.Domain,
            request.Workflow,
            request.Instance,
            request.Headers,
            request.QueryParameters,
            cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
