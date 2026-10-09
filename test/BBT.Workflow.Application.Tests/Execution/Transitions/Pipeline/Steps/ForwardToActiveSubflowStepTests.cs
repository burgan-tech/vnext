using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

public class ForwardToActiveSubflowStepTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldHandOffContinuationToActiveSubFlow()
    {
        var step = new ForwardToActiveSubflowStep();
        var context = CreateContextWithActiveSubFlow();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var job = context.Directives.ConsumePostCommitJobs().Single().ShouldBeOfType<ForwardToSubflowJob>();
        job.ContinuationBehavior.ShouldBe(PostCommitContinuationBehavior.HandoffToChild);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateDataTransition_ShouldNotForwardToSubflow()
    {
        // updateData executes on the instance it targets: the parent's data updates and its
        // own auto transitions may advance — it is never forwarded to the active subflow.
        var step = new ForwardToActiveSubflowStep();
        var context = CreateContextWithActiveSubFlow(transitionKey: "update-parent-data");

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();     // continues down the normal pipeline
        context.Directives.PostCommitJobs.ShouldBeEmpty(); // no ForwardToSubflowJob
    }

    [Fact]
    public async Task ExecuteAsync_WhenAnOlderAcceptReservedTheChain_ShouldStampTheClaimOnTheJob()
    {
        // Legacy, kept for the rollout (deprecation subflow-chain-reserve-claim): a parent job an
        // OLDER runtime's accept enqueued with the chain reserve, or an older parent's relay passing
        // through this level. That accept flipped the leaf Busy; without the claim the relay would be
        // rejected by that same leaf with a 409 (and E31 would release the reserve, losing the request).
        var step = new ForwardToActiveSubflowStep();
        var context = CreateContextWithActiveSubFlow();
        context.SubflowChainReserved = true;

        await step.ExecuteAsync(context, CancellationToken.None);

        var job = context.Directives.ConsumePostCommitJobs().Single().ShouldBeOfType<ForwardToSubflowJob>();
        job.ChainReserved.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAcceptDidNotReserveTheChain_ShouldNotStampTheClaim()
    {
        // Every request this runtime admits: its accept no longer reserves the chain, so the forward
        // reaches the leaf as a normal request. Claiming a reserve that was never taken would let the
        // relay barge past a leaf that is Busy for its own reasons.
        var step = new ForwardToActiveSubflowStep();
        var context = CreateContextWithActiveSubFlow();

        await step.ExecuteAsync(context, CancellationToken.None);

        var job = context.Directives.ConsumePostCommitJobs().Single().ShouldBeOfType<ForwardToSubflowJob>();
        job.ChainReserved.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ParentSharedTransitionAvailableInCurrentState_RunsOnTheParent()
    {
        var step = new ForwardToActiveSubflowStep();
        var context = CreateContextWithActiveSubFlow(transitionKey: "escalate");
        var shared = Transition.Create("escalate", null, "$self", TriggerType.Manual, "Patch");
        shared.AddAvailableIn("waiting-child");
        context.Workflow.AddSharedTransition(shared);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Value!.SkipToOrder.ShouldBeNull();
        context.Directives.PostCommitJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ParentSharedTransitionNotAvailableInCurrentState_IsForwarded()
    {
        // Same predicate as the intake proxy: a shared transition the parent's current state does not
        // offer is forwarded like any other key — it used to run on the parent with every policy
        // validation bypassed (SubFlowBypassSpecification).
        var step = new ForwardToActiveSubflowStep();
        var context = CreateContextWithActiveSubFlow(transitionKey: "escalate");
        var shared = Transition.Create("escalate", null, "$self", TriggerType.Manual, "Patch");
        shared.AddAvailableIn("some-other-state");
        context.Workflow.AddSharedTransition(shared);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Value!.SkipToOrder.ShouldBe(LifecycleOrder.Finalize);
        context.Directives.ConsumePostCommitJobs().Single().ShouldBeOfType<ForwardToSubflowJob>()
            .TransitionKey.ShouldBe("escalate");
    }

    private static TransitionExecutionContext CreateContextWithActiveSubFlow(
        string transitionKey = "child-transition")
    {
        var instance = Instance.Create(Guid.NewGuid(), "parent-workflow", "1.0.0");
        instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "waiting-child",
            Guid.NewGuid(),
            SubFlowType.SubFlow.Code,
            "child-domain",
            "child-workflow",
            "1.0.0"));

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "parent-domain",
            WorkflowKey = instance.Flow,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = Definitions.Workflow.Create(),
            Current = StateFactory.CreateDefault("waiting-child", StateType.SubFlow),
            Transition = Transition.Create(transitionKey, "waiting-child", "waiting-child", TriggerType.Manual, "Patch"),
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
