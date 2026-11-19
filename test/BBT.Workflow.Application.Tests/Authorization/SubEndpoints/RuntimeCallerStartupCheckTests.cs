using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// The startup line every host writes (vnext#1265): operators compare it across hosts before enabling the
/// gate, and the Critical 50049 is the audit trail for running a key ring with the script sandbox off.
/// </summary>
public sealed class RuntimeCallerStartupCheckTests
{
    private const int NotConfigured = 50048;
    private const int KeyRingWithoutSandbox = 50049;

    private readonly CapturingLogger _logger = new();

    [Fact]
    public async Task StartAsync_ShouldLogNotConfigured_AndNothingElse_WithoutAKeyRing()
    {
        await StartAsync(ring: false, runsFlowScripts: true, allowWithoutSandbox: true, sandboxEnabled: false);

        _logger.Entries.ShouldHaveSingleItem().ShouldBe((NotConfigured, LogLevel.Information));
    }

    [Fact]
    public async Task StartAsync_ShouldLogCritical_ForAKeyRingOnAScriptHostWithTheSandboxBypassed()
    {
        await StartAsync(ring: true, runsFlowScripts: true, allowWithoutSandbox: true, sandboxEnabled: false);

        _logger.Entries.ShouldHaveSingleItem().ShouldBe((KeyRingWithoutSandbox, LogLevel.Critical));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public async Task StartAsync_ShouldLogNothing_WhenTheSandboxIsOn_OrTheHostRunsNoScripts(
        bool runsFlowScripts, bool allowWithoutSandbox, bool sandboxEnabled)
    {
        await StartAsync(ring: true, runsFlowScripts, allowWithoutSandbox, sandboxEnabled);

        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_ShouldLogNothing_ForTheSandboxOff_WithoutTheBypass()
    {
        // RuntimeCallerOptionsValidator refuses this state at startup, so the Critical line is reserved for
        // the explicit bypass.
        await StartAsync(ring: true, runsFlowScripts: true, allowWithoutSandbox: false, sandboxEnabled: false);

        _logger.Entries.ShouldBeEmpty();
    }

    private Task StartAsync(bool ring, bool runsFlowScripts, bool allowWithoutSandbox, bool sandboxEnabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scripting:Sandbox:Enabled"] = sandboxEnabled.ToString() })
            .Build();
        var check = new RuntimeCallerStartupCheck(
            Identity(ring ? DefaultRing() : null),
            Options.Create(new RuntimeCallerOptions { AllowWithoutScriptSandbox = allowWithoutSandbox }),
            configuration,
            _logger,
            runsFlowScripts);

        return check.StartAsync(CancellationToken.None);
    }

    private sealed class CapturingLogger : ILogger<RuntimeCallerStartupCheck>
    {
        public ConcurrentQueue<(int EventId, LogLevel Level)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((eventId.Id, logLevel));
    }
}
