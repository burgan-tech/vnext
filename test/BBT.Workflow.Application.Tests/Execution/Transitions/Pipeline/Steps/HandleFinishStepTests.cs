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
