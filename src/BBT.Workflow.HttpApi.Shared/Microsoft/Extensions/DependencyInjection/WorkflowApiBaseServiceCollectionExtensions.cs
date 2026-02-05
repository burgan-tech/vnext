using System.Net;
using System.Text.Json.Serialization;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema.EntityFrameworkCore.Interceptors;
using BBT.Workflow;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.Data;
using BBT.Workflow.Headers;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Dapr.Jobs.Extensions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Base service collection extensions shared across all Workflow APIs
/// </summary>
public static class WorkflowApiBaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the centralized JsonSerializerOptions as a singleton in DI.
    /// This allows services to inject JsonSerializerOptions for consistent JSON handling.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddJsonSerializerOptions(this IServiceCollection services)
    {
        services.AddSingleton(JsonSerializerConstants.JsonOptions);
        return services;
    }

    /// <summary>
    /// Adds Dapr client services
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDaprClients(this IServiceCollection services)
    {
        services.AddDaprClient();
        services.AddDaprJobsClient();
        return services;
    }

    public static IServiceCollection AddAspNetCoreModules(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAetherAmbientServiceProvider();
        services.AddJsonSerializerOptions();
        services.AddAetherCore(options =>
        {
            options.Environment ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
            options.ApplicationName ??= configuration.GetValue<string?>("ApplicationName") ?? "vNext";
        });
        services.AddAetherAspNetCore();
        
        services.AddEndpointsApiExplorer();
        services.AddAetherApiVersioning(apiTitle: "vNext API");

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                // Use centralized JSON configuration from JsonSerializerConstants
                var centralOptions = JsonSerializerConstants.JsonOptions;
                
                options.JsonSerializerOptions.WriteIndented = centralOptions.WriteIndented;
                options.JsonSerializerOptions.PropertyNamingPolicy = centralOptions.PropertyNamingPolicy;
                options.JsonSerializerOptions.DictionaryKeyPolicy = centralOptions.DictionaryKeyPolicy;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = centralOptions.PropertyNameCaseInsensitive;
                options.JsonSerializerOptions.DefaultIgnoreCondition = centralOptions.DefaultIgnoreCondition;
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                
                // Add converters from centralized configuration
                foreach (var converter in centralOptions.Converters)
                {
                    options.JsonSerializerOptions.Converters.Add(converter);
                }
            });
        
        return services;
    }

    public static IServiceCollection AddDbContext(this IServiceCollection services, IConfiguration configuration)
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

        #region DomainEvents

        services.AddAetherDbContext<MessagingDbContext>((_, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"),
                    npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues");
                    })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        services.AddAetherDomainEvents<MessagingDbContext>(options =>
        {
            options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
        });

        services.AddAetherOutbox<MessagingDbContext>();
        services.AddAetherInbox<MessagingDbContext>();

        #endregion
        
        return services;
    }

    public static IServiceCollection AddBackgroundJob(this IServiceCollection services)
    {
        services.AddAetherBackgroundJob<WorkflowDbContext>(options =>
        {
            options.AddHandler<FlowTimeoutJobHandler>(FlowTimeoutJobHandler.HandlerName);
            options.AddHandler<TransitionJobHandler>(TransitionJobHandler.HandlerName);
            options.AddHandler<TransitionTimerJobHandler>(TransitionTimerJobHandler.HandlerName);
        });
        
        services.AddDaprJobScheduler();
        return services;
    }
    
    public static IServiceCollection AppMapper(this IServiceCollection services)
    {
        services.AddAetherAutoMapperMapper(
        [
            typeof(WorkflowApiBaseServiceCollectionExtensions), // HttpApi.Shared
            typeof(WorkflowDomainModuleServiceCollectionExtensions), // Domain
            typeof(WorkflowApplicationModuleServiceCollectionExtensions) // Application
        ]);
        return services;
    }

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAetherTelemetry(configuration);
        return services;
    }

    public static IServiceCollection AddDistributedCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDaprDistributedCache(configuration["DAPR_STATE_STORE_NAME"]!);
        return services;
    }

    public static IServiceCollection AddDistributedLock(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDaprDistributedLock(configuration["DAPR_LOCK_STORE_NAME"]!);
        return services;
    }

    public static IServiceCollection AddEventBus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEventBusWithHooks(options =>
            {
                options.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                options.PrefixEnvironmentToTopic = true;
                options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            }
        );
        return services;
    }

    public static IServiceCollection AddExceptionHandling(this IServiceCollection services)
    {
        // Configure Aether's error code to HTTP status code mapping
        // This is the central place for all error code mappings in the application
        // Both exception handling and Result pattern use this configuration
        services.Configure<AetherExceptionHttpStatusCodeOptions>(opt =>
        {
            // General errors
            opt.Map(WorkflowErrorCodes.Locked, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.ValidationErrors, HttpStatusCode.BadRequest);

            // Instance errors
            opt.Map(WorkflowErrorCodes.NotFoundDomain, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.ConflictWorkflow, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.RuntimeSchemaInvalidState, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.TransitionLocked, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.AutoTransitionConditionNotMet, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.UnauthorizedTransition, HttpStatusCode.Forbidden);
            opt.Map(WorkflowErrorCodes.InvalidState, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.NotFoundTransition, HttpStatusCode.NotFound);
            opt.Map(WorkflowErrorCodes.NotFoundInitialState, HttpStatusCode.NotFound);
            opt.Map(WorkflowErrorCodes.NotFoundWorkflow, HttpStatusCode.NotFound);

            // Execution errors
            opt.Map(WorkflowErrorCodes.ExecutionStepFailed, HttpStatusCode.BadRequest);

            // Task errors
            opt.Map(WorkflowErrorCodes.TaskContextCreation, HttpStatusCode.InternalServerError);
            opt.Map(WorkflowErrorCodes.TaskExecution, HttpStatusCode.InternalServerError);
        });
        return services;
    }
    
    public static IServiceCollection AddRuntimeMiddleware(this IServiceCollection services)
    {
        services.AddScoped<WorkflowRuntimeMiddleware>();
       
        return services;
    }

    public static IServiceCollection AddHeaderService(this IServiceCollection services)
    {
        services.AddScoped<ResponseHeaderFilter>();
        services.AddScoped<IHeaderService, HttpContextHeaderService>();
        services
            .ReplaceSchemaResolver<HeaderSchemaResolutionStrategy, WorkflowHeaderSchemaResolutionStrategy>();
        
        return services;
    }
}