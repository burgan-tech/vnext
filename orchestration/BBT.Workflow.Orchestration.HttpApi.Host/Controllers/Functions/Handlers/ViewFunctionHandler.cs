using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>view</c> system function.
/// </summary>
public sealed class ViewFunctionHandler(
    IInstanceQueryAppService queryAppService,
    ICallerRoleResolver callerRoleResolver) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.View;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        // The caller's role set comes from the configured provider, not from ICurrentUser directly:
        // a provider failure denies the read rather than serving it as if the caller had no roles.
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(request.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result.Fail(callerRoles.Error).ToActionResult(request.HttpContext);

        var input = new GetViewInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Version = request.Parameters.Version,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
            Roles = callerRoles.Value
        };

        var result = await queryAppService.GetViewAsync(
            input,
            request.Parameters.TransitionKey ?? string.Empty,
            cancellationToken);

        return result.ToActionResult(request.HttpContext);
    }
}
