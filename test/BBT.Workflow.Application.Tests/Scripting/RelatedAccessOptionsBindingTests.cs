using System.Collections.Generic;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Regression coverage for a bug where <see cref="RelatedAccessOptions"/> was never bound to
/// configuration: nothing called <c>Configure&lt;RelatedAccessOptions&gt;</c> or
/// <c>AddOptions&lt;RelatedAccessOptions&gt;</c>, so <c>Workflow:Scripting:RelatedAccess:MaxResolutionsPerContext</c>
/// in <c>appsettings.json</c> — the exact key the docs and the accessor's own exception message point
/// operators at — did nothing. The cap stayed permanently at the hard-coded default of 10.
/// </summary>
public sealed class RelatedAccessOptionsBindingTests
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
    public void ConfiguredValue_ShouldReachRelatedAccessOptions_MaxResolutionsPerContext()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RelatedAccessOptions.SectionName}:MaxResolutionsPerContext"] = "25"
            })
            .Build();

        using var provider = BuildProvider(configuration);
        using var scope = provider.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<RelatedAccessOptions>>();

        options.Value.MaxResolutionsPerContext.ShouldBe(25);
    }

    [Fact]
    public void MissingSection_ShouldFallBackToTheBuiltInDefault()
    {
        var configuration = new ConfigurationBuilder().Build();

        using var provider = BuildProvider(configuration);
        using var scope = provider.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<RelatedAccessOptions>>();

        options.Value.MaxResolutionsPerContext.ShouldBe(10);
    }
}
