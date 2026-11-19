using BBT.Workflow.RuntimeCaller;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Pipeline registration of the runtime-caller token strip (vnext#1265).</summary>
public static class RuntimeCallerApplicationBuilderExtensions
{
    /// <summary>
    /// Moves the runtime-caller token header out of every request (see
    /// <see cref="RuntimeCallerTokenMiddleware"/>). Register it before anything that reads or logs
    /// request headers — in particular before <c>UseCurrentUser</c> and <c>UseHttpBodyLogging</c>.
    /// </summary>
    public static IApplicationBuilder UseRuntimeCallerTokenStrip(this IApplicationBuilder app)
        => app.UseMiddleware<RuntimeCallerTokenMiddleware>();
}
