using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Middlewares;
using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Pins the startup side of the Dapr local-delivery guard: Enforce without a token file fails
/// validation, and LogOnly without one is announced once (EventId 40998) by a hosted service rather
/// than by the middleware.
/// </summary>
public sealed class DaprLocalDeliveryStartupTests
{
    private static DaprLocalDeliveryOptionsValidator Validator(string? tokenFile) =>
        new(Options.Create(new RuntimeCallerOptions { AppApiTokenFile = tokenFile }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Validator_Enforce_WithoutTokenFile_Fails(string? tokenFile)
    {
        var result = Validator(tokenFile)
            .Validate(null, new DaprLocalDeliveryOptions { Mode = DaprLocalDeliveryMode.Enforce });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Workflow:RuntimeCaller:AppApiTokenFile");
    }

    [Fact]
    public void Validator_Enforce_WithTokenFile_Succeeds()
        => Validator("/var/run/secrets/app-api-token/token")
            .Validate(null, new DaprLocalDeliveryOptions { Mode = DaprLocalDeliveryMode.Enforce })
            .Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(DaprLocalDeliveryMode.Off)]
    [InlineData(DaprLocalDeliveryMode.LogOnly)]
    public void Validator_OtherModes_WithoutTokenFile_Succeed(DaprLocalDeliveryMode mode)
        => Validator(null).Validate(null, new DaprLocalDeliveryOptions { Mode = mode }).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(DaprLocalDeliveryMode.LogOnly, null, true)]
    [InlineData(DaprLocalDeliveryMode.LogOnly, "s3cret", false)]
    [InlineData(DaprLocalDeliveryMode.Off, null, false)]
    [InlineData(DaprLocalDeliveryMode.Enforce, "s3cret", false)]
    public async Task StartupCheck_Logs40998_OnlyForLogOnlyWithoutToken(
        DaprLocalDeliveryMode mode, string? token, bool expected)
    {
        var logger = new EventIdLogger();
        var check = new DaprLocalDeliveryStartupCheck(
            Options.Create(new DaprLocalDeliveryOptions { Mode = mode }),
            new DaprAppApiToken(token),
            logger);

        await check.StartAsync(CancellationToken.None);

        logger.EventIds.Contains(40998).ShouldBe(expected);
    }

    private sealed class EventIdLogger : ILogger<DaprLocalDeliveryStartupCheck>
    {
        public List<int> EventIds { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => EventIds.Add(eventId.Id);
    }
}
