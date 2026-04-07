using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Workflow.Headers;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Runtime middleware module: workflow runtime middleware and header/schema resolution services.
/// </summary>
public static class WorkflowRuntimeMiddlewareExtensions
{
    /// <summary>
    /// Registers the workflow runtime middleware scoped service.
    /// Used by hosts that serve runtime API endpoints (Orchestration, Monitor, Workers).
    /// </summary>
    public static IServiceCollection AddWorkflowRuntimeMiddleware(this IServiceCollection services)
    {
        services.AddScoped<WorkflowRuntimeMiddleware>();
        return services;
    }

    /// <summary>
    /// Registers response header filter, header service, and custom schema resolution strategy.
    /// Used by hosts that need X-Workflow header-based schema resolution.
    /// </summary>
    public static IServiceCollection AddWorkflowHeaderService(this IServiceCollection services)
    {
        services.AddScoped<ResponseHeaderFilter>();
        services.AddScoped<IHeaderService, HttpContextHeaderService>();
        services
            .ReplaceSchemaResolver<HeaderSchemaResolutionStrategy, WorkflowHeaderSchemaResolutionStrategy>();

        return services;
    }
}
