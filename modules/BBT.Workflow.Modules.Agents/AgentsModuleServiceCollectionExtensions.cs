using BBT.Workflow.Agents;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the AI agent runtime (issue #1105). Call from the orchestration host only.</summary>
public static class AgentsModuleServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the unavailable agent runtime and MCP gateway registered by the application layer with
    /// the MAF runtime and the MCP SDK transport.
    /// </summary>
    public static IServiceCollection AddAgentsModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IChatClientProvider, ChatClientProvider>();
        services.Replace(ServiceDescriptor.Singleton<IAgentRuntime, MafAgentRuntime>());
        services.Replace(ServiceDescriptor.Singleton<IMcpClientGateway, McpClientGateway>());
        return services;
    }
}
