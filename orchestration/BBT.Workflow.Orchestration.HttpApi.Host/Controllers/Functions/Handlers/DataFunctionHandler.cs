using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>data</c> system function.
/// Supports conditional GET (304 Not Modified) and sets ETag response headers.
/// </summary>
public sealed class DataFunctionHandler(
    IInstanceQueryAppService queryAppService,
    ICallerRoleResolver callerRoleResolver) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Data;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        // The caller's role set comes from the configured provider, not from ICurrentUser directly:
        // a provider failure denies the read rather than serving it as if the caller had no roles.
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(request.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result.Fail(callerRoles.Error).ToActionResult(request.HttpContext);

        var input = new GetInstanceDataInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            IfNoneMatch = request.IfNoneMatch,
            Extensions = request.Parameters.Extensions,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Version = request.QueryParameters.GetOrDefault("version"),
            Roles = callerRoles.Value,
        };

        var result = await queryAppService.GetInstanceDataAsync(input, cancellationToken);

        if (result.Result is { IsSuccess: true, Value: { } value })
        {
            if (!string.IsNullOrEmpty(value.ETag))
                request.HttpContext.Response.Headers[HeadersConstants.ETag] = value.ETag;
            if (!string.IsNullOrEmpty(value.EntityEtag))
                request.HttpContext.Response.Headers[HeadersConstants.XEntityETag] = value.EntityEtag;
        }

        if (result.IsNotModified)
            return new StatusCodeResult(304);

        return result.Result.ToActionResult(request.HttpContext);
    }
}
