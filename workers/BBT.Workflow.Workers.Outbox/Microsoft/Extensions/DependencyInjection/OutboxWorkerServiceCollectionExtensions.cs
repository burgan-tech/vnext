using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Worker Outbox
/// </summary>
public static class OutboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Worker Outbox specific services
    /// </summary>
    public static IServiceCollection AddWorkerOutboxModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        services
            .AddDomainModule()
            .AddAspNetCoreModules(configuration)
            .AddDaprClients()
            .AddAetherEventBus(options =>
            {
                options.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                options.PrefixEnvironmentToTopic = true;
                options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            })
            .AddOutboxMessagingContext(configuration)
            .AddTelemetry(configuration)
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddAppHealthChecks();
        return services;
    }

    /// <summary>
    /// Registers only the messaging DbContext (sys_queues outbox tables) and the outbox
    /// processor. The Outbox worker reads OutboxMessages and publishes via the event bus — it does
    /// not need WorkflowDbContext, instance repositories, or the application/infrastructure modules.
    /// </summary>
    private static IServiceCollection AddOutboxMessagingContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSchemaResolution(options =>
        {
            options.HeaderKey = "X-Workflow";
            options.QueryStringKey = "workflow";
            options.RouteValueKey = "workflow";
            options.ThrowIfNotFound = false;
        });

        services.AddAetherUnitOfWorkMiddleware();

        services.AddAetherNpgsql<MessagingDbContext>(
            configuration.GetConnectionString("Default")!,
            SchemaSwitchingMode.QualifiedNames,
            (_, options) =>
            {
                options.UseNpgsql(
                        configuration.GetConnectionString("Default"),
                        npgsqlOptions =>
                        {
                            npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues");
                        })
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            });

        // withHostedService: true → SDK registers OutboxBackgroundService with adaptive polling.
        // No manual AddHostedService call needed.
        services.AddAetherOutbox<MessagingDbContext>(
            options => configuration.GetSection("Aether:Outbox").Bind(options),
            withHostedService: true);

        // Replaces the SDK's no-op publisher; this worker already has a DaprClient.
        services.AddAetherOutboxDaprSignalling();

        return services;
    }
}
