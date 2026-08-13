using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
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
