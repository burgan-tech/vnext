using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// What the sub-endpoint gate tells an operator (vnext#1265): the Observe-to-Enforce procedure reads the
/// 50050/50051 lines and the <c>vnext.gate.*</c> / <c>vnext.runtime_caller.*</c> span tags, so each decision
/// is pinned here — with the grant reason and the token verdict kept apart, and nothing from an
/// unverified token written out.
/// </summary>
public sealed class SubEndpointGateObservabilityTests : IDisposable
{
    // A listener callback that throws poisons Activity for the whole test process, so it never touches a
    // static that may not be initialised yet: it compares against a constant.
    private const string SourceName = "BBT.Workflow.Tests.SubEndpointGate";
    private static readonly ActivitySource Source = new(SourceName);
    private static readonly IReadOnlyDictionary<string, string?> Headers = new Dictionary<string, string?>();
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly RuntimeCallTarget Complete = RuntimeCallTarget.Complete("core", "parent", Parent);
    private static readonly SubEndpointGrantOptions RuntimeOnly = new() { Role = "$Everyone", Grant = "deny" };
    private static readonly SubEndpointGrantOptions AllowOps = new() { Role = "vnext.ops", Grant = "allow" };

    private readonly ActivityListener _listener;
    private readonly ICallerRoleResolver _roles = Substitute.For<ICallerRoleResolver>();
    private readonly CapturingLogger _logger = new();

    public SubEndpointGateObservabilityTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private async Task<Activity> DecideAsync(SubEndpointGateMode mode, SubEndpointGrantOptions grant, string? token)
    {
        var gate = new SubEndpointGate(
            Options.Create(new SubEndpointGateOptions { Mode = mode, Grants = [grant] }),
            _roles, _logger, Identity(DefaultRing()));
        using var activity = Source.StartActivity("gate")!;
        await gate.AuthorizeAsync(Complete, token, Headers);
        return activity;
    }

    private static string Token(DateTimeOffset at)
        => RuntimeCallerToken.Create("k1", Key(1), Complete, "partner", RuntimeCallerClasses.Relay, at);

    private void CallerRoles(CallerRoleResolution resolution)
        => _roles.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(resolution);

    [Fact]
    public async Task AdmittedRuntime_ShouldTagVerdictAndCaller_AndLogNothing()
    {
        var activity = await DecideAsync(SubEndpointGateMode.Enforce, RuntimeOnly, Token(Now.AddSeconds(-7)));

        activity.GetTagItem(TelemetryConstants.TagNames.GateDecision).ShouldBe("admitted_runtime");
        activity.GetTagItem(TelemetryConstants.TagNames.GateReason).ShouldBeNull();
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerVerdict).ShouldBe("valid");
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerClass).ShouldBe(RuntimeCallerClasses.Relay);
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerDomain).ShouldBe("partner");
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerKeyId).ShouldBe("k1");
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerSkewSeconds).ShouldBe(7L);
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task AdmittedGrant_ShouldTagDecision_WithTheTokenVerdictApart()
    {
        CallerRoles(new CallerRoleResolution(["vnext.ops"], CallerRoleOutcome.FromHeader));

        var activity = await DecideAsync(SubEndpointGateMode.Enforce, AllowOps, token: null);

        activity.GetTagItem(TelemetryConstants.TagNames.GateDecision).ShouldBe("admitted_grant");
        activity.GetTagItem(TelemetryConstants.TagNames.GateReason).ShouldBeNull();
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerVerdict).ShouldBe("missing");
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Observe_ShouldLogWouldRefuse_WithGrantReasonAndTokenReasonApart()
    {
        var activity = await DecideAsync(SubEndpointGateMode.Observe, RuntimeOnly, Token(Now.AddMinutes(-30)));

        activity.GetTagItem(TelemetryConstants.TagNames.GateDecision).ShouldBe("would_refuse");
        activity.GetTagItem(TelemetryConstants.TagNames.GateReason).ShouldBe("grant_denied");
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerVerdict).ShouldBe("clock_skew");
        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.EventId.ShouldBe(50051);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("Reason: grant_denied");
        entry.Message.ShouldContain("TokenReason: clock_skew");
        entry.Message.ShouldContain($"instance {Parent:D}");
        entry.Message.ShouldContain("CallerClass: relay");
    }

    [Fact]
    public async Task Enforce_ShouldLogRefused_AndKeepForgedClaimsOut()
    {
        var forged = RuntimeCallerToken.Create("k1", Key(99), Complete, "evil\r\nInjected", "subflow", Now);

        var activity = await DecideAsync(SubEndpointGateMode.Enforce, RuntimeOnly, forged);

        activity.GetTagItem(TelemetryConstants.TagNames.GateDecision).ShouldBe("refused");
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerVerdict).ShouldBe("bad_signature");
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerClass).ShouldBeNull();
        activity.GetTagItem(TelemetryConstants.TagNames.RuntimeCallerDomain).ShouldBeNull();
        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.EventId.ShouldBe(50050);
        entry.Message.ShouldContain("CallerClass: -, CallerDomain: -");
        entry.Message.ShouldNotContain("Injected");
    }

    [Fact]
    public async Task Enforce_ShouldKeepControlCharactersInRouteValuesOutOfTheLog()
    {
        // Route values are decoded URL text: %0D%0A in a path segment reaches the gate as CR LF.
        var gate = new SubEndpointGate(
            Options.Create(new SubEndpointGateOptions { Mode = SubEndpointGateMode.Enforce, Grants = [RuntimeOnly] }),
            _roles, _logger, Identity(DefaultRing()));

        await gate.AuthorizeAsync(RuntimeCallTarget.Complete("core", "parent\r\nInjected: yes", "x\ny"), null, Headers);

        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("core/parent??Injected: yes, instance x?y");
        entry.Message.ShouldNotContain("\n");
        entry.Message.ShouldNotContain("\r");
    }

    [Fact]
    public async Task Enforce_RolesUnavailable_ShouldTagUnavailable_AndLogItsReason()
    {
        CallerRoles(new CallerRoleResolution([], CallerRoleOutcome.Failed, "unavailable"));

        var activity = await DecideAsync(SubEndpointGateMode.Enforce, AllowOps, token: null);

        activity.GetTagItem(TelemetryConstants.TagNames.GateDecision).ShouldBe("unavailable");
        activity.GetTagItem(TelemetryConstants.TagNames.GateReason).ShouldBe("roles_unavailable");
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("Reason: roles_unavailable");
    }

    private sealed class CapturingLogger : ILogger<SubEndpointGate>
    {
        public ConcurrentQueue<(int EventId, LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
    }
}
