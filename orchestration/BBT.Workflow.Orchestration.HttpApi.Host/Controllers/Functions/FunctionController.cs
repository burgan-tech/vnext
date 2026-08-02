using System.Text.Json;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.Users;
using BBT.Workflow.Functions;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Controller for handling workflow function operations
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class FunctionController(
    IFunctionAppService functionAppService,
    ICurrentUser currentUser,
    IInstanceFunctionHandlerFactory handlerFactory,
    IDomainFunctionHandlerFactory domainHandlerFactory) : AetherControllerBase
{
    [HttpGet("{domain}/functions")]
    public async Task<IActionResult> GetDomainFunctionsAsync(
        [FromRoute] string domain,
        CancellationToken cancellationToken = default)
    {
        var response = await functionAppService.GetFunctionsAsync(domain, cancellationToken);
        return FromResult(response);
    }

    [HttpGet("{domain}/functions/{function}")]
    public async Task<IActionResult> GetFunctionByKeyAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var request = new DomainFunctionRequest(
            domain,
            function,
            version,
            requestContext.Headers,
            requestContext.QueryParameters,
            HttpContext);

        var handler = domainHandlerFactory.Get(function.ToLowerInvariant());
        return await handler.HandleAsync(request, cancellationToken);
    }

    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}")]
    public async Task<IActionResult> GetFunctionWithInstanceAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] FunctionQueryParameters parameters,
        [FromHeader(Name = "If-None-Match")] string? ifNoneMatch,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();
        var functionType = function.ToLowerInvariant();

        var handler = handlerFactory.Get(functionType);
        if (handler != null)
        {
            var request = new InstanceFunctionRequest(
                domain,
                workflow,
                instance,
                parameters,
                ifNoneMatch,
                requestContext.Headers,
                requestContext.QueryParameters,
                currentUser,
                HttpContext);

            return await handler.HandleAsync(request, cancellationToken);
        }

        var result = await functionAppService.GetFunctionByInstanceAsync(
            function, workflow, domain, instance,
            requestContext.Headers, requestContext.QueryParameters, null, HttpContext.Request.Method, cancellationToken);

        return FunctionResponseActionResultMapper.ToActionResult(result, HttpContext);
    }

    [HttpPost("{domain}/functions/{function}")]
    [HttpPatch("{domain}/functions/{function}")]
    [HttpDelete("{domain}/functions/{function}")]
    public async Task<IActionResult> InvokeDomainFunctionAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromBody] JsonElement? body = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var request = new DomainFunctionRequest(
            domain,
            function,
            version,
            requestContext.Headers,
            requestContext.QueryParameters,
            HttpContext,
            body);

        var handler = domainHandlerFactory.Get(function.ToLowerInvariant());
        return await handler.HandleAsync(request, cancellationToken);
    }

    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}")]
    [HttpPatch("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}")]
    [HttpDelete("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}")]
    public async Task<IActionResult> InvokeInstanceFunctionAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] FunctionQueryParameters parameters,
        [FromBody] JsonElement? body = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();
        var functionType = function.ToLowerInvariant();

        var handler = handlerFactory.Get(functionType);
        if (handler != null)
        {
            var request = new InstanceFunctionRequest(
                domain,
                workflow,
                instance,
                parameters,
                null,
                requestContext.Headers,
                requestContext.QueryParameters,
                currentUser,
                HttpContext,
                body);

            return await handler.HandleAsync(request, cancellationToken);
        }

        var result = await functionAppService.GetFunctionByInstanceAsync(
            function, workflow, domain, instance,
            requestContext.Headers, requestContext.QueryParameters, body, HttpContext.Request.Method, cancellationToken);

        return FunctionResponseActionResultMapper.ToActionResult(result, HttpContext);
    }
}
