using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Shared;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Tests for <see cref="ReservedTransitionResolver"/>.
/// Validates reserved transition detection for cancel, exit, updateData,
/// timeout, and subflow resume, and lock key isolation via GetOwnLockKey.
/// </summary>
public class ReservedTransitionResolverTests
{
    private readonly ReservedTransitionResolver _resolver = new();

    [Fact]
    public void IsReserved_WithCancelTransition_ShouldReturnTrue()
    {
        var ctx = CreateContext(cancelKey: "cancel", transitionKey: "cancel");
        _resolver.IsReserved(ctx).ShouldBeTrue();
    }

    [Fact]
    public void IsReserved_WithExitTransition_ShouldReturnTrue()
    {
        var ctx = CreateContext(exitKey: "exit", transitionKey: "exit");
        _resolver.IsReserved(ctx).ShouldBeTrue();
    }

    [Fact]
    public void IsReserved_WithUpdateDataTransition_ShouldReturnTrue()
    {
        var ctx = CreateContext(updateDataKey: "updateData", transitionKey: "updateData");
        _resolver.IsReserved(ctx).ShouldBeTrue();
    }

    [Fact]
    public void IsReserved_WithNormalTransition_ShouldReturnFalse()
    {
        var ctx = CreateContext(transitionKey: "approve");
        _resolver.IsReserved(ctx).ShouldBeFalse();
    }

    [Fact]
    public void IsReserved_WithSharedTransition_ShouldReturnTrue()
    {
        var ctx = CreateContext(sharedTransitionKey: "shared-approve", transitionKey: "shared-approve");
        _resolver.IsReserved(ctx).ShouldBeTrue();
    }

    [Fact]
    public void IsReserved_WithTimeoutTransition_ShouldReturnTrue()
    {
        var ctx = CreateContext(transitionKey: "$timeout");
        ctx.Directives.MarkAsTimeoutTransition();
        _resolver.IsReserved(ctx).ShouldBeTrue();
    }

    [Fact]
    public void IsReserved_WithSubFlowResume_ShouldReturnTrue()
    {
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume();
        _resolver.IsReserved(ctx).ShouldBeTrue();
    }

    [Fact]
    public void IsReserved_WithMismatchedKey_ShouldReturnFalse()
    {
        var ctx = CreateContext(cancelKey: "cancel", transitionKey: "not-cancel");
        _resolver.IsReserved(ctx).ShouldBeFalse();
    }

    // GetOwnLockKey — type-label tests

