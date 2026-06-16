using System.Reflection;
using BBT.Workflow.Mcp.Auth;
using BBT.Workflow.Mcp.Clients;
using BBT.Workflow.Mcp.Meta;
using BBT.Workflow.Mcp.Tools;
using ModelContextProtocol.Server;

namespace BBT.Workflow.Mcp.Configuration;

/// <summary>
/// Shared service/tool registration used by both the stdio and Streamable HTTP hosts.
/// </summary>
public static class McpServerSetup
{
    /// <summary>
    /// Binds <see cref="McpOptions"/> and returns the resolved snapshot (used for conditional
    /// tool registration before the container is built).
    /// </summary>
    public static McpOptions AddVNextMcpOptions(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(McpOptions.SectionName);
        services.Configure<McpOptions>(section);
        return section.Get<McpOptions>() ?? new McpOptions();
    }

    /// <summary>
    /// Registers the typed Orchestration HTTP client from the resolved options.
    /// </summary>
    public static IServiceCollection AddOrchestrationClient(this IServiceCollection services, McpOptions options)
    {
        // Per-domain client authorization is enforced in OrchestrationHttpClient via this service.
        services.AddHttpContextAccessor();
        services.AddScoped<IDomainAuthorizer, DomainAuthorizer>();

        var userAgent = $"vnext-mcp/{ServerVersion}";
        services.AddHttpClient<IOrchestrationClient, OrchestrationHttpClient>(client =>
        {
            client.BaseAddress = new Uri(options.OrchestrationBaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            // Provenance: lets the Orchestration runtime see the call originated from an MCP client.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });

        return services;
    }

    private static string ServerVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Registers the npm-sourced <see cref="IMetaProvider"/> as a singleton plus the background
    /// warmup service that loads the pinned <c>vnext-meta</c> package at startup.
    /// </summary>
    public static IServiceCollection AddMetaProvider(this IServiceCollection services)
    {
        services.AddHttpClient("npm");
        services.AddSingleton<IMetaProvider, NpmMetaProvider>();
        services.AddHostedService<MetaWarmupHostedService>();
        return services;
    }

    /// <summary>
    /// Registers tool groups, honoring the <see cref="McpOptions"/> gates:
    /// read-only tools always; <c>get_mapping_code</c> only when <see cref="McpOptions.AllowCodeRead"/>;
    /// mutating tools only when <see cref="McpOptions.AllowMutations"/>. Documentation discovery is
    /// delegated to the Context7 MCP server, so no docs tools are registered here.
    /// </summary>
    public static IMcpServerBuilder AddVNextTools(this IMcpServerBuilder builder, McpOptions options)
    {
        builder
            .WithTools<ComponentTools>()
            .WithTools<RuntimeTools>()
            .WithTools<MetaTools>();

        if (options.AllowCodeRead)
            builder.WithTools<MappingCodeTools>();

        if (options.AllowMutations)
            builder.WithTools<MutatingRuntimeTools>();

        return builder;
    }
}
