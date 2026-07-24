using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>schema</c> system function.
/// Supports conditional GET (304 Not Modified) and sets the ETag response header.
/// </summary>
public sealed class SchemaFunctionHandler(
    IInstanceQueryAppService queryAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.Schema;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var input = new GetSchemaInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Version = request.Parameters.Version,
            IfNoneMatch = request.IfNoneMatch,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Roles = request.CurrentUser.ResolveCallerRoles(request.Headers),
        };

        var result = await queryAppService.GetSchemaAsync(
            input,
            request.Parameters.TransitionKey,
            cancellationToken);

        if (result.Result is { IsSuccess: true, Value: { } value } && !string.IsNullOrEmpty(value.ETag))
            request.HttpContext.Response.Headers[HeadersConstants.ETag] = value.ETag;

        if (result.IsNotModified)
            return new StatusCodeResult(304);

        return result.Result.ToActionResult(request.HttpContext);
    }
}
