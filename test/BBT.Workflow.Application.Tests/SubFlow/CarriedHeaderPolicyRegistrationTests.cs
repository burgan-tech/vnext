using System.Linq;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// The producers take <see cref="ICarriedHeaderPolicy"/> as an optional parameter, so a composition
/// root that forgot to register it would silently drop every carried header. These tests pin the
/// registration made by <c>AddApplicationModule</c>.
/// </summary>
public sealed class CarriedHeaderPolicyRegistrationTests
{
    [Fact]
    public void AddApplicationModule_Registers_The_Policy_As_A_Singleton()
    {
        var services = new ServiceCollection();
        services.AddApplicationModule();

        var descriptor = services.Single(d => d.ServiceType == typeof(ICarriedHeaderPolicy));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        descriptor.ImplementationType.ShouldBe(typeof(CarriedHeaderPolicy));
    }

    [Fact]
    public void Registered_Policy_Resolves_As_The_Same_NonNull_Instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddApplicationModule();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddOptions<SubFlowCarriedHeadersOptions>();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ICarriedHeaderPolicy>();
        first.ShouldNotBeNull();
        provider.GetRequiredService<ICarriedHeaderPolicy>().ShouldBeSameAs(first);
    }

    [Fact]
    public void The_Producers_Declare_The_Policy_Parameter_Of_The_Registered_Type()
    {
        foreach (var producer in new[]
                 {
                     typeof(HandleFinishStep),
                     typeof(BBT.Workflow.Execution.Pipeline.TransitionPipeline),
                     typeof(BBT.Workflow.Execution.PostCommit.PostCommitParentMutationService)
                 })
        {
            producer.GetConstructors().Single().GetParameters()
                .ShouldContain(p => p.ParameterType == typeof(ICarriedHeaderPolicy));
        }
    }

    [Theory]
    [InlineData(0, 4096, false)]
    [InlineData(64, 0, false)]
    [InlineData(-1, 4096, false)]
    [InlineData(64, 4096, true)]
    public void Options_Validation_Requires_Positive_Caps(int maxEntries, int maxValueLength, bool valid)
    {
        using var provider = BuildProviderWith(o =>
        {
            o.MaxEntries = maxEntries;
            o.MaxValueLength = maxValueLength;
        });

        var read = () => provider.GetRequiredService<IOptions<SubFlowCarriedHeadersOptions>>().Value;
        if (valid)
        {
            read.ShouldNotThrow();
        }
        else
        {
            read.ShouldThrow<OptionsValidationException>();
        }
    }

    [Fact]
    public void Options_Validation_Rejects_Null_AdditionalDenied()
    {
        using var provider = BuildProviderWith(o => o.AdditionalDenied = null!);

        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SubFlowCarriedHeadersOptions>>().Value);
    }

    private static ServiceProvider BuildProviderWith(System.Action<SubFlowCarriedHeadersOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddApplicationModule();
        services.Configure(configure);
        return services.BuildServiceProvider();
    }
}
