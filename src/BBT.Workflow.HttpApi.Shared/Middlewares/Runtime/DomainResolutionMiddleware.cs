using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Opens the request's domain scope (<see cref="IRuntimeInfoProvider.UseDomain"/>) so that code
/// reading <see cref="IRuntimeInfoProvider.Domain"/> sees the domain being served when one process
/// hosts several domains.
/// </summary>
/// <remarks>
/// Registered after routing. The domain comes from the <c>{domain}</c> route value, else from the
/// domain segment of the <c>X-Workflow</c> header. A domain that is not hosted opens no scope: the
/// request continues and the application service's own <c>Check</c> rejects it with
/// <c>NotFoundDomainException</c>, exactly as in single-domain hosting.
/// </remarks>
/// <param name="runtimeInfoProvider">Provider holding the hosted-domain set.</param>
public sealed class DomainResolutionMiddleware(IRuntimeInfoProvider runtimeInfoProvider) : IMiddleware
{
    /// <summary>The route value carrying the domain on every business route.</summary>
    public const string RouteValueKey = "domain";

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var domain = context.GetRouteValue(RouteValueKey) as string;
        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = context.GetWorkflowInfo()?.Domain;
        }

        if (!runtimeInfoProvider.IsDomainMatch(domain))
        {
            await next(context);
            return;
        }

        using (runtimeInfoProvider.UseDomain(domain!))
        {
            await next(context);
        }
    }
}
