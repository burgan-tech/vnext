using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.Data;
using Dapr.Jobs.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Background job module: Aether background job handlers and Dapr job scheduler.
/// </summary>
public static class WorkflowBackgroundJobExtensions
{
    /// <summary>
    /// Registers Aether background job infrastructure with workflow-specific job handlers
    /// (FlowTimeout, Transition, TransitionTimer) and the Dapr job scheduler.
    /// Used by Orchestration, Inbox, and standard worker configurations.
    /// </summary>
    public static IServiceCollection AddWorkflowBackgroundJobs(this IServiceCollection services)
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
}
