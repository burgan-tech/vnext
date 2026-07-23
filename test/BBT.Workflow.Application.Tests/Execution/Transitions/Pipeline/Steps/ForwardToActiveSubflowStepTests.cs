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

    private static TransitionExecutionContext CreateContextWithActiveSubFlow()
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
            TransitionKey = "child-transition",
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = Definitions.Workflow.Create(),
            Current = StateFactory.CreateDefault("waiting-child", StateType.SubFlow),
            Transition = Transition.Create("child-transition", "waiting-child", "waiting-child", TriggerType.Manual, "Patch"),
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
