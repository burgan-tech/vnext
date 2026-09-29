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
    IFunctionInfoAppService functionInfoAppService,
    IFunctionMetricsAppService functionMetricsAppService,
    ICurrentUser currentUser,
    IInstanceFunctionHandlerFactory handlerFactory,
    IDomainFunctionHandlerFactory domainHandlerFactory) : AetherControllerBase
{
    /// <summary>
    /// Paged execution metrics for a domain-registered function (vnext-client-sdk-core#60, item D):
    /// its recent runs (newest first) plus a window summary (count / p50 / p95 / failure-rate). A
    /// function is not record-based, so this is a paged execution series, not an attempts model.
    /// </summary>
    [HttpGet("{domain}/functions/{function}/metrics")]
    public async Task<IActionResult> GetDomainFunctionMetricsAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool? succeeded = null,
        CancellationToken cancellationToken = default)
    {
        var input = new GetFunctionMetricsInput
        {
            Domain = domain,
            Workflow = null,
            FunctionKey = function,
            Page = page,
            PageSize = pageSize,
            From = from,
            To = to,
            Succeeded = succeeded
        };

        var response = await functionMetricsAppService.GetFunctionMetricsAsync(input, cancellationToken);
        return FromResult(response);
    }

    /// <summary>
    /// The flow-scoped sibling of <see cref="GetDomainFunctionMetricsAsync"/> — the same shape,
    /// narrowed to executions that ran under the given workflow.
    /// </summary>
    [HttpGet("{domain}/workflows/{workflow}/functions/{function}/metrics")]
    public async Task<IActionResult> GetFlowFunctionMetricsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string function,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool? succeeded = null,
        CancellationToken cancellationToken = default)
    {
        var input = new GetFunctionMetricsInput
        {
            Domain = domain,
            Workflow = workflow,
            FunctionKey = function,
            Page = page,
            PageSize = pageSize,
            From = from,
            To = to,
            Succeeded = succeeded
        };

        var response = await functionMetricsAppService.GetFunctionMetricsAsync(input, cancellationToken);
        return FromResult(response);
    }

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

    /// <summary>
    /// Describes a domain-scoped function: whether the caller may run it, with which verbs, and the
    /// view and schema contracts that apply - as hyperlinks, in the state function's style.
    /// Only custom (sys-functions) functions are describable; built-in system functions return 404.
    /// </summary>
    [HttpGet("{domain}/functions/{function}/info")]
    public async Task<IActionResult> GetDomainFunctionInfoAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var result = await functionInfoAppService.GetInfoByKeyAsync(
            domain, function, version,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken);

        return FromResult(result);
    }

    /// <summary>
    /// Returns the view a domain-scoped function's <c>inputView</c> or <c>outputView</c> resolves to
    /// for this request. Rules are re-evaluated on every call.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("{domain}/functions/{function}/view")]
    public async Task<IActionResult> GetDomainFunctionViewAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromQuery] string? target = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var result = await functionInfoAppService.GetViewByKeyAsync(
            domain, function, target ?? string.Empty, version,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken);

        return FromResult(result);
    }

    /// <summary>
    /// Returns the schema a domain-scoped function's <c>inputSchema</c> or <c>outputSchema</c>
    /// resolves to for this request.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("{domain}/functions/{function}/schema")]
    public async Task<IActionResult> GetDomainFunctionSchemaAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromQuery] string? target = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var result = await functionInfoAppService.GetSchemaByKeyAsync(
            domain, function, target ?? string.Empty, version,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken);

        return FromResult(result);
    }

    /// <summary>
    /// Describes a function in the context of a workflow instance. Serves functions of every scope,
    /// and resolves contract rules against the instance's current data.
    /// </summary>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/info")]
    public async Task<IActionResult> GetInstanceFunctionInfoAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromRoute] string function,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var result = await functionInfoAppService.GetInfoByInstanceAsync(
            domain, workflow, instance, function,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken);

        return FromResult(result);
    }

    /// <summary>
    /// Returns the view an instance-bound function's <c>inputView</c> or <c>outputView</c> resolves to
    /// for this request.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/view")]
    public async Task<IActionResult> GetInstanceFunctionViewAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromRoute] string function,
        [FromQuery] string? target = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var result = await functionInfoAppService.GetViewByInstanceAsync(
            domain, workflow, instance, function, target ?? string.Empty,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken);

        return FromResult(result);
    }

    /// <summary>
    /// Returns the schema an instance-bound function's <c>inputSchema</c> or <c>outputSchema</c>
    /// resolves to for this request.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/schema")]
    public async Task<IActionResult> GetInstanceFunctionSchemaAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromRoute] string function,
        [FromQuery] string? target = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var result = await functionInfoAppService.GetSchemaByInstanceAsync(
            domain, workflow, instance, function, target ?? string.Empty,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken);

        return FromResult(result);
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
