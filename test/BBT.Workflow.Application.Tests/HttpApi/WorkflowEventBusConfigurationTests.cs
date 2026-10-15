using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Events;
using BBT.Workflow.HttpApi.Shared.Events;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Unit tests for <see cref="WorkflowEventBusConfiguration"/>: every runtime topic is
/// <c>vnext.{APP_DOMAIN}.{environment}.*</c>, the prefix the broker ACL is granted on.
/// </summary>
public sealed class WorkflowEventBusConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Topic_prefix_is_domain_and_lower_cased_environment()
    {
        var configuration = Config(("APP_DOMAIN", "core"), ("ASPNETCORE_ENVIRONMENT", "Production"));

        WorkflowEventBusConfiguration.BuildTopicPrefix(configuration).ShouldBe("vnext.core.production");
    }

    [Fact]
    public void Missing_environment_follows_the_host_default_production()
    {
        var configuration = Config(("APP_DOMAIN", "core"));

        WorkflowEventBusConfiguration.BuildTopicPrefix(configuration).ShouldBe("vnext.core.production");
    }

    [Fact]
    public void Missing_domain_fails_fast()
    {
        var configuration = Config(("ASPNETCORE_ENVIRONMENT", "Development"));

        Should.Throw<InvalidOperationException>(() => WorkflowEventBusConfiguration.BuildTopicPrefix(configuration));
    }

    [Fact]
    public void Apply_sets_prefix_and_runtime_event_topic_follows_it()
    {
        var configuration = Config(
            ("APP_DOMAIN", "core"),
            ("ASPNETCORE_ENVIRONMENT", "Development"),
            ("DAPR_PUBSUB_STORE_NAME", "vnext-pubsub"),
            ("ApplicationName", "Orchestration"));
        var options = new AetherEventBusOptions();

        WorkflowEventBusConfiguration.Apply(options, configuration);

        options.TopicPrefix.ShouldBe("vnext.core.development");
        options.PubSubName.ShouldBe("vnext-pubsub");
        options.DefaultSource.ShouldBe("urn:vnext:orchestration");
        TopicNameFormatter.Format("instance.sub.completed", 1, null, options, "Development")
            .ShouldBe("vnext.core.development.instance.sub.completed.v1");
    }
}
