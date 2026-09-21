using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="ApplyTimeoutStateStep"/>, focused on WHICH timeout definition decides
/// the target state.
/// </summary>
/// <remarks>
/// <b>The defect these pin.</b> The step used to read <c>context.Workflow.Timeout</c> — the
/// instance's own definition — while a SubFlow child's deadline may come from the parent's
/// <c>subFlow.overrides.timeout</c>, stamped into the child's ExtraProperties at start and, until
/// now, read by nothing after the arm. A child declaring <c>"timeout": null</c> therefore had a job
/// armed from the override's timer that fired into <c>TimeoutConfigMissing</c> and did nothing:
/// vnext-example's <c>subflow-orchestration</c> pair is in exactly that shape on master. Since the
/// state function publishes that same deadline to clients, the step and the read must resolve it
/// identically — which is why both now call
/// <see cref="InstanceMetadataExtensions.ResolveEffectiveTimeout(Instance, Definitions.Workflow, out bool)"/>.
/// </remarks>
public class ApplyTimeoutStateStepTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";

    private readonly ApplyTimeoutStateStep _step =
        new(Substitute.For<ILogger<ApplyTimeoutStateStep>>());

    [Fact]
    public void Order_ShouldBeApplyTimeoutState()
    {
        _step.Order.ShouldBe(LifecycleOrder.ApplyTimeoutState);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotATimeoutTransition_DoesNothing()
    {
        var context = CreateContext(timeoutOverrideStamp: null);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Target.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_UsesTheWorkflowsOwnTimeoutTarget_WhenThereIsNoOverride()
    {
        var context = CreateContext(timeoutOverrideStamp: null);
        context.Workflow.SetTimeout(
            WorkflowTimeout.Create("own", "state2", VersionStrategy.IncreasePatch.Code, "never", "PT1H"));
        context.Directives.MarkAsTimeoutTransition();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Target.ShouldNotBeNull();
        context.Target!.Key.ShouldBe("state2");
    }

    /// <summary>
    /// The regression this change exists for: a child with NO timeout of its own, armed from the
    /// parent's override, must be pulled to the override's target. Before the fix this returned
    /// <c>Fail(TimeoutConfigMissing)</c> and the instance went nowhere.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UsesTheSubFlowOverrideTarget_WhenTheChildDeclaresNoTimeout()
    {
        var stamp = JsonSerializer.Serialize(
            WorkflowTimeout.Create("child-push-timeout", "state2", "Minor", "OnEntry", "PT15M"),
            JsonSerializerConstants.JsonOptions);
        var context = CreateContext(timeoutOverrideStamp: stamp);
        context.Workflow.Timeout.ShouldBeNull();
        context.Directives.MarkAsTimeoutTransition();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Target.ShouldNotBeNull();
        context.Target!.Key.ShouldBe("state2");
    }

    /// <summary>
    /// The override wins over the child's own timeout, matching the arm — otherwise the instance
    /// would be pulled to a different state than the one it was scheduled (and advertised) for.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PrefersTheOverrideTarget_OverTheChildsOwnTimeout()
    {
        var stamp = JsonSerializer.Serialize(
            WorkflowTimeout.Create("child-push-timeout", "state2", "Minor", "OnEntry", "PT15M"),
            JsonSerializerConstants.JsonOptions);
        var context = CreateContext(timeoutOverrideStamp: stamp);
        context.Workflow.SetTimeout(
            WorkflowTimeout.Create("own", "state1", VersionStrategy.IncreasePatch.Code, "never", "PT1H"));
        context.Directives.MarkAsTimeoutTransition();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Target!.Key.ShouldBe("state2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoTimeoutResolvesAtAll_Fails()
    {
        var context = CreateContext(timeoutOverrideStamp: null);
        context.Directives.MarkAsTimeoutTransition();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        context.Target.ShouldBeNull();
    }

    private static TransitionExecutionContext CreateContext(string? timeoutOverrideStamp)
    {
        var instanceId = Guid.NewGuid();
        var workflow = CreateWorkflow();
        var instance = Instance.Create(instanceId, WorkflowKey, "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);

        if (timeoutOverrideStamp is not null)
        {
            instance.SetMetaData(new ExtraPropertyDictionary
            {
                [DomainConsts.MetaDataKeys.TimeoutOverride] = timeoutOverrideStamp
            });
        }

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = WellKnownTransitionKeys.Timeout,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow()
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           { "key": "state1", "stateType": "Intermediate", "transitions": [] },
                           { "key": "state2", "stateType": "Finish", "transitions": [] }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
