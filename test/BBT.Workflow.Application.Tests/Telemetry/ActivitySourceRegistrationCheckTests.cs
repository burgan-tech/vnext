using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// The startup half of the registration rule — the half that can see a deployment.
/// </summary>
public sealed class ActivitySourceRegistrationCheckTests
{
    [Fact]
    public async Task WarnsWhenAnIndexOverrideSilentlyReplacesTheFirstSource()
    {
        // Exactly the shape a chart's free-form env block produces: .NET merges configuration
        // arrays by INDEX, so "AdditionalSources__0" does not append — it REPLACES the first entry.
        // Here that costs BBT.Workflow.Pipeline, and with it every pipeline span and the activation
        // metric, with no code change and no error anywhere.
        var effective = TelemetryConstants.ActivitySources.All.ToList();
        effective[0] = "SomeTeam.Custom.Source";

        var logger = Substitute.For<ILogger<ActivitySourceRegistrationCheck>>();
        await CreateCheck(effective, logger).StartAsync(CancellationToken.None);

        logger.ReceivedWithAnyArgs(1).Log(
            LogLevel.Warning, default, default(object)!, null, default(Func<object, Exception?, string>)!);
    }

    [Fact]
    public async Task StaysSilentWhenEveryRequiredSourceIsPresent()
    {
        var logger = Substitute.For<ILogger<ActivitySourceRegistrationCheck>>();

        await CreateCheck(TelemetryConstants.ActivitySources.All.ToList(), logger)
            .StartAsync(CancellationToken.None);

        logger.DidNotReceiveWithAnyArgs().Log(
            default, default, default(object)!, null, default(Func<object, Exception?, string>)!);
    }

    /// <summary>
    /// A host that covers its own family with a wildcard is correctly configured, and warning about
    /// it would train everyone to ignore this log — which is how a real gap gets missed.
    /// </summary>
    [Fact]
    public async Task AcceptsWildcardCoverage()
    {
        var wildcards = TelemetryConstants.ActivitySources.All
            .Select(source => source[..(source.IndexOf('.') + 1)] + "*")
            .Distinct()
            .ToList();

        var logger = Substitute.For<ILogger<ActivitySourceRegistrationCheck>>();
        await CreateCheck(wildcards, logger).StartAsync(CancellationToken.None);

        logger.DidNotReceiveWithAnyArgs().Log(
            default, default, default(object)!, null, default(Func<object, Exception?, string>)!);
    }

    /// <summary>
    /// Sources a host deliberately omits must never warn: they are the reason the check is worth
    /// reading at all.
    /// </summary>
    [Fact]
    public async Task DoesNotWarnAboutDeliberatelyUnregisteredSources()
    {
        var required = TelemetryConstants.ActivitySources.All
            .Where(source => !TelemetryConstants.ActivitySources.DeliberatelyUnregistered.ContainsKey(source))
            .ToList();

        var logger = Substitute.For<ILogger<ActivitySourceRegistrationCheck>>();
        await CreateCheck(required, logger).StartAsync(CancellationToken.None);

        logger.DidNotReceiveWithAnyArgs().Log(
            default, default, default(object)!, null, default(Func<object, Exception?, string>)!);
    }

    private static ActivitySourceRegistrationCheck CreateCheck(
        IReadOnlyList<string> sources, ILogger<ActivitySourceRegistrationCheck> logger)
    {
        var values = sources
            .Select((source, index) => new KeyValuePair<string, string?>(
                $"Telemetry:Tracing:AdditionalSources:{index}", source));

        // The source-generated LoggerMessage extension asks IsEnabled first; a substitute answers
        // false by default, which would make every one of these assertions pass for the wrong reason.
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ActivitySourceRegistrationCheck(configuration, logger);
    }
}
