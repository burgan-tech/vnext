using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

public class HandleFinishStepTests
{
    [Fact]
    public async Task ExecuteAsync_Cancel_ShouldPassTerminationAndCallerModeToInstance()
    {
        var repository = Substitute.For<IInstanceRepository>();
        var step = new HandleFinishStep(repository, Substitute.For<ILogger<HandleFinishStep>>());
        var instance = CreateSubItem();
        var termination = TerminationContext.Direct(Guid.NewGuid());
        var context = CreateCancelContext(instance, termination);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var message = context.Directives.ConsumeDeferredEvents()
            .Select(x => x.Event).OfType<InstanceSubCanceledEvent>().Single();
        message.Sync.ShouldBeTrue();
        message.TerminationOrigin.ShouldBe(termination.Origin);
        message.InitiatorInstanceId.ShouldBe(termination.InitiatorInstanceId);
        message.CascadeId.ShouldBe(termination.CascadeId);
        await repository.Received(1).UpdateAsync(instance, true, CancellationToken.None);
    }

    /// <summary>
    /// history: none (vnext#1006): the buffered data is written once, and the completion is saved
    /// INSIDE that write (same transaction) — a Completed instance never commits without its data.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_HistoryNone_CompletesInsideTheBufferFlush()
    {
        var repository = Substitute.For<IInstanceRepository>();
        var writer = Substitute.For<IInstanceDataWriteService>();
        var completedInsideFlush = false;
        writer.FlushAsync(Arg.Any<Instance>(), Arg.Any<Definitions.Workflow?>(), Arg.Any<Func<CancellationToken, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                callInfo.ArgAt<Instance>(0).Status.ShouldNotBe(InstanceStatus.Completed);
                await callInfo.ArgAt<Func<CancellationToken, Task>?>(2)!(CancellationToken.None);
                completedInsideFlush = callInfo.ArgAt<Instance>(0).Status.Equals(InstanceStatus.Completed);
                return (InstanceData?)null;
            });
        var step = new HandleFinishStep(repository, Substitute.For<ILogger<HandleFinishStep>>(),
            instanceDataWriteService: writer);
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        var context = CreateFinishContext(instance);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        completedInsideFlush.ShouldBeTrue();
        await repository.Received(1).UpdateAsync(instance, true, CancellationToken.None);
        context.Items["IsFinishState"].ShouldBe(true);
    }

    private static TransitionExecutionContext CreateFinishContext(Instance instance)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetHistory(HistoryMode.None);
        var transition = Transition.Create("go", "work", "done", TriggerType.Automatic, "Patch");
        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "domain",
            WorkflowKey = instance.Flow,
            TransitionKey = transition.Key,
            Trigger = TriggerType.Automatic,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = StateFactory.CreateDefault("work"),
            Target = StateFactory.CreateDefault("done", StateType.Finish),
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static TransitionExecutionContext CreateCancelContext(
        Instance instance,
        TerminationContext termination)
    {
        var workflow = Definitions.Workflow.Create();
        var transition = Transition.Create(
            WellKnownTransitionKeys.Cancel, "state", "state", TriggerType.Manual, "Patch");
        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "child-domain",
            WorkflowKey = instance.Flow,
            TransitionKey = transition.Key,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = StateFactory.CreateDefault("state"),
            Transition = transition,
            Instance = instance,
            CallerMode = ExecMode.Sync,
            Termination = termination,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Instance CreateSubItem()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Id] = Guid.NewGuid().ToString();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = "parent-domain";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "parent-flow";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Version] = "1.0.0";
        return instance;
    }
}
