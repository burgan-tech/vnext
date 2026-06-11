using Prometheus;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Application builder extensions specific to Worker Inbox
/// </summary>
public static class InboxWorkerApplicationBuilderExtensions
{
    /// <summary>
    /// Configures the application pipeline with base worker inbox middleware
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication UseWorkerInbox(this WebApplication app)
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
        // NOTE: UseWorkflowHttpMetrics removed — its HttpMetricsMiddleware needs IWorkflowMetrics,
        // which lived in the (now-removed) Infrastructure module. Generic Prometheus HTTP metrics
        // below are sufficient for the thin forwarder.
        app.UseHttpMetrics();
        app.MapMetrics(); 
        app.MapControllers();
        // NOTE: UseDaprScheduledJobHandler removed — the Inbox registers no background-job handlers
        // and must not dispatch Dapr scheduled jobs (that allowed transitions to run in-process here).
        app.MapAppHealthChecks();
        return app;
    }
}