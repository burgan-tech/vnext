using System;
using BBT.Workflow.Execution;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks;

/// <summary>
/// Pins the Dapr app-id conventions against the Helm chart, which is the deployment-time
/// authority (<c>vnext.daprAnnotations</c> in <c>charts/vnext/templates/_helpers.tpl</c>).
/// If these expectations change, the chart changed too — or something is about to resolve
/// to an app-id that does not exist.
/// </summary>
public sealed class VNextAppIdsTests
{
    #region Convention Tests

    [Theory]
    [InlineData("credit", "vnext-credit-app")]
    [InlineData("customers", "vnext-customers-app")]
    [InlineData("corporate-credits", "vnext-corporate-credits-app")]
    public void Orchestrator_Should_Match_Chart_Convention(string domain, string expected) =>
        VNextAppIds.Orchestrator(domain).ShouldBe(expected);

    [Theory]
    [InlineData("credit", "vnext-credit-execution-app")]
    [InlineData("customers", "vnext-customers-execution-app")]
    public void Execution_Should_Match_Chart_Convention(string domain, string expected) =>
        VNextAppIds.Execution(domain).ShouldBe(expected);

    [Fact]
    public void Worker_And_Migrator_Should_Match_Chart_Convention()
    {
        VNextAppIds.WorkerInbox("credit").ShouldBe("vnext-credit-worker-inbox-app");
        VNextAppIds.WorkerOutbox("credit").ShouldBe("vnext-credit-worker-outbox-app");
        VNextAppIds.DbMigrator("credit").ShouldBe("vnext-credit-db-migrator-app");
    }

    #endregion

    #region Normalization Tests

    /// <summary>
    /// App-ids become DNS names and are lower-cased by <see cref="Uri"/> on the invocation
    /// path, so an upper-cased domain must not leak through: the resulting app-id would no
    /// longer match the SPIFFE identity Dapr expects and mTLS would reject the connection.
    /// </summary>
    [Theory]
    [InlineData("CREDIT")]
    [InlineData("Credit")]
    [InlineData("  credit  ")]
    public void Should_Normalize_Domain_To_Lowercase_And_Trim(string domain) =>
        VNextAppIds.Orchestrator(domain).ShouldBe("vnext-credit-app");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Throw_When_Domain_Is_Missing(string? domain) =>
        Should.Throw<ArgumentException>(() => VNextAppIds.Orchestrator(domain!));

    #endregion

    #region Precedence Tests

    [Fact]
    public void OrchestratorOrDefault_Should_Prefer_Configured_Value() =>
        VNextAppIds.OrchestratorOrDefault("legacy-orchestrator", "credit")
            .ShouldBe("legacy-orchestrator");

    [Fact]
    public void ExecutionOrDefault_Should_Prefer_Configured_Value() =>
        VNextAppIds.ExecutionOrDefault("legacy-execution", "credit")
            .ShouldBe("legacy-execution");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OrchestratorOrDefault_Should_Fall_Back_To_Convention(string? configured) =>
        VNextAppIds.OrchestratorOrDefault(configured, "credit").ShouldBe("vnext-credit-app");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecutionOrDefault_Should_Fall_Back_To_Convention(string? configured) =>
        VNextAppIds.ExecutionOrDefault(configured, "credit")
            .ShouldBe("vnext-credit-execution-app");

    [Fact]
    public void OrchestratorOrDefault_Should_Trim_Configured_Value() =>
        VNextAppIds.OrchestratorOrDefault("  spaced-app  ", "credit").ShouldBe("spaced-app");

    /// <summary>
    /// The regression this type was created for: the execution default used to be the bare
    /// <c>"vnext-execution"</c> in code while every appsettings.json and compose file said
    /// <c>vnext-execution-app</c>. The mismatch was invisible because configuration was always
    /// present — it would only have surfaced in the one case a default exists for.
    /// </summary>
    [Fact]
    public void ExecutionOrDefault_Should_Not_Reproduce_The_Stale_Bare_Default() =>
        VNextAppIds.ExecutionOrDefault(configured: null, domain: "credit")
            .ShouldNotBe("vnext-execution");

    /// <summary>
    /// The other half of that regression: <c>"vnext-app"</c> was hardcoded in six invokers plus
    /// the inbox forwarder and is correct ONLY for the <c>core</c> domain.
    /// </summary>
    [Fact]
    public void OrchestratorOrDefault_Should_Be_Domain_Aware()
    {
        VNextAppIds.OrchestratorOrDefault(null, "core").ShouldBe("vnext-core-app");
        VNextAppIds.OrchestratorOrDefault(null, "credit").ShouldNotBe("vnext-app");
    }

    #endregion
}
