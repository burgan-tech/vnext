using BBT.Aether.Threading;
using BBT.Workflow.Data;
using BBT.Workflow.Middlewares;
using BBT.Workflow.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Base application builder extensions shared across all Workflow APIs
/// </summary>
public static class WorkflowApiBaseApplicationBuilderExtensions
{
    /// <summary>
    /// Adds Workflow runtime middleware to the pipeline
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseRuntime(this IApplicationBuilder app)
    {
        return app.UseMiddleware<WorkflowRuntimeMiddleware>();
    }
    
    /// <summary>
    /// Adds app version middleware that writes X-App-Version header to every response.
    /// Should be registered early in the pipeline to cover all responses including errors.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseAppVersion(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AppVersionMiddleware>();
    }

    /// <summary>
    /// Adds middleware that reads X-Parent-Instance-Id from the request and enriches Activity (tag/baggage) and log scope
    /// so that traces and logs for subflow/subprocess requests are searchable by parent instance ID.
    /// Should be registered after UseCorrelationId() and before controllers.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseParentInstanceIdEnrichment(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ParentInstanceIdEnrichmentMiddleware>();
    }

    /// <summary>
    /// Adds HTTP metrics middleware to the pipeline
    /// </summary>
    public static IApplicationBuilder UseWorkflowHttpMetrics(this IApplicationBuilder app)
    {
        return app.UseMiddleware<HttpMetricsMiddleware>();
    }
    
    public static void MigrateMessagingDbContext(this IServiceProvider services)
    {
        AsyncHelper.RunSync(async () =>
        {
            await using var scope = services.CreateAsyncScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            if (dbContext.Database.IsRelational())
            {
                await dbContext.Database.MigrateAsync();
            }
        });
    }
}