using BBT.Aether.Events;
using BBT.Workflow.Execution;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.HttpApi.Shared.Events;

/// <summary>
/// The event bus settings every vNext host shares (orchestration, execution, inbox and outbox
/// workers). Kept in one place because the publisher and the subscribers must compute the same
/// topic name — a host that drifts publishes to, or listens on, a topic nobody else uses.
/// </summary>
public static class WorkflowEventBusConfiguration
{
    /// <summary>Topic prefix root; every runtime topic is <c>vnext.{domain}.{environment}.*</c>.</summary>
    public const string TopicRoot = "vnext";

    /// <summary>
    /// Applies the shared settings: <c>DefaultSource</c>, <c>PubSubName</c> and the topic prefix
    /// <c>vnext.{APP_DOMAIN}.{ASPNETCORE_ENVIRONMENT}</c> (environment lower-cased). Broker ACLs are
    /// granted on <c>vnext.{domain}.*</c>, so a topic outside this prefix is refused by Kafka.
    /// Co-hosted domains (<c>APP_DOMAINS</c>) share the primary domain's topics: they run in the same
    /// processes and use the same broker user.
    /// </summary>
    public static void Apply(AetherEventBusOptions options, IConfiguration configuration)
    {
        options.DefaultSource =
            $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
        options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
        options.TopicPrefix = BuildTopicPrefix(configuration);
    }

    /// <summary>Builds <c>vnext.{APP_DOMAIN}.{environment}</c>.</summary>
    public static string BuildTopicPrefix(IConfiguration configuration)
    {
        var domain = configuration[VNextAppIds.ConfigKeys.AppDomain];
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new InvalidOperationException(
                $"{VNextAppIds.ConfigKeys.AppDomain} must be set: it names the event bus topics (vnext.{{domain}}.{{environment}}.*).");
        }

        // An unset ASPNETCORE_ENVIRONMENT is "Production" to the .NET host; the topic follows the
        // same rule so it always names the environment the process believes it is running in.
        var environment = configuration["ASPNETCORE_ENVIRONMENT"];
        if (string.IsNullOrWhiteSpace(environment))
        {
            environment = "Production";
        }

        return $"{TopicRoot}.{domain.Trim()}.{environment.Trim().ToLowerInvariant()}";
    }
}
