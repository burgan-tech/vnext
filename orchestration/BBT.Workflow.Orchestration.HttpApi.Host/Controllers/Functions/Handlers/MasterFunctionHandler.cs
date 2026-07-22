using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.CurrentUser;
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
            Version = request.Parameters?.Version,
            IfNoneMatch = request.IfNoneMatch,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Roles = request.CurrentUser.ResolveCallerRoles(request.Headers),
        };

        var result = await queryAppService.GetMasterAsync(input, cancellationToken);

        if (result.Result is { IsSuccess: true, Value: { } value } && !string.IsNullOrEmpty(value.ETag))
            request.HttpContext.Response.Headers[HeadersConstants.ETag] = value.ETag;

        if (result.IsNotModified)
            return new StatusCodeResult(304);

        return result.Result.ToActionResult(request.HttpContext);
    }
}
