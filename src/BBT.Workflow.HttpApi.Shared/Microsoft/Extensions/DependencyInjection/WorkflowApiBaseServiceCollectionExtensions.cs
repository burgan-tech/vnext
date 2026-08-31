using System.Net;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.Tracing;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.Data;
using BBT.Workflow.Authorization;
using BBT.Workflow.Headers;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Dapr.Jobs.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Base service collection extensions shared across all Workflow APIs
/// </summary>
public static class WorkflowApiBaseServiceCollectionExtensions
{
    /// <summary>
    /// Configuration section bound onto the Aether background-job options
    /// (Schema, MaxRetryCount, RetryBaseDelay, ArmingInterval, ArmingBatchSize, VisibilityTimeout).
    /// </summary>
    private const string BackgroundJobConfigurationSection = "BackgroundJob";

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

    public static IServiceCollection AddAspNetCoreModules(this IServiceCollection services,
        IConfiguration configuration)
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

        // Raw request body capture for signature verification (JWS/mTLS): expose the original payload to
        // mappings via ScriptContext.RawBody. Replaces the ambient-only default registered in the Application layer.
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Singleton<BBT.Workflow.Scripting.IRequestRawBodyProvider,
            BBT.Workflow.Middlewares.HttpContextRawBodyProvider>());
        services.Configure<BBT.Workflow.Middlewares.RawRequestBodyBufferingOptions>(
            configuration.GetSection(BBT.Workflow.Middlewares.RawRequestBodyBufferingOptions.SectionPath));

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
                options.JsonSerializerOptions.ReferenceHandler = centralOptions.ReferenceHandler;
                options.JsonSerializerOptions.MaxDepth = centralOptions.MaxDepth;

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

        services.AddAetherNpgsql<WorkflowDbContext>(
            configuration.GetConnectionString("Default")!,
            SchemaSwitchingMode.QualifiedNames,
            (sp, options) =>
            {
                options.UseNpgsql(configuration.GetConnectionString("Default"),
                        npgsqlOptions => { npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations"); })
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

                options.ReplaceService<IMigrationsSqlGenerator, MultiSchemaNpgsqlMigrationsSqlGenerator>();

                // SchemaAwareModelCacheKeyFactory replaces SET search_path approach:
                // a separate compiled model is cached per schema, table names are fully qualified,
                // no session-level directive is ever sent — PgBouncer transaction-mode safe.
                options.ReplaceService<IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>();
            });

        services.AddAetherUnitOfWorkMiddleware();

        services.AddSingleton<IDataSeedService, WorkflowDataSeedService>();

        services.AddAetherNpgsql<MessagingDbContext>(
            configuration.GetConnectionString("Default")!,
            SchemaSwitchingMode.QualifiedNames,
            (_, options) =>
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

    /// <summary>
    /// Registers domain event dispatching, transactional outbox, and inbox infrastructure.
    /// Requires <see cref="BBT.Aether.Events.IDistributedEventBus"/> and <see cref="BBT.Aether.Events.IEventSerializer"/>
    /// to be registered (via <c>AddEventBus</c> or <c>AddAetherEventBus</c>).
    /// Do NOT call from DbMigrator or other minimal hosts.
    /// </summary>
    public static IServiceCollection AddDomainEventsInfrastructure(this IServiceCollection services)
    {
        services.AddAetherDomainEvents<MessagingDbContext>(options =>
        {
            options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
        });

        // Bind the full AetherOutboxOptions from the "Aether:Outbox" config section (ProcessingInterval,
        // BatchSize, LeaseDuration, RetentionPeriod, MaxRetryCount, RetryBaseDelay). Absent keys keep
        // Aether's defaults. The shared singleton drives both the outbox and inbox processors, so the
        // section configures poll latency, batch size, and lease behavior from appsettings.
        var configuration = services.GetConfiguration();

        services.AddAetherOutbox<MessagingDbContext>(options =>
            configuration.GetSection("Aether:Outbox").Bind(options));
        services.AddAetherInbox<MessagingDbContext>();

        return services;
    }

    public static IServiceCollection AddBackgroundJob(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();

        // Whether to run the arming/reaper hosted service in this process. Defaults to true
        // (configurable via BackgroundJob:WithHostedService) so the background-job processor runs
        // unless explicitly disabled (e.g. for read-only / scale-out roles).
        var withHostedService = configuration.GetValue(
            $"{BackgroundJobConfigurationSection}:WithHostedService", true);

        services.AddAetherBackgroundJob<MessagingDbContext>(options =>
        {
            options.AddHandler<FlowTimeoutJobHandler>(FlowTimeoutJobHandler.HandlerName);
            options.AddHandler<TransitionJobHandler>(TransitionJobHandler.HandlerName);
            options.AddHandler<TransitionTimerJobHandler>(TransitionTimerJobHandler.HandlerName);
            options.AddHandler<LongPollAckTimeoutJobHandler>(LongPollAckTimeoutJobHandler.HandlerName);
            options.AddHandler<StateNotifyJobHandler>(StateNotifyJobHandler.HandlerName);
            
            // Bind the tunables (Schema, MaxRetryCount, RetryBaseDelay, ArmingInterval,
            // ArmingBatchSize, VisibilityTimeout) from configuration. Absent keys keep the
            // BackgroundJobOptions defaults; the registered handlers are not affected.
            configuration.GetSection(BackgroundJobConfigurationSection).Bind(options);
        }, withHostedService: withHostedService);

        services.AddDaprJobScheduler();

        return services;
    }

    public static IServiceCollection AppMapper(this IServiceCollection services)
    {
        services.AddAetherMapperlyMapper(
        [
            typeof(WorkflowApiBaseServiceCollectionExtensions), // HttpApi.Shared
            typeof(WorkflowDomainModuleServiceCollectionExtensions), // Domain
            typeof(WorkflowApplicationModuleServiceCollectionExtensions) // Application
        ]);
        return services;
    }

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        // The RequestIdLogProcessor runs after Aether's header enricher and before the exporter,
        // stamping the originating request id on every log record of every service — including
        // paths with no HttpContext, where the enricher is silent. See the processor's remarks
        // for why the header enricher cannot be the source of this field.
        services.AddAetherTelemetry(configuration, configure: builder =>
            builder
                .ConfigureLogging((_, logging) =>
                    logging.AddProcessor(serviceProvider =>
                        new RequestIdLogProcessor(serviceProvider.GetRequiredService<ICorrelationIdProvider>())))
                // Span counterpart, so a trace filters on the same x_request_id value as the logs.
                .ConfigureTracing((_, tracing) =>
                {
                    tracing
                        // Aether registers AspNetCore + HttpClient instrumentation, but nothing for
                        // gRPC. Grpc.Net.Client — which every Dapr.Client call goes through — creates
                        // its own activity regardless, and the System.Net.Http span for the HTTP/2
                        // request nests under it. Unexported, that activity leaves a hole: the
                        // HttpClient span names a parent no backend ever saw, and Elastic APM
                        // re-parents such a span to the trace root, detaching it from the pipeline
                        // that issued the call. Registering the instrumentation exports the parent
                        // and closes the hole; this is the same failure mode Business-mode span
                        // filtering causes, documented on PipelineStepActivityHelper.
                        .AddGrpcClientInstrumentation()
                        // The pipeline's own spans say how long a DB region took (Instance.Load,
                        // Instance.AppendData, Uow.Commit) but not which command spent it. One span
                        // per EF Core command closes that: a slow load is then readable as the query
                        // it actually ran, and an N+1 shows up as N sibling spans rather than as one
                        // wide parent.
                        //
                        // The query TEXT is emitted unconditionally in this version (the old
                        // SetDbStatementForText toggle is gone), and the part that would actually
                        // carry business data — parameter VALUES — stays behind
                        // OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS, false
                        // unless set. Text with `@p0` placeholders is what makes the span readable,
                        // so there is nothing to gate here.
                        //
                        // The DisplayName is renamed because the default is the database name: a
                        // transition showed fifteen sibling spans all called `Aether_WorkflowDb`,
                        // which says how many commands ran and nothing about what they were. Naming
                        // the verb makes a write stand out among reads at a glance, and follows the
                        // same rule as the rest of this branch's spans — subject in the name, detail
                        // in the tags, where the full statement already sits.
                        .AddEntityFrameworkCoreInstrumentation(options =>
                            options.EnrichWithIDbCommand = static (activity, command) =>
                                activity.DisplayName = $"Db.{DescribeSqlVerb(command.CommandText)}")
                        .AddProcessor(serviceProvider =>
                            new RequestIdSpanProcessor(serviceProvider.GetRequiredService<ICorrelationIdProvider>()));

                    // Worker hosts only: see IdlePollSpanProcessor. Other hosts have no idle poll
                    // loop, so the processor would only add a per-span branch for nothing.
                    if (configuration.GetValue("Telemetry:Tracing:DropRootDbSpans", false))
                    {
                        tracing.AddProcessor(new IdlePollSpanProcessor());
                    }
                }));
        return services;
    }

    /// <summary>
    /// Reduces a SQL command to the verb that names it, for use as an EF Core span's DisplayName.
    /// </summary>
    /// <remarks>
    /// Deliberately the first token and nothing more: anything that tried to name the table would
    /// have to parse SQL, and a span name is not worth a parser. Only the verbs EF Core actually
    /// issues are recognized; anything else — a transaction statement, a provider probe, a leading
    /// comment or hint — reports <c>Query</c> rather than being guessed at, because the full
    /// statement is already on the span as a tag for the cases where the verb is not enough.
    /// </remarks>
    /// <param name="commandText">The command text EF Core is about to execute; may be null or empty.</param>
    /// <returns>An uppercase SQL verb, or <c>Query</c> when it cannot be determined.</returns>
    private static string DescribeSqlVerb(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return "Query";

        var text = commandText.AsSpan().TrimStart();

        var end = text.IndexOfAny(' ', '\r', '\n');
        var verb = end < 0 ? text : text[..end];

        return verb switch
        {
            _ when verb.Equals("SELECT", StringComparison.OrdinalIgnoreCase) => "SELECT",
            _ when verb.Equals("INSERT", StringComparison.OrdinalIgnoreCase) => "INSERT",
            _ when verb.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) => "UPDATE",
            _ when verb.Equals("DELETE", StringComparison.OrdinalIgnoreCase) => "DELETE",
            _ when verb.Equals("MERGE", StringComparison.OrdinalIgnoreCase) => "MERGE",
            _ => "Query"
        };
    }

    public static IServiceCollection AddDistributedCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDaprDistributedCache(configuration["DAPR_STATE_STORE_NAME"]!);
        return services;
    }

    public static IServiceCollection AddDistributedLock(this IServiceCollection services, IConfiguration configuration)
    {
        var lockStoreName = configuration["DAPR_LOCK_STORE_NAME"]!;

        services.AddDaprDistributedLock(lockStoreName);
        services.AddSingleton<
            BBT.Workflow.Infrastructure.Execution.Locks.IPostgreSqlDistributedLockService,
            BBT.Workflow.Infrastructure.Execution.Locks.NpgsqlDistributedLockService>();

        services.AddResourceLock(lockStoreName);
        return services;
    }

    /// <summary>
    /// Registers request-scoped transition lock scope and busy marker services.
    /// </summary>
    public static IServiceCollection AddTransitionLockScope(this IServiceCollection services)
    {
        services.AddScoped<BBT.Workflow.Execution.Pipeline.ITransitionLockScopeFactory,
            BBT.Workflow.Infrastructure.Execution.Locks.TransitionLockScopeFactory>();
        services.AddScoped<BBT.Workflow.Execution.Pipeline.IInstanceStatusLock,
            BBT.Workflow.Infrastructure.Execution.Locks.InstanceStatusLock>();
        return services;
    }

    public static IServiceCollection AddResourceLock(this IServiceCollection services, string lockStoreName)
    {
        services.AddScoped<BBT.Workflow.Execution.IResourceLockService>(sp =>
            new BBT.Workflow.Infrastructure.Execution.ResourceLock.DaprResourceLockService(
                sp.GetRequiredService<Dapr.Client.DaprClient>(),
                lockStoreName,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                    BBT.Workflow.Infrastructure.Execution.ResourceLock.DaprResourceLockService>>()));
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
            // Subflow terminal-outcome lock contention is expected & retryable, not an internal
            // error. 503 is transient (TransientHttpStatus) so the inbox relay redelivers.
            opt.Map(WorkflowErrorCodes.SubflowTerminalLockNotAcquired, HttpStatusCode.ServiceUnavailable);
            opt.Map(WorkflowErrorCodes.InstanceBusy, HttpStatusCode.Conflict);
            // InstanceData write funnel: FOR UPDATE lock wait exceeded → caller-retryable
            // conflict; statement cancelled by statement_timeout → transient (retried by relays).
            opt.Map(WorkflowErrorCodes.InstanceDataLockTimeout, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.InstanceDataWriteTimeout, HttpStatusCode.ServiceUnavailable);
            opt.Map(WorkflowErrorCodes.RuntimeSchemaInvalidState, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.TransitionLocked, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.AutoTransitionConditionNotMet, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.UnauthorizedTransition, HttpStatusCode.Forbidden);
            opt.Map(WorkflowErrorCodes.AuthorizationRoleDenied, HttpStatusCode.Forbidden);
            opt.Map(WorkflowErrorCodes.AuthorizeRequiresExactlyOneTarget, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.AuthorizeQueryRolesRequiresInstance, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.InvalidState, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.NotFoundTransition, HttpStatusCode.NotFound);
            opt.Map(WorkflowErrorCodes.NotFoundInitialState, HttpStatusCode.NotFound);
            opt.Map(WorkflowErrorCodes.NotFoundWorkflow, HttpStatusCode.NotFound);

            // Execution errors
            opt.Map(WorkflowErrorCodes.ExecutionStepFailed, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.ResourceLockConflict, HttpStatusCode.Conflict);

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
        services.AddScoped<ICallerPositionAccessor, HttpContextCallerPositionAccessor>();
        services
            .AddScoped<BBT.Workflow.Languages.ICurrentLanguage, BBT.Workflow.Languages.HttpContextCurrentLanguage>();
        services
            .ReplaceSchemaResolver<HeaderSchemaResolutionStrategy, WorkflowHeaderSchemaResolutionStrategy>();

        return services;
    }
}
