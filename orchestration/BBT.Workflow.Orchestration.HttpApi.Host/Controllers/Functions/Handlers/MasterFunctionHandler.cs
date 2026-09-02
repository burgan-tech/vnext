using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>master</c> system function. Returns the flow-level master schema the instance is
/// bound to, forwarding to the active subflow instance when one is present.
/// Supports conditional GET (304 Not Modified) and sets the ETag response header.
/// </summary>
public sealed class MasterFunctionHandler(
    IInstanceQueryAppService queryAppService,
    ICallerRoleResolver callerRoleResolver) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Master;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        // The caller's role set comes from the configured provider, not from ICurrentUser directly:
        // a provider failure denies the read rather than serving it as if the caller had no roles.
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(request.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result.Fail(callerRoles.Error).ToActionResult(request.HttpContext);

        var input = new GetMasterInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Version = request.Parameters?.Version,
            IfNoneMatch = request.IfNoneMatch,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Roles = callerRoles.Value,
        };

        var result = await queryAppService.GetMasterAsync(input, cancellationToken);

        if (result.Result is { IsSuccess: true, Value: { } value } && !string.IsNullOrEmpty(value.ETag))
            request.HttpContext.Response.Headers[HeadersConstants.ETag] = value.ETag;

        if (result.IsNotModified)
            return new StatusCodeResult(304);

        return result.Result.ToActionResult(request.HttpContext);
    }
}
