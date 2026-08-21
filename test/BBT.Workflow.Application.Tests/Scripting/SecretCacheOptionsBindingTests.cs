using System.Collections.Generic;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Verifies that <see cref="SecretCacheOptions"/> is bound from configuration
/// (section <c>Scripting:SecretCache</c>) by <c>AddTaskHandlers</c>, and that a missing
/// section keeps the built-in defaults (enabled, 30 second TTL). The options are registered
/// as a concrete singleton via the BindSection pattern, like the sandbox/helpers options.
/// </summary>
public sealed class SecretCacheOptionsBindingTests
{
    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton(_ => Substitute.For<IComponentCacheStore>());
        services.AddScoped(_ => Substitute.For<IRelatedInstanceReader>());
        services.AddScoped(_ => Substitute.For<IInstanceCorrelationRepository>());
        services.AddTaskHandlers();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void ConfiguredValues_ShouldReachSecretCacheOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SecretCacheOptions.SectionName}:Enabled"] = "false",
                [$"{SecretCacheOptions.SectionName}:TtlSeconds"] = "5"
            })
            .Build();

        using var provider = BuildProvider(configuration);

        var options = provider.GetRequiredService<SecretCacheOptions>();

        options.Enabled.ShouldBeFalse();
        options.TtlSeconds.ShouldBe(5);
    }

    [Fact]
    public void MissingSection_ShouldFallBackToTheBuiltInDefaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        using var provider = BuildProvider(configuration);

        var options = provider.GetRequiredService<SecretCacheOptions>();

        options.Enabled.ShouldBeTrue();
        options.TtlSeconds.ShouldBe(30);
    }
}
