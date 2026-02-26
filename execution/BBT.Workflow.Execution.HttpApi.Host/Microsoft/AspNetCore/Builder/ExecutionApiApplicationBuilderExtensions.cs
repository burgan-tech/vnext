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
        app.UseHttpsRedirection();
        app.UseCorrelationId();
        app.UseParentInstanceIdEnrichment();
        app.UseSecurityHeaders();
        app.UseCurrentUser();
        app.UseStaticFiles();
        app.UseAetherApiVersioning();
        app.UseRouting();
        app.UseHttpMetrics();
        app.MapMetrics(); 
        app.MapControllers();
        app.MapAppHealthChecks();

        return app;
    }
} 