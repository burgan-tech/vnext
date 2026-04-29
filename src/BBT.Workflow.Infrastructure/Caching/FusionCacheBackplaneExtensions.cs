using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;

namespace BBT.Workflow.Infrastructure.Caching;

/// <summary>
/// Wires a FusionCache Redis backplane using the same "Redis" configuration section that
/// the Aether AddRedis() pipeline already consumes. Picked up by the Application-layer
/// FusionCache builder via <c>TryWithRegisteredBackplane()</c>, so cross-pod L1 invalidations
/// flow through Redis pub/sub without any callsite changes in consumers.
/// </summary>
public static class FusionCacheBackplaneExtensions
{
    private const string RedisSectionName = "Redis";
    private const string DefaultChannelPrefix = "workflow.fusion";

    /// <summary>
    /// Registers a StackExchange.Redis-backed FusionCache backplane if the host has a
    /// Redis configuration section. Safe to call when Redis is not configured: in that case
    /// the registration is skipped and FusionCache continues to operate without a backplane.
    /// </summary>
    public static IServiceCollection AddWorkflowFusionCacheBackplane(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redisSection = configuration.GetSection(RedisSectionName);
        if (!redisSection.Exists())
            return services;

        var connectionString = BuildConnectionString(redisSection);
        if (string.IsNullOrWhiteSpace(connectionString))
            return services;

        // Channel-prefix isolates this pod-cluster's invalidation traffic from any other
        // FusionCache deployment that may share the same Redis instance.
        var channelPrefix = redisSection.GetValue<string>("InstanceName") ?? DefaultChannelPrefix;

        services.AddFusionCacheStackExchangeRedisBackplane(opt =>
        {
            opt.Configuration = connectionString;
            opt.ChannelPrefix = channelPrefix;
        });

        return services;
    }

    private static string? BuildConnectionString(IConfigurationSection redisSection)
    {
        var endpoints = redisSection
            .GetSection("Standalone:EndPoints")
            .Get<string[]>();

        if (endpoints is null || endpoints.Length == 0)
            return null;

        var parts = new List<string>(endpoints);

        var password = redisSection.GetValue<string>("Password");
        if (!string.IsNullOrEmpty(password))
            parts.Add($"password={password}");

        var ssl = redisSection.GetValue<bool?>("Ssl");
        if (ssl == true)
            parts.Add("ssl=true");

        var connectionTimeout = redisSection.GetValue<int?>("ConnectionTimeout");
        if (connectionTimeout is > 0)
            parts.Add($"connectTimeout={connectionTimeout.Value.ToString(CultureInfo.InvariantCulture)}");

        var defaultDatabase = redisSection.GetValue<int?>("DefaultDatabase");
        if (defaultDatabase is >= 0)
            parts.Add($"defaultDatabase={defaultDatabase.Value.ToString(CultureInfo.InvariantCulture)}");

        // Backplane fan-out can be bursty during component publishes; abortConnect=false lets
        // FusionCache start without Redis and retry transparently when it comes back.
        parts.Add("abortConnect=false");

        return string.Join(",", parts);
    }
}
