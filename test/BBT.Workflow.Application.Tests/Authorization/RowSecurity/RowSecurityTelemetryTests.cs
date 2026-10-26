using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Row-security observability (vnext#1107, Task 9): EventIds 20505-20509, read-span tags, and the rule that
/// logs and tags carry names, indexes and codes only — never a parameter, header or identity value.
/// </summary>
public sealed class RowSecurityTelemetryTests : IDisposable
{
    private const string Canary = "CANARY-7f3a";

    private readonly ICallerRoleResolver _resolver = Substitute.For<ICallerRoleResolver>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly CapturingLogger _logs = new();
    private readonly Activity _span = new("rowsecurity-telemetry-test");
    private readonly RowSecurityService _svc;

    public RowSecurityTelemetryTests()
    {
        _span.Start();
        _currentUser.UserName.Returns("user-" + Canary);
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["branch-staff"], CallerRoleOutcome.Resolved));
        _svc = new RowSecurityService(_resolver, _currentUser, Options.Create(new RowSecurityOptions()), _logs);
    }

    public void Dispose() => _span.Stop();

    private static RowSecurityRequest Req(
        Result<WorkflowDefinition> flow,
        Dictionary<string, string?>? headers = null,
        SchemaFilterContext? schema = null,
        string surface = RowSecuritySurfaces.List) =>
        new("core", "rs-flow", flow, schema, SchemaExposureMetadata.Empty, headers, null, null, null, surface);

    private static Result<WorkflowDefinition> Ok(string? rules, string version = "1.0.0") =>
        Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(rules, version));

    private object? Tag(string name) => _span.GetTagItem(name);

    [Fact]
    public async Task Filtered_Tags_And_Logs_NamesOnly()
    {
        var decision = await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule),
            new() { ["x-branch-code"] = Canary }));

        decision.Outcome.ShouldBe(RowSecurityOutcome.Filtered);
        Tag("vnext.rowsecurity.applied").ShouldBe(true);
        Tag("vnext.rowsecurity.outcome").ShouldBe("filtered");
        Tag("vnext.rowsecurity.rules").ShouldBe("0");
        _logs.Single(20505).Level.ShouldBe(LogLevel.Information);
        _logs.Single(20505).Message.ShouldContain("outcome=filtered");
        _logs.Single(20505).Message.ShouldContain("list");
        AssertNoCanary();
    }

    [Fact]
    public async Task Canary_NeverAppearsInAnyLogOrTag_AcrossEveryOutcome()
    {
        var number = SchemaFilterMetadataResolver.Resolve(
            JsonDocument.Parse("""{"type":"object","properties":{"amount":{"type":"number"}}}""").RootElement)!;
        const string numberRule =
            """{"roles":["branch-staff"],"filter":{"attributes":{"amount":{"ge":"$.context.Headers.x-min"}}}}""";
        var headers = new Dictionary<string, string?> { ["x-branch-code"] = Canary, ["x-min"] = Canary, ["role"] = Canary };

        await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule), headers));          // filtered
        await _svc.DecideAsync(Req(Ok(numberRule), headers, number));                         // coercion drop
        await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule), new() { ["x-branch-code"] = Canary }));

        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution([], CallerRoleOutcome.Failed, "status"));
        await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule), headers));          // resolution failed
        await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Fail(Error.Conflict("Instance:100052", Canary)), headers));

        _logs.Entries.Count.ShouldBeGreaterThan(4);
        AssertNoCanary();
    }

    [Fact]
    public async Task NotConfigured_AddsNoTag_AndNoLog()
    {
        await _svc.DecideAsync(Req(Ok(null)));
        await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Fail(
            Error.NotFound(WorkflowErrorCodes.CacheItemNotFound, "x"))));

        _span.TagObjects.Any(t => t.Key.StartsWith("vnext.rowsecurity.")).ShouldBeFalse();
        _logs.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Filtered_TagsFlowVersion() // K2: the version the pointer selected
    {
        await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule, "1.0.0"),
            new() { ["x-branch-code"] = "BR-001" }));

        Tag("vnext.rowsecurity.flow_version").ShouldBe("1.0.0");
        _logs.Single(20505).Message.ShouldContain("rs-flow@1.0.0");
    }

    [Fact]
    public async Task CoercionFailure_LogsReasonNotExceptionText()
    {
        var number = SchemaFilterMetadataResolver.Resolve(
            JsonDocument.Parse("""{"type":"object","properties":{"amount":{"type":"number"}}}""").RootElement)!;
        const string rule = """{"roles":["branch-staff"],"filter":{"attributes":{"amount":{"ge":"$.context.Headers.x-min"}}}}""";

        await _svc.DecideAsync(Req(Ok(rule), new() { ["x-min"] = "abc" }, number));

        var dropped = _logs.Single(20507);
        dropped.Level.ShouldBe(LogLevel.Warning);
        dropped.Message.ShouldContain("reason=coercion");
        dropped.Message.ShouldContain("rule=0");
        dropped.Message.ShouldContain("x-min");
        dropped.Message.ShouldNotContain("abc");
        dropped.Message.ShouldNotContain("FormatException");
        Tag("vnext.rowsecurity.outcome").ShouldBe("denied-no-contribution");
    }

    [Fact]
    public async Task MissingParameter_TagsNamesOnly()
    {
        await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule), new()));

        Tag("vnext.rowsecurity.missing_params").ShouldBe("x-branch-code");
    }

    [Fact]
    public async Task ResolutionFailed_Logs20508_Warning()
    {
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution([], CallerRoleOutcome.Failed, "status"));

        await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule), new() { ["x-branch-code"] = "BR-001" }));

        var entry = _logs.Single(20508);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("kind=status");
        Tag("vnext.rowsecurity.outcome").ShouldBe("denied-resolution-failed");
    }

    [Fact]
    public async Task RuleSetUnavailable_Logs20509_WithErrorCode_AndTagsDeniedFlowLoad()
    {
        await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Fail(Error.Conflict("Instance:100052", "boom"))));

        var entry = _logs.Single(20509);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("Instance:100052");
        Tag("vnext.rowsecurity.outcome").ShouldBe("denied-flow-load");
        Tag("vnext.rowsecurity.flow_version").ShouldBeNull();
    }

    [Fact]
    public async Task NoMatchingRule_TagsDeniedNoMatch_AndGetSurfaceIsLogged()
    {
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["other"], CallerRoleOutcome.Resolved));

        await _svc.DecideAsync(Req(Ok(RowSecurityServiceTests.BranchRule), surface: RowSecuritySurfaces.Get));

        Tag("vnext.rowsecurity.outcome").ShouldBe("denied-no-match");
        _logs.Single(20505).Message.ShouldContain("get");
    }

    [Fact]
    public async Task ExplicitSpan_IsTaggedInsteadOfAmbient()
    {
        using var other = new Activity("explicit-read-span");
        other.Start();
        var request = Req(Ok(RowSecurityServiceTests.BranchRule), new() { ["x-branch-code"] = "BR-001" }) with { Span = other };

        await _svc.DecideAsync(request);

        other.GetTagItem("vnext.rowsecurity.outcome").ShouldBe("filtered");
    }

    [Fact]
    public void Excluded_OverwritesOutcomeTag()
    {
        _span.SetTag("vnext.rowsecurity.outcome", "filtered");

        InstanceReadActivityHelper.SetRowSecurityExcluded(_span);

        Tag("vnext.rowsecurity.outcome").ShouldBe("excluded");
    }

    private void AssertNoCanary()
    {
        _logs.Entries.All(e => !e.Message.Contains(Canary) && e.State.All(kv => kv.Value?.ToString()?.Contains(Canary) != true))
            .ShouldBeTrue();
        _logs.Entries.All(e => !e.Message.Contains("user-")).ShouldBeTrue();
        _span.Tags.All(t => t.Value?.Contains(Canary) != true).ShouldBeTrue();
    }

    private sealed record Entry(
        int EventId, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class CapturingLogger : ILogger<RowSecurityService>
    {
        public List<Entry> Entries { get; } = [];

        public Entry Single(int eventId) => Entries.Single(e => e.EventId == eventId);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var kv = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add(new Entry(eventId.Id, logLevel, formatter(state, exception) +
                (exception is null ? "" : " " + exception), kv));
        }
    }
}
