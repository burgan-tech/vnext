using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.Domain.Services;
using BBT.Aether.MultiSchema.EntityFrameworkCore.Interceptors;
using BBT.Workflow.Data;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Database module: EF Core DbContexts, schema resolution, and unit-of-work middleware.
/// </summary>
public static class WorkflowDbContextExtensions
{
    /// <summary>
    /// Registers WorkflowDbContext, MessagingDbContext, schema resolution, and unit-of-work middleware.
    /// Required by all hosts that access PostgreSQL (all except Execution).
    /// </summary>
    public static IServiceCollection AddWorkflowDbContext(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSchemaResolution(options =>
        {
            options.HeaderKey = "X-Workflow";
            options.QueryStringKey = "workflow";
            options.RouteValueKey = "workflow";
            options.ThrowIfNotFound = false;
        });

        services.AddAetherDbContext<WorkflowDbContext>((sp, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"),
                    npgsqlOptions => { npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations"); })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

            options.ReplaceService<IMigrationsSqlGenerator, MultiSchemaNpgsqlMigrationsSqlGenerator>();
            options.AddInterceptors(
                sp.GetRequiredService<NpgsqlSchemaConnectionInterceptor>(),
                sp.GetRequiredService<WorkflowDatabaseInterceptor>(),
                sp.GetRequiredService<WorkflowTransactionInterceptor>()
            );
        });

        services.AddAetherUnitOfWorkMiddleware();

        services.AddSingleton<IDataSeedService, WorkflowDataSeedService>();

        services.AddAetherDbContext<MessagingDbContext>((_, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"),
                    npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues");
                    })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        return services;
    }
}
