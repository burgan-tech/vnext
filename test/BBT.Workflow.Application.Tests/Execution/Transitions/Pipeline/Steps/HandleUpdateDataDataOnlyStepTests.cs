using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="HandleUpdateDataDataOnlyStep"/>: an updateData request against a
/// parent that owns an open SubFlow correlation writes its data and stops there; every other
/// case (no correlation, SubProcess correlation, completed correlation, non-updateData
/// transitions) runs the full pipeline.
/// </summary>
public class HandleUpdateDataDataOnlyStepTests
{
    private readonly HandleUpdateDataDataOnlyStep _step = new();

    [Fact]
    public async Task ExecuteAsync_UpdateDataWithActiveSubFlow_ShouldSkipToFinalize()
    {
        var context = CreateContext("update-parent-data", SubFlowType.SubFlow.Code, completed: false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBe(LifecycleOrder.Finalize);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateDataWithSubProcessCorrelation_ShouldContinue()
    {
        // Fan-in parents (SubProcess children reporting back) must keep running their own
        // pipeline so the state's auto transitions see the fresh data.
        var context = CreateContext("update-parent-data", SubFlowType.SubProcess.Code, completed: false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_UpdateDataWithCompletedSubFlowCorrelation_ShouldContinue()
    {
        // Completion window: the subflow is done, so the parent may advance its own autos.
        // HandleSubFlowStep's updateData exemption keeps the subflow from being restarted.
        var context = CreateContext("update-parent-data", SubFlowType.SubFlow.Code, completed: true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_UpdateDataWithoutCorrelation_ShouldContinue()
    {
        var context = CreateContext("update-parent-data", subFlowType: null, completed: false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RegularTransitionWithActiveSubFlow_ShouldContinue()
    {
        // The short-circuit is updateData-specific; other transitions keep their own handling
        // (ForwardToActiveSubflowStep already relayed them at order 10).
        var context = CreateContext("regular-transition", SubFlowType.SubFlow.Code, completed: false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();
    }

    private static TransitionExecutionContext CreateContext(
        string transitionKey, string? subFlowType, bool completed)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        var state = StateFactory.CreateDefault("current");

        if (subFlowType is not null)
        {
            var correlation = InstanceCorrelation.Create(
                Guid.NewGuid(), instance.Id, state.Key, Guid.NewGuid(),
                subFlowType, "child-domain", "child-workflow", "1.0.0");
            if (completed)
                correlation.Completed();

            instance.AddCorrelation(correlation);
        }

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = instance.Flow,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = Definitions.Workflow.Create(),
            Current = state,
            Target = state,
            Transition = Transition.Create(transitionKey, state.Key, state.Key, TriggerType.Manual, "Patch"),
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
