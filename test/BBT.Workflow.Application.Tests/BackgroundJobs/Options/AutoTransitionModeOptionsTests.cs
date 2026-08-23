using System.Collections.Generic;
using BBT.Workflow.BackgroundJobs.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs;

/// <summary>
/// Pins <see cref="WorkflowExecutionOptions.AutoTransitionMode"/>: the default is
/// <see cref="AutoTransitionMode.Inline"/> (no scheduler round trip per chained hop) and the
/// setting binds from configuration by name, so an environment can opt back into per-hop
/// scheduler jobs without a code change.
/// </summary>
public class AutoTransitionModeOptionsTests
{
    [Fact]
    public void Default_IsInline()
    {
        // The default lives in code, not in appsettings: a host that never writes the key still
        // gets the low-latency path.
        new WorkflowExecutionOptions().AutoTransitionMode.ShouldBe(AutoTransitionMode.Inline);
    }

    [Theory]
    [InlineData("Inline", AutoTransitionMode.Inline)]
    [InlineData("Scheduled", AutoTransitionMode.Scheduled)]
    [InlineData("scheduled", AutoTransitionMode.Scheduled)]
    public void BindsFromConfiguration(string configured, AutoTransitionMode expected)
    {
        var options = Bind(new Dictionary<string, string?>
        {
            [$"{WorkflowExecutionOptions.SectionName}:{nameof(WorkflowExecutionOptions.AutoTransitionMode)}"] =
                configured
        });

        options.AutoTransitionMode.ShouldBe(expected);
    }

    [Fact]
    public void AbsentKey_KeepsInlineDefault()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            [$"{WorkflowExecutionOptions.SectionName}:{nameof(WorkflowExecutionOptions.TransitionJobTimeoutSeconds)}"] =
                "120"
        });

        options.AutoTransitionMode.ShouldBe(AutoTransitionMode.Inline);
        options.TransitionJobTimeoutSeconds.ShouldBe(120);
    }

    private static WorkflowExecutionOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkflowExecutionOptions>(
            configuration.GetSection(WorkflowExecutionOptions.SectionName));

        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<WorkflowExecutionOptions>>()
            .Value;
    }
}
