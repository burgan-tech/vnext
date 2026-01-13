using Prometheus;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Application builder extensions specific to Orchestration API
/// </summary>
public static class OrchestrationApiApplicationBuilderExtensions
{
    /// <summary>
    /// Configures the Orchestration API application pipeline
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication UseOrchestrationApiModule(this WebApplication app)
    {
        app.UseAetherAmbientServiceProvider();
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
        
        app.EnsureDatabaseCreatedInDevelopment();
        app.Services.MigrateMessagingDbContext();
        return app;
    }
}
 