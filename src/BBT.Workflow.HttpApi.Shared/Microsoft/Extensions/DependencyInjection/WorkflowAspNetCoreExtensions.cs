using BBT.Workflow;
using BBT.Workflow.DefinitionContext;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ASP.NET Core module: Aether core, JSON serialization, API versioning, controllers.
/// </summary>
public static class WorkflowAspNetCoreExtensions
{
    /// <summary>
    /// Registers Aether core, ASP.NET Core, JSON serialization, API versioning, and controller services.
    /// Required by all HTTP-facing hosts (Orchestration, Execution, Monitor, Workers).
    /// </summary>
    public static IServiceCollection AddWorkflowAspNetCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAetherAmbientServiceProvider();
        services.AddWorkflowJsonSerializer();
        services.AddAetherCore(options =>
        {
            options.Environment ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
            options.ApplicationName ??= configuration.GetValue<string?>("ApplicationName") ?? "vNext";
        });
        services.AddAetherAspNetCore();

        services.AddEndpointsApiExplorer();
        services.AddAetherApiVersioning(apiTitle: "vNext API");
        services.AddScoped<IWorkflowContext, WorkflowContext>();

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                var centralOptions = JsonSerializerConstants.JsonOptions;

                options.JsonSerializerOptions.WriteIndented = centralOptions.WriteIndented;
                options.JsonSerializerOptions.PropertyNamingPolicy = centralOptions.PropertyNamingPolicy;
                options.JsonSerializerOptions.DictionaryKeyPolicy = centralOptions.DictionaryKeyPolicy;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = centralOptions.PropertyNameCaseInsensitive;
                options.JsonSerializerOptions.DefaultIgnoreCondition = centralOptions.DefaultIgnoreCondition;
                options.JsonSerializerOptions.ReferenceHandler = centralOptions.ReferenceHandler;
                options.JsonSerializerOptions.MaxDepth = centralOptions.MaxDepth;

                foreach (var converter in centralOptions.Converters)
                {
                    options.JsonSerializerOptions.Converters.Add(converter);
                }
            });

        return services;
    }

    /// <summary>
    /// Registers the centralized JsonSerializerOptions as a singleton in DI.
    /// </summary>
    public static IServiceCollection AddWorkflowJsonSerializer(this IServiceCollection services)
    {
        services.AddSingleton(JsonSerializerConstants.JsonOptions);
        return services;
    }
}
