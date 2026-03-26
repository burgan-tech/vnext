using BBT.Aether.AspNetCore.Results;
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
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Longpooling;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
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
            Role = request.CurrentUser.Roles?.FirstOrDefault()
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
