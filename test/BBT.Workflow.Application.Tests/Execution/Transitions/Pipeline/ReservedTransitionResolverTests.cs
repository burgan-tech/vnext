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
/// timeout, and subflow resume, and <see cref="IReservedTransitionResolver.RequiresOwnLock"/>.
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
    public void RequiresOwnLock_WithSubFlowResume_ShouldReturnTrue()
    {
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume();
        _resolver.RequiresOwnLock(ctx).ShouldBeTrue();
    }

    [Fact]
    public void RequiresOwnLock_WithTimeoutTransition_ShouldReturnFalse()
    {
        var ctx = CreateContext(transitionKey: "$timeout");
        ctx.Directives.MarkAsTimeoutTransition();
        _resolver.RequiresOwnLock(ctx).ShouldBeFalse();
    }

    [Fact]
    public void RequiresOwnLock_WithCancelTransition_ShouldReturnFalse()
    {
        var ctx = CreateContext(cancelKey: "cancel", transitionKey: "cancel");
        _resolver.RequiresOwnLock(ctx).ShouldBeFalse();
    }

    [Fact]
    public void RequiresOwnLock_WithNormalTransition_ShouldReturnFalse()
    {
        var ctx = CreateContext(transitionKey: "approve");
        _resolver.RequiresOwnLock(ctx).ShouldBeFalse();
    }

    [Fact]
    public void IsReserved_WithMismatchedKey_ShouldReturnFalse()
    {
        var ctx = CreateContext(cancelKey: "cancel", transitionKey: "not-cancel");
        _resolver.IsReserved(ctx).ShouldBeFalse();
    }

    private static TransitionExecutionContext CreateContext(
        string transitionKey = "test",
        string? cancelKey = null,
        string? exitKey = null,
        string? updateDataKey = null)
    {
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
            "sharedTransitions": [],
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
