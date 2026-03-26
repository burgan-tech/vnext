using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>authorize</c> system function.
/// Returns 200 when allowed, 403 when not allowed, or an error response on failure.
/// </summary>
public sealed class AuthorizeFunctionHandler(
    IAuthorizeAppService authorizeAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Authorize;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var qp = request.QueryParameters;
        var role = request.CurrentUser.Roles?.Length > 0
            ? string.Join(",", request.CurrentUser.Roles)
            : (request.Parameters.Role
               ?? qp.GetValueOrDefault("role", null)
               ?? string.Empty);

        var version = request.Parameters.Version
                      ?? qp.GetValueOrDefault("version", null);

        var checkQueryRoles = request.Parameters.QueryRoles == true
            || string.Equals(
                qp.GetValueOrDefault("queryRoles", null),
                "true",
                StringComparison.OrdinalIgnoreCase);

        var result = await authorizeAppService.GetAuthorizeResultForInstanceAsync(
            request.Domain,
            request.Workflow,
            request.Instance,
            role,
            request.Parameters.TransitionKey,
            request.Parameters.FunctionKey,
            version,
            checkQueryRoles,
            cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult(request.HttpContext);

        if (result.Value!.Allowed)
            return result.ToActionResult(request.HttpContext);

        return new ObjectResult(result.Value) { StatusCode = 403 };
    }
}
