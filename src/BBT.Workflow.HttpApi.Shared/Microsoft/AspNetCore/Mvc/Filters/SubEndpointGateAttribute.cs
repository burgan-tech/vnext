using BBT.Workflow.Authorization.SubEndpoints;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.HttpApi.Results;
using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Mvc.Filters;

/// <summary>
/// Puts a runtime-internal endpoint behind the sub-endpoint gate (vnext#1265). The gate runs as an
/// authorization filter, so a refusal happens before the request body is bound and before anything
/// is read or written. The route must carry <c>{domain}</c> and <c>{workflow}</c>, and
/// <c>{instance}</c> for <see cref="RuntimeCallOperations.Complete"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SubEndpointGateAttribute : TypeFilterAttribute
{
    /// <summary>Gates the endpoint as <paramref name="operation"/> (one of <see cref="RuntimeCallOperations"/>).</summary>
    public SubEndpointGateAttribute(string operation) : base(typeof(SubEndpointGateFilter))
    {
        Arguments = [operation];
    }
}

/// <summary>
/// Builds the call target from the route, takes the token the ingress strip moved into
/// <see cref="IRuntimeCallerTokenFeature"/>, and asks <see cref="ISubEndpointGate"/>.
/// </summary>
internal sealed class SubEndpointGateFilter(
    string operation,
    IOptions<SubEndpointGateOptions> options,
    ISubEndpointGate gate) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (options.Value.ModeFor(operation) == SubEndpointGateMode.Off)
            return;

        var httpContext = context.HttpContext;
        var route = context.RouteData.Values;
        var target = new RuntimeCallTarget(
            operation,
            route["domain"]?.ToString() ?? string.Empty,
            route["workflow"]?.ToString() ?? string.Empty,
            operation == RuntimeCallOperations.Complete ? route["instance"]?.ToString() : null);
        var headers = httpContext.Request.Headers.ToDictionary(
            header => header.Key.ToLowerInvariant(),
            header => header.Value.FirstOrDefault());

        var result = await gate.AuthorizeAsync(
            target,
            httpContext.Features.Get<IRuntimeCallerTokenFeature>()?.Token,
            headers,
            httpContext.RequestAborted);
        if (!result.IsSuccess)
            context.Result = WorkflowResultActionResultMapper.ToActionResult(result, httpContext);
    }
}
