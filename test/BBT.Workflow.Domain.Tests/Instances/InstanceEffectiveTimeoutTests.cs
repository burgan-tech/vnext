using System;
using System.Text.Json;
using BBT.Aether;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins <see cref="InstanceMetadataExtensions.ResolveEffectiveTimeout(ExtraPropertyDictionary?, Definitions.Workflow, out bool)"/> —
/// the single answer to "which timeout is actually in force for this instance", shared by the arm
/// (<c>InstanceCommandAppService</c>), the fire path (<c>FlowTimeoutJobHandler</c>,
/// <c>ApplyTimeoutStateStep</c>) and the state function's <c>timeout</c> block.
/// </summary>
/// <remarks>
/// <b>Why it is one function.</b> The parent-supplied SubFlow override used to be read by the arm
/// alone: a child whose own definition carried no <c>timeout</c> had a job armed from the override's
/// timer, and then the fire path — which read <c>workflow.Timeout</c> — bailed with
/// <c>TimeoutConfigMissing</c> and did nothing. The deadline existed, was scheduled, and was
/// unreachable. These tests exist so the three surfaces can never drift apart again.
/// </remarks>
public class InstanceEffectiveTimeoutTests : DomainTestBase<DomainEntryPoint>
{
    private static Definitions.Workflow WorkflowWithTimeout(WorkflowTimeout? timeout)
    {
        var workflow = Definitions.Workflow.Create();
        if (timeout is not null)
            workflow.SetTimeout(timeout);
        return workflow;
    }

    private static ExtraPropertyDictionary Stamped(string json) =>
        new() { [DomainConsts.MetaDataKeys.TimeoutOverride] = json };

    [Fact]
    public void NoOverride_FallsBackToTheWorkflowsOwnTimeout()
    {
        var workflow = WorkflowWithTimeout(WorkflowTimeout.Create("own", "finish", "Patch", "never", "PT1H"));

        var resolved = new ExtraPropertyDictionary().ResolveEffectiveTimeout(workflow, out var malformed);

        malformed.ShouldBeFalse();
        resolved.ShouldNotBeNull();
        resolved!.Key.ShouldBe("own");
        resolved.Target.ShouldBe("finish");
    }

    [Fact]
    public void NoOverrideAndNoWorkflowTimeout_ResolvesToNull()
    {
        var resolved = new ExtraPropertyDictionary()
            .ResolveEffectiveTimeout(WorkflowWithTimeout(null), out var malformed);

        malformed.ShouldBeFalse();
        resolved.ShouldBeNull();
    }

    [Fact]
    public void Override_WinsOverTheWorkflowsOwnTimeout()
    {
        var workflow = WorkflowWithTimeout(WorkflowTimeout.Create("own", "own-finish", "Patch", "never", "PT1H"));
        var stamp = JsonSerializer.Serialize(
            WorkflowTimeout.Create("child-push-timeout", "child-cancelled", "Minor", "OnEntry", "PT15M"),
            JsonSerializerConstants.JsonOptions);

        var resolved = Stamped(stamp).ResolveEffectiveTimeout(workflow, out var malformed);

        malformed.ShouldBeFalse();
        resolved!.Key.ShouldBe("child-push-timeout");
        resolved.Target.ShouldBe("child-cancelled");
        resolved.Timer.Duration.ShouldBe("PT15M");
    }

    /// <summary>
    /// The case the whole change exists for: vnext-example's <c>subflow-orchestration-child</c> has
    /// <c>"timeout": null</c> and its parent supplies the override. Reading the child's own
    /// definition answers "no timeout" for an instance that demonstrably has one armed.
    /// </summary>
    [Fact]
    public void Override_ResolvesEvenWhenTheChildDeclaresNoTimeoutOfItsOwn()
    {
        var stamp = JsonSerializer.Serialize(
            WorkflowTimeout.Create("child-push-timeout", "child-cancelled", "Minor", "OnEntry", "PT15M"),
            JsonSerializerConstants.JsonOptions);

        var resolved = Stamped(stamp).ResolveEffectiveTimeout(WorkflowWithTimeout(null), out var malformed);

        malformed.ShouldBeFalse();
        resolved.ShouldNotBeNull();
        resolved!.Target.ShouldBe("child-cancelled");
    }

    /// <summary>
    /// Stamps written by runtimes before the writer moved to the shared options are PascalCase with
    /// default converters. They must keep resolving, or an upgrade would silently drop the override
    /// for every in-flight child.
    /// </summary>
    [Fact]
    public void Override_WrittenWithDefaultSerializerOptions_StillResolves()
    {
        var legacyStamp = JsonSerializer.Serialize(
            WorkflowTimeout.Create("legacy", "legacy-cancelled", "Minor", "never", "PT5M"));

        var resolved = Stamped(legacyStamp).ResolveEffectiveTimeout(WorkflowWithTimeout(null), out var malformed);

        malformed.ShouldBeFalse();
        resolved.ShouldNotBeNull();
        resolved!.Key.ShouldBe("legacy");
        resolved.Target.ShouldBe("legacy-cancelled");
    }

    [Theory]
    [InlineData("{ not json")]                                  // broken document -> JsonException
    [InlineData("{\"key\":\"\",\"target\":\"x\",\"versionStrategy\":\"Minor\",\"timer\":{\"reset\":\"never\",\"duration\":\"PT5M\"}}")] // Check guard -> ArgumentException
    public void MalformedOverride_FallsBackAndReportsItInsteadOfThrowing(string stamp)
    {
        var workflow = WorkflowWithTimeout(WorkflowTimeout.Create("own", "finish", "Patch", "never", "PT1H"));

        var resolved = Stamped(stamp).ResolveEffectiveTimeout(workflow, out var malformed);

        malformed.ShouldBeTrue();
        resolved!.Key.ShouldBe("own");
    }

    /// <summary>
    /// A malformed stamp on an instance whose workflow has no timeout of its own resolves to null —
    /// no timeout — rather than throwing. Both callers treat a timeout as a backstop that must never
    /// fail the operation it rides on: the arm lets the start proceed, the state read answers 200.
    /// </summary>
    [Fact]
    public void MalformedOverride_WithNoWorkflowTimeout_ResolvesToNull()
    {
        var resolved = Stamped("{ not json").ResolveEffectiveTimeout(WorkflowWithTimeout(null), out var malformed);

        malformed.ShouldBeTrue();
        resolved.ShouldBeNull();
    }

    [Fact]
    public void EmptyStamp_IsTreatedAsNoOverride()
    {
        var workflow = WorkflowWithTimeout(WorkflowTimeout.Create("own", "finish", "Patch", "never", "PT1H"));

        var resolved = Stamped("   ").ResolveEffectiveTimeout(workflow, out var malformed);

        malformed.ShouldBeFalse();
        resolved!.Key.ShouldBe("own");
    }

    [Fact]
    public void InstanceOverload_ReadsTheInstancesOwnExtraProperties()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        var stamp = JsonSerializer.Serialize(
            WorkflowTimeout.Create("from-parent", "cancelled", "Minor", "never", "PT20S"),
            JsonSerializerConstants.JsonOptions);
        instance.SetMetaData(Stamped(stamp));

        var resolved = instance.ResolveEffectiveTimeout(WorkflowWithTimeout(null));

        resolved!.Key.ShouldBe("from-parent");
        resolved.Target.ShouldBe("cancelled");
    }
}
