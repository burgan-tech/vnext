using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Workers.Inbox.Forwarding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Worker Inbox.
/// </summary>
/// <remarks>
/// The Inbox worker is a THIN FORWARDER: it receives distributed events, applies the local
/// domain-match guard, and forwards each to an Orchestration internal endpoint via Dapr service
/// invocation. It deliberately registers only what that requires — domain runtime info, the
/// ASP.NET/Aether core (unit of work + multi-schema + controllers), event subscription, the inbox
/// dedup store, and the messaging DbContext. It does NOT pull the Application or Infrastructure
/// modules, background jobs, the outbox, distributed cache/lock/Redis, the object mapper, or the
/// WorkflowDbContext — those are orchestration concerns and must not run in the Inbox process.
/// </remarks>
public static class InboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Worker Inbox specific services.
    /// </summary>
    public static IServiceCollection AddWorkerInboxModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        services
            .AddDomainModule()                       // IRuntimeInfoProvider + domain primitives (no infra deps)
            .AddAspNetCoreModules(configuration)     // AddAetherCore (UoW + multi-schema), AspNetCore, controllers
            .AddDaprClients()
            .AddAetherEventBus(options =>
            {
                options.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                options.PrefixEnvironmentToTopic = true;
                options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            })
            .AddInboxMessagingDbContext(configuration)
            .AddTelemetry(configuration)
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddAppHealthChecks();

        // Inbox = thin forwarder: deliver events to Orchestration via Dapr service invocation.
        // Singleton — depends only on configuration/logger and owns one Dapr-invokable HttpClient.
        services.AddSingleton<IOrchestrationForwarder, DaprOrchestrationForwarder>();

        return services;
    }

    /// <summary>
    /// Registers only the messaging DbContext (sys_queues: inbox tables) plus the schema
    /// resolution + unit-of-work middleware the event-processing controller relies on. Also
    /// registers the inbox processor and its background service via AddAetherInbox.
    /// </summary>
    private static IServiceCollection AddInboxMessagingDbContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var schemaSwitchingMode = configuration.GetValue("Aether:SchemaSwitchingMode",
            SchemaSwitchingMode.SessionSearchPath);

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
            schemaSwitchingMode,
            (_, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"),
                    npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues");
                    })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        // withHostedService: true → SDK registers InboxBackgroundService with adaptive polling.
        // AetherInboxOptions is a separate class from AetherOutboxOptions (no longer share config).
        // Schema MUST be set — InboxProcessor skips all runs with a warning if Schema is null.
        services.AddAetherInbox<MessagingDbContext>(
            options => configuration.GetSection("Aether:Inbox").Bind(options),
            withHostedService: true);

        return services;
    }
}