    [Fact]
    public void GetOwnLockKey_WithSubFlowResume_ShouldUseResumeLabel()
    {
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume();
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":resume");
    }

    [Fact]
    public void GetOwnLockKey_WithSubFlowResume_ShouldDifferFromMainFlowLockKey()
    {
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume();
        _resolver.GetOwnLockKey(ctx).ShouldNotBe(ctx.LockKey);
    }

    [Fact]
    public void GetOwnLockKey_WithSubFlowResumeAndSubInstanceId_ShouldIncludeSubInstanceId()
    {
        var subInstanceId = Guid.NewGuid();
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume(subInstanceId);
        _resolver.GetOwnLockKey(ctx).ShouldBe($"{ctx.LockKey}:resume:{subInstanceId:N}");
    }

    [Fact]
    public void GetOwnLockKey_WithSubFlowResumeWithoutSubInstanceId_ShouldFallBackToLegacyResumeKey()
    {
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume();
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":resume");
    }

    [Fact]
    public void GetOwnLockKey_TwoDifferentSubInstances_ShouldProduceDifferentKeys()
    {
        var ctx1 = CreateContext(transitionKey: "resume");
        var ctx2 = CreateContext(transitionKey: "resume");
        ctx1.Directives.MarkAsSubFlowResume(Guid.NewGuid());
        ctx2.Directives.MarkAsSubFlowResume(Guid.NewGuid());
        _resolver.GetOwnLockKey(ctx1).ShouldNotBe(_resolver.GetOwnLockKey(ctx2));
    }

    [Fact]
    public void GetOwnLockKey_WithCancelTransition_ShouldUseCancelLabel()
    {
        var ctx = CreateContext(cancelKey: "cancel", transitionKey: "cancel");
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":cancel");
    }

    [Fact]
    public void GetOwnLockKey_WithExitTransition_ShouldUseExitLabel()
    {
        var ctx = CreateContext(exitKey: "exit", transitionKey: "exit");
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":exit");
    }

    [Fact]
    public void GetOwnLockKey_WithUpdateDataTransition_ShouldUseUpdateDataLabel()
    {
        var ctx = CreateContext(updateDataKey: "updateData", transitionKey: "updateData");
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":updatedata");
    }

    [Fact]
    public void GetOwnLockKey_WithTimeoutTransition_ShouldUseTimeoutLabel()
    {
        var ctx = CreateContext(transitionKey: "$timeout");
        ctx.Directives.MarkAsTimeoutTransition();
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":timeout");
    }

    [Fact]
    public void GetOwnLockKey_WithSharedTransition_ShouldUseSharedLabel()
    {
        var ctx = CreateContext(sharedTransitionKey: "shared-approve", transitionKey: "shared-approve");
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":shared");
    }

    [Fact]
    public void GetOwnLockKey_WithSharedTransition_ShouldDifferFromMainFlowLockKey()
    {
        var ctx = CreateContext(sharedTransitionKey: "shared-approve", transitionKey: "shared-approve");
        _resolver.GetOwnLockKey(ctx).ShouldNotBe(ctx.LockKey);
    }

    [Fact]
    public void GetOwnLockKey_AllReservedTypes_ShouldDifferFromMainFlowLockKey()
    {
        var ctxCancel = CreateContext(cancelKey: "cancel", transitionKey: "cancel");
        var ctxExit = CreateContext(exitKey: "exit", transitionKey: "exit");
        var ctxUpdate = CreateContext(updateDataKey: "updateData", transitionKey: "updateData");
        var ctxTimeout = CreateContext(transitionKey: "$timeout");
        ctxTimeout.Directives.MarkAsTimeoutTransition();

        _resolver.GetOwnLockKey(ctxCancel).ShouldNotBe(ctxCancel.LockKey);
        _resolver.GetOwnLockKey(ctxExit).ShouldNotBe(ctxExit.LockKey);
        _resolver.GetOwnLockKey(ctxUpdate).ShouldNotBe(ctxUpdate.LockKey);
        _resolver.GetOwnLockKey(ctxTimeout).ShouldNotBe(ctxTimeout.LockKey);
    }

    private static TransitionExecutionContext CreateContext(
        string transitionKey = "test",
        string? cancelKey = null,
        string? exitKey = null,
        string? updateDataKey = null,
        string? sharedTransitionKey = null)
    {
        var sharedTransitionsJson = sharedTransitionKey is not null
            ? $"{{\"key\": \"{sharedTransitionKey}\", \"from\": null, \"target\": \"state1\", \"triggerType\": \"Manual\", \"versionStrategy\": \"Patch\", \"labels\": [], \"onExecutionTasks\": [], \"view\": null}}"
            : string.Empty;

        var json = $$"""
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            {{(cancelKey is not null ? $"\"cancel\": {{\"key\": \"{cancelKey}\", \"from\": null, \"target\": \"cancelled\", \"triggerType\": \"Manual\", \"versionStrategy\": \"Patch\", \"labels\": [], \"onExecutionTasks\": [], \"view\": null}}," : "")}}
            {{(exitKey is not null ? $"\"exit\": {{\"key\": \"{exitKey}\", \"from\": null, \"target\": \"exited\", \"triggerType\": \"Manual\", \"versionStrategy\": \"Patch\", \"labels\": [], \"onExecutionTasks\": [], \"view\": null}}," : "")}}
            {{(updateDataKey is not null ? $"\"updateData\": {{\"key\": \"{updateDataKey}\", \"from\": null, \"target\": \"state1\", \"triggerType\": \"Manual\", \"versionStrategy\": \"Patch\", \"labels\": [], \"onExecutionTasks\": [], \"view\": null}}," : "")}}
            "states": [
                {"key": "state1", "type": "P", "transitions": []},
                {"key": "cancelled", "type": "Q", "transitions": []},
                {"key": "exited", "type": "Q", "transitions": []}
            ],
            "sharedTransitions": [{{sharedTransitionsJson}}],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));

        var transition = Transition.Create(transitionKey, null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = Guid.NewGuid(),
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Transition = transition,
            Instance = Instances.Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0"),
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
