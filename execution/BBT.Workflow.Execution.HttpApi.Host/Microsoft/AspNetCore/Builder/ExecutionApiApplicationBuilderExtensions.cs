using BBT.Workflow.Execution.Invocation;
using Prometheus;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Application builder extensions specific to Execution API
/// </summary>
public static class ExecutionApiApplicationBuilderExtensions
{
    /// <summary>
    /// Configures the Execution API application pipeline
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication UseExecutionApiModule(this WebApplication app)
    {
        app.UseAetherAmbientServiceProvider();
        app.UseAppVersion();
        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseHsts();
        }
        app.UseExceptionHandler();
        app.UseAppResponseCompression();
        app.UseCloudEvents();
        app.MapSubscribeHandler();
        // gRPC surface of the task-invoke endpoint (see TaskInvokerGrpcService). NOT served on
        // the same Kestrel endpoint as the HTTP controller -- Kestrel binds two separate
        // cleartext endpoints, one HTTP/1.1-only and one HTTP/2-only h2c, because without TLS
        // there's no ALPN to multiplex both on one port. See the Kestrel endpoint-configuration
        // comment in Program.cs for the full explanation of why, and Kestrel:GrpcPort for which
        // port this service actually listens on.
        app.MapGrpcService<TaskInvokerGrpcService>();
        app.UseHttpsRedirection();
        app.UseCorrelationId();
        app.UseParentInstanceIdEnrichment();
        app.UseSecurityHeaders();
        app.UseCurrentUser();
        app.UseRawRequestBodyBuffering();
        app.UseStaticFiles();
        app.UseAetherApiVersioning(
            useSwagger: !app.Environment.IsProduction(),
            useSwaggerUi: !app.Environment.IsProduction());
        app.UseRouting();
        app.UseHttpMetrics();
        app.MapMetrics(); 
        app.MapControllers();
        app.MapAppHealthChecks();

        return app;
    }
} 