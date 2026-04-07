using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Caching module: Dapr distributed cache and distributed lock.
/// </summary>
public static class WorkflowCachingExtensions
{
    /// <summary>
    /// Registers Dapr-backed distributed cache.
    /// Required by hosts that use component cache, session, or distributed state.
    /// </summary>
    public static IServiceCollection AddWorkflowDistributedCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDaprDistributedCache(configuration["DAPR_STATE_STORE_NAME"]!);
        return services;
    }

    /// <summary>
    /// Registers Dapr-backed distributed lock.
    /// Required by hosts that perform concurrent write operations (Orchestration, Inbox, Outbox, DbMigrator).
    /// </summary>
    public static IServiceCollection AddWorkflowDistributedLock(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDaprDistributedLock(configuration["DAPR_LOCK_STORE_NAME"]!);
        return services;
    }
}
