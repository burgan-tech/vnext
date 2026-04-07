using BBT.Aether.Events;
using BBT.Workflow.Data;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Event bus module: Aether event bus with hook support, domain event dispatching, outbox/inbox.
/// </summary>
public static class WorkflowEventBusExtensions
{
    /// <summary>
    /// Registers the Aether event bus with hook decorator using workflow-standard configuration.
    /// Hosts that also need domain event dispatching should call <see cref="AddWorkflowDomainEvents"/>.
    /// </summary>
    public static IServiceCollection AddWorkflowEventBus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEventBusWithHooks(options =>
        {
            options.DefaultSource =
                $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
            options.PrefixEnvironmentToTopic = true;
            options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
        });
        return services;
    }

    /// <summary>
    /// Registers domain event dispatching, transactional outbox, and inbox infrastructure.
    /// Requires <see cref="IDistributedEventBus"/> to be registered (via <see cref="AddWorkflowEventBus"/>
    /// or <c>AddAetherEventBus</c>).
    /// Do NOT call from DbMigrator or other minimal hosts.
    /// </summary>
    public static IServiceCollection AddWorkflowDomainEvents(this IServiceCollection services)
    {
        services.AddAetherDomainEvents<MessagingDbContext>(options =>
        {
            options.DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox;
        });

        services.AddAetherOutbox<MessagingDbContext>();
        services.AddAetherInbox<MessagingDbContext>();

        return services;
    }
}
