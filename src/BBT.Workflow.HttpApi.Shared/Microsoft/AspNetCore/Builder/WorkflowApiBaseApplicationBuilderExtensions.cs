using BBT.Aether.Threading;
using BBT.Workflow.Data;
using BBT.Workflow.Middlewares;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    /// Opens the request's domain scope from the <c>{domain}</c> route value (or the <c>X-Workflow</c>
    /// header) so a process hosting several domains serves each request as its own domain.
    /// Must run after <c>UseRouting</c>, before the unit of work and the controllers.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseDomainResolution(this IApplicationBuilder app)
    {
        return app.UseMiddleware<DomainResolutionMiddleware>();
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
    /// Buffers the raw request body so the original payload can be exposed to mappings for signature
    /// verification (JWS/mTLS) via <c>ScriptContext.RawBody</c>. Register before controllers/model binding.
    /// </summary>
    public static IApplicationBuilder UseRawRequestBodyBuffering(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RawRequestBodyBufferingMiddleware>();
    }
    
    public static void MigrateMessagingDbContext(this IServiceProvider services)
    {
        // Once per database: a co-hosted domain with its own database has its own copy.
        ForEachDomainDatabase(services, async () =>
        {
            await using var scope = services.CreateAsyncScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            if (dbContext.Database.IsRelational())
            {
                var migrationOptions = scope.ServiceProvider.GetService<IOptions<SchemaMigrationOptions>>()?.Value
                                       ?? new SchemaMigrationOptions();
                dbContext.Database.SetCommandTimeout(migrationOptions.CommandTimeoutSeconds);
                await dbContext.Database.MigrateAsync();
            }
        });
    }

    /// <summary>
    /// Migrates the domain-wide metrics context (function-execution journal, fixed <c>sys_metrics</c>
    /// schema). Mirrors <see cref="MigrateMessagingDbContext"/>; called once at deploy time from the
    /// DbMigrator, since this context is not part of the per-flow schema migration.
    /// </summary>
    public static void MigrateMetricsDbContext(this IServiceProvider services)
    {
        // Once per database: a co-hosted domain with its own database has its own copy.
        ForEachDomainDatabase(services, async () =>
        {
            await using var scope = services.CreateAsyncScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            if (dbContext.Database.IsRelational())
            {
                var migrationOptions = scope.ServiceProvider.GetService<IOptions<SchemaMigrationOptions>>()?.Value
                                       ?? new SchemaMigrationOptions();
                dbContext.Database.SetCommandTimeout(migrationOptions.CommandTimeoutSeconds);
                await dbContext.Database.MigrateAsync();
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="migrate"/> once per distinct database (multi-domain hosting with dedicated
    /// databases), under one of its domains' scope; with one database it runs once, as before.
    /// </summary>
    private static void ForEachDomainDatabase(IServiceProvider services, Func<Task> migrate)
    {
        AsyncHelper.RunSync(async () =>
        {
            var databases = services.GetService<IDomainDatabaseResolver>()?.DatabaseRepresentatives;
            if (databases is not { Count: > 1 })
            {
                await migrate();
                return;
            }

            foreach (var domain in databases)
            {
                using (DomainScope.Begin(domain))
                {
                    await migrate();
                }
            }
        });
    }
}