using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

public class HandleSubFlowStepTests
{
    [Theory]
    [InlineData("S", PostCommitContinuationBehavior.HandoffToChild)]
    [InlineData("P", PostCommitContinuationBehavior.ContinueParent)]
    public async Task ExecuteAsync_ShouldEncodeConfiguredSubFlowContinuationBehavior(
        string subFlowType,
        PostCommitContinuationBehavior expectedBehavior)
    {
        var repository = Substitute.For<IInstanceRepository>();
        var guidGenerator = Substitute.For<IGuidGenerator>();
        guidGenerator.Create().Returns(Guid.NewGuid(), Guid.NewGuid());
        var step = new HandleSubFlowStep(
            repository,
            guidGenerator,
            Substitute.For<ILogger<HandleSubFlowStep>>());
        var context = CreateContext(subFlowType);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var job = context.Directives.ConsumePostCommitJobs().Single().ShouldBeOfType<StartSubflowJob>();
        job.ContinuationBehavior.ShouldBe(expectedBehavior);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateDataTransition_ShouldNeverStartSubflowAndContinue()
    {
        // updateData's $self transition through a SubFlow state must leave the subflow
        // machinery untouched: no StartSubflowJob (even when no active correlation exists —
        // the completed-correlation window), and the pipeline CONTINUES so the parent's own
        // auto transitions (order 90) can advance with the fresh data.
        var repository = Substitute.For<IInstanceRepository>();
        var step = new HandleSubFlowStep(
            repository,
            Substitute.For<IGuidGenerator>(),
            Substitute.For<ILogger<HandleSubFlowStep>>());
        var context = CreateContext("S", transitionKey: "update-parent-data");

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();          // continues to Auto
        context.Directives.PostCommitJobs.ShouldBeEmpty(); // no StartSubflowJob
    }

    private static TransitionExecutionContext CreateContext(
        string subFlowType, string transitionKey = "enter-child")
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        var target = StateFactory.CreateDefault("child", StateType.SubFlow);
        target.SetSubFlow(
            subFlowType,
            new Reference("child-workflow", "child-domain", "sys-flows", "1.0.0"),
            ScriptCode.FromNative("{}"),
            viewOverrides: null);

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
            Current = StateFactory.CreateDefault("current"),
            Target = target,
            Transition = Transition.Create(transitionKey, "current", "child", TriggerType.Manual, "Patch"),
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
