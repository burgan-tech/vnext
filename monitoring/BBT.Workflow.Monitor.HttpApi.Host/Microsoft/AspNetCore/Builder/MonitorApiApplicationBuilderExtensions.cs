
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Application builder extensions specific to the Monitor API host.
/// </summary>
public static class MonitorApiApplicationBuilderExtensions
{
    /// <summary>
    /// Configures the Monitor API middleware pipeline.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application for chaining.</returns>
    public static WebApplication UseMonitorApiModule(this WebApplication app)
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
        app.UseHttpBodyLogging();
        app.MapControllers();
        app.MapAppHealthChecks();

        return app;
    }
}
