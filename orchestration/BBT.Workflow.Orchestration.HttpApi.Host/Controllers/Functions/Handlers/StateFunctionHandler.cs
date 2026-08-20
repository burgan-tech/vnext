using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>state</c> (longpooling) system function.
/// Supports conditional GET (304 Not Modified) and sets ETag response headers.
/// </summary>
public sealed class StateFunctionHandler(
    IInstanceQueryAppService queryAppService,
    ICallerRoleResolver callerRoleResolver) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Longpooling;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        // The caller's role set comes from the configured provider, not from ICurrentUser directly:
        // a provider failure denies the read rather than serving it as if the caller had no roles.
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(request.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result.Fail(callerRoles.Error).ToActionResult(request.HttpContext);

        var input = new GetInstanceStateInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            IfNoneMatch = request.IfNoneMatch,
            Version = request.Parameters.Version,
            Extensions = request.Parameters.Extensions,
            Headers = request.Headers,
            QueryParams = request.QueryParameters,
            Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
            Roles = callerRoles.Value
        };

        var result = await queryAppService.GetInstanceStateAsync(input, cancellationToken);

        if (result.IsNotModified)
            return new StatusCodeResult(304);

        if (result.Result.IsSuccess && result.Result.Value is { } value)
        {
            if (!string.IsNullOrEmpty(value.ETag))
                request.HttpContext.Response.Headers[HeadersConstants.ETag] = value.ETag;
            if (!string.IsNullOrEmpty(value.EntityEtag))
                request.HttpContext.Response.Headers[HeadersConstants.XEntityETag] = value.EntityEtag;
        }

        return result.Result.ToActionResult(request.HttpContext);
    }
}
