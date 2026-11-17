using BBT.Workflow.Execution.RuntimeCaller;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.RuntimeCaller;

/// <summary>The runtime-caller token a request arrived with, moved out of the headers at ingress.</summary>
public interface IRuntimeCallerTokenFeature
{
    /// <summary>The raw header value; a repeated header arrives joined and therefore fails to parse.</summary>
    string? Token { get; }
}

/// <summary>
/// Moves <see cref="RuntimeCallerToken.HeaderName"/> out of the request headers, on every route and
/// whatever the gate mode, before anything reads the headers (vnext#1265).
/// <para>
/// It must run before <c>UseHttpBodyLogging</c>, which serialises every request header, and before
/// any endpoint copies the headers into <c>input.Headers</c> — from there they reach the transition
/// record (<c>InstanceTransitions."Header"</c>, served by the history API), job payloads and the
/// headers flow scripts read. Only the gate filter reads the token, from
/// <see cref="IRuntimeCallerTokenFeature"/>.
/// </para>
/// </summary>
public sealed class RuntimeCallerTokenMiddleware(RequestDelegate next)
{
    /// <summary>Strips the header and keeps its value as a feature.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(RuntimeCallerToken.HeaderName, out var values))
        {
            context.Features.Set<IRuntimeCallerTokenFeature>(new RuntimeCallerTokenFeature(values.ToString()));
            context.Request.Headers.Remove(RuntimeCallerToken.HeaderName);
        }

        return next(context);
    }

    private sealed record RuntimeCallerTokenFeature(string? Token) : IRuntimeCallerTokenFeature;
}
