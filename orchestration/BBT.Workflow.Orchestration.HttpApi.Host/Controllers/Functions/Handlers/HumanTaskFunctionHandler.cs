using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>human-task</c> function at domain level.
/// Returns active instances with Human state subtype assigned to the current user.
/// </summary>
public sealed class HumanTaskFunctionHandler(
    IInstanceQueryAppService queryAppService) : IDomainFunctionHandler
{
    public string FunctionType => FunctionTypeConst.HumanTask;

    public async Task<IActionResult> HandleAsync(
        DomainFunctionRequest request, CancellationToken cancellationToken)
    {
        var result = await queryAppService.GetHumanTaskInstancesAsync(
            request.Domain, cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
