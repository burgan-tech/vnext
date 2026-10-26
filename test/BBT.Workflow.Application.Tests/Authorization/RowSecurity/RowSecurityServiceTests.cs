using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Definitions.Schemas;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// <see cref="RowSecurityService"/>: lookup-failure mapping (R-F9 / D5), NotConfigured before role resolution
/// (C-6), the rule set and version come from the lookup handed in (K2), and the planner decides the rest.
/// </summary>
public sealed class RowSecurityServiceTests
{
    internal const string BranchRule =
        """{"roles":["branch-staff"],"filter":{"attributes":{"branchCode":{"eq":"$.context.Headers.x-branch-code"}}}}""";

    internal const string RegionRule =
        """{"roles":["branch-staff"],"filter":{"attributes":{"region":{"eq":"$.context.Headers.x-region"}}}}""";

    private readonly ICallerRoleResolver _resolver = Substitute.For<ICallerRoleResolver>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly RowSecurityService _svc;

    public RowSecurityServiceTests()
    {
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["branch-staff"], CallerRoleOutcome.Resolved));
        _svc = new RowSecurityService(_resolver, _currentUser, Options.Create(new RowSecurityOptions()));
    }

    /// <summary>A minimal flow; <paramref name="rules"/> null = no <c>rowSecurity</c>.</summary>
    internal static WorkflowDefinition Flow(string? rules, string version = "1.0.0")
    {
        var rowSecurity = rules is null ? "" : $$""","rowSecurity":{"rules":[{{rules}}]}""";
        var json = $$"""
            {
                "key": "rs-flow", "version": "{{version}}", "domain": "core", "flow": "sys-flows",
                "type": "F",
                "labels": [{"label": "Test", "language": "en"}],
                "states": [{"key": "review", "stateType": 2, "versionStrategy": "Minor",
                    "labels": [{"label": "Review", "language": "en"}], "transitions": []}],
                "sharedTransitions": [],
                "startTransition": {"key": "start", "target": "review", "versionStrategy": "Minor",
                    "triggerType": 0, "labels": [{"label": "Start", "language": "en"}]}
                {{rowSecurity}}
            }
            """;
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
    }

    private static RowSecurityRequest Req(Result<WorkflowDefinition> flow, Dictionary<string, string?>? headers = null) =>
        new("core", "rs-flow", flow, null, SchemaExposureMetadata.Empty, headers, null, null, null);

    [Fact]
    public async Task Decide_NoRowSecurity_NeverResolvesRoles() // C-6
    {
        var d = await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Ok(Flow(null))));

        d.Outcome.ShouldBe(RowSecurityOutcome.NotConfigured);
        d.Outcome.ToTagValue().ShouldBeNull();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task Decide_FlowLookupFailsWithNonNotFound_ReturnsLookupError_ForEveryFlow() // U-1, D5
    {
        var err = Error.Failure("cache", "down");

        var d = await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Fail(err)));

        d.Outcome.ShouldBe(RowSecurityOutcome.DeniedRuleSetUnavailable);
        d.LookupError.ShouldBe(err);
        d.ReturnsNoRows.ShouldBeTrue();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Theory] // R-F9: any ErrorType other than "no published definition" denies — NotFound-typed ones included.
    [InlineData(WorkflowErrorCodes.FlowDefaultVersionInvalid)]
    [InlineData(WorkflowErrorCodes.VersionRegistryUnavailable)]
    [InlineData(WorkflowErrorCodes.FlowVersionNotActivated)]
    [InlineData("Other:999")]
    public async Task Decide_AnyLookupErrorButCacheItemNotFound_IsDenied_EvenAsNotFoundType(string code)
    {
        foreach (var err in new[] { Error.NotFound(code, "x"), Error.Conflict(code, "x"), Error.Transient(code, "x") })
        {
            var d = await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Fail(err)));

            d.Outcome.ShouldBe(RowSecurityOutcome.DeniedRuleSetUnavailable);
            d.LookupError.ShouldBe(err);
        }
    }

    [Fact]
    public async Task Decide_FlowNotFound_IsNotConfigured()
    {
        var d = await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Fail(
            Error.NotFound(WorkflowErrorCodes.CacheItemNotFound, "Workflow not found"))));

        d.Outcome.ShouldBe(RowSecurityOutcome.NotConfigured);
        d.LookupError.ShouldBeNull();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task Decide_UsesPolicyVersionFromLookup() // K2: the version handed in decides, and is reported
    {
        var headers = new Dictionary<string, string?> { ["x-branch-code"] = "BR-001", ["x-region"] = "north" };

        var d = await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Ok(Flow(BranchRule, "1.0.0")), headers));

        d.Outcome.ShouldBe(RowSecurityOutcome.Filtered);
        d.FlowVersion.ShouldBe("1.0.0");
        JsonSerializer.Serialize(d.Predicate).ShouldContain("branchCode");
        JsonSerializer.Serialize(d.Predicate).ShouldNotContain("region");
    }

    [Fact]
    public async Task Decide_PassesNonNullHeaders_ToResolver_EvenWhenRequestHasNone()
    {
        await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Ok(Flow(BranchRule)), headers: null));

        await _resolver.Received(1).ResolveRoleSetAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decide_ResolutionFailed_IsDenied()
    {
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution([], CallerRoleOutcome.Failed, "status"));

        var d = await _svc.DecideAsync(Req(Result<WorkflowDefinition>.Ok(Flow(BranchRule)),
            new Dictionary<string, string?> { ["x-branch-code"] = "BR-001" }));

        d.Outcome.ShouldBe(RowSecurityOutcome.DeniedResolutionFailed);
        d.FlowVersion.ShouldBe("1.0.0");
    }
}
