using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>master</c> system function. Returns the flow-level master schema the instance is
/// bound to, forwarding to the active subflow instance when one is present.
/// </summary>
public sealed class MasterFunctionHandler(
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Master;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var input = new GetMasterInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Version = request.Parameters.Version,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Roles = request.CurrentUser.ResolveCallerRoles(request.Headers),
        };

        var result = await queryAppService.GetMasterAsync(input, cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
