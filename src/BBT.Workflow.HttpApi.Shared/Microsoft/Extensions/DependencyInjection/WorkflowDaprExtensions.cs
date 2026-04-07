using Dapr.Jobs.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dapr client module: DaprClient and DaprJobsClient registration.
/// </summary>
public static class WorkflowDaprExtensions
{
    /// <summary>
    /// Registers Dapr client and Dapr Jobs client services.
    /// Required by hosts that communicate with Dapr sidecars (Orchestration, Inbox, Outbox, DbMigrator).
    /// </summary>
    public static IServiceCollection AddWorkflowDapr(this IServiceCollection services)
    {
        services.AddDaprClient();
        services.AddDaprJobsClient();
        return services;
    }
}
