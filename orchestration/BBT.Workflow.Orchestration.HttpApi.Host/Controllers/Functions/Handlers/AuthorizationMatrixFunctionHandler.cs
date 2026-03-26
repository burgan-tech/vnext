using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>permissions</c> (authorization matrix) system function.
/// </summary>
public sealed class AuthorizationMatrixFunctionHandler(
    IAuthorizeAppService authorizeAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.AuthorizationMatrix;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var version = request.Parameters.Version
                      ?? request.QueryParameters.GetValueOrDefault("version", null);

        var result = await authorizeAppService.GetAuthorizationMatrixForInstanceAsync(
            request.Domain,
            request.Workflow,
            request.Instance,
            version,
            cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
