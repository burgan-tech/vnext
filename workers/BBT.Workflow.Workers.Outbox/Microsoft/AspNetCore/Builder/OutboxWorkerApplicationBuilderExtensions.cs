using Prometheus;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Application builder extensions specific to Worker Outbox
/// </summary>
public static class OutboxWorkerApplicationBuilderExtensions
{
    /// <summary>
    /// Configures the application pipeline with base worker outbox middleware
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication UseWorkerOutbox(this WebApplication app)
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
        app.UseRuntime();
        app.UseCorrelationId();
        app.UseSecurityHeaders();
        app.UseCurrentUser();
        app.UseStaticFiles();
        app.UseAetherApiVersioning();
        app.UseRouting();
        app.UseSchemaResolution();
        app.UseAetherUnitOfWork();
        app.UseWorkflowHttpMetrics();
        app.UseHttpMetrics();
        app.MapMetrics(); 
        app.MapControllers();
        app.UseDaprScheduledJobHandler();
        app.MapAppHealthChecks();
        return app;
    }
}