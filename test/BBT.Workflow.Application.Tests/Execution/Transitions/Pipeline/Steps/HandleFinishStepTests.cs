using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
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

    private static HandleFinishStep CreateStepWithPolicy(bool emit = true)
    {
        var policy = new CarriedHeaderPolicy(
            Options.Create(new SubFlowCarriedHeadersOptions { Emit = emit }),
            NullLogger<CarriedHeaderPolicy>.Instance);
        return new HandleFinishStep(Substitute.For<IInstanceRepository>(),
            Substitute.For<ILogger<HandleFinishStep>>(), carriedHeaderPolicy: policy);
    }

    private static Dictionary<string, string?> Hdrs() => new()
    {
        ["sub"] = "u1", ["authorization"] = "Bearer x", ["x-custom"] = "v"
    };

    [Fact]
    public async Task Finish_Passes_Captured_Headers_To_Complete()
    {
        var step = CreateStepWithPolicy();
        var context = CreateFinishContext(CreateSubItem(), Hdrs());

        (await step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        var evt = context.Directives.ConsumeDeferredEvents().Select(x => x.Event)
            .OfType<InstanceSubCompletedEvent>().Single();
        evt.CarriedHeaders.ShouldNotBeNull();
        evt.CarriedHeaders!.Keys.OrderBy(k => k).ShouldBe(new[] { "sub", "x-custom" });
    }

    [Fact]
    public async Task Cancel_Transition_Passes_Captured_Headers_To_Cancel()
    {
        var step = CreateStepWithPolicy();
        var context = CreateCancelContext(CreateSubItem(), TerminationContext.Direct(Guid.NewGuid()), Hdrs());

        (await step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        var evt = context.Directives.ConsumeDeferredEvents().Select(x => x.Event)
            .OfType<InstanceSubCanceledEvent>().Single();
        evt.CarriedHeaders!.Keys.OrderBy(k => k).ShouldBe(new[] { "sub", "x-custom" });
    }

    [Fact]
    public async Task Empty_Context_Headers_Yields_Null()
    {
        var step = CreateStepWithPolicy();
        var context = CreateFinishContext(CreateSubItem(), new Dictionary<string, string?>());

        (await step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Directives.ConsumeDeferredEvents().Select(x => x.Event)
            .OfType<InstanceSubCompletedEvent>().Single().CarriedHeaders.ShouldBeNull();
    }

    [Fact]
    public async Task Emit_Off_Yields_Null()
    {
        var step = CreateStepWithPolicy(emit: false);
        var context = CreateFinishContext(CreateSubItem(), Hdrs());

        (await step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        context.Directives.ConsumeDeferredEvents().Select(x => x.Event)
            .OfType<InstanceSubCompletedEvent>().Single().CarriedHeaders.ShouldBeNull();
    }

    [Fact]
    public async Task Root_Instance_Finish_Never_Captures_Headers()
    {
        var policy = Substitute.For<ICarriedHeaderPolicy>();
        var step = new HandleFinishStep(Substitute.For<IInstanceRepository>(),
            Substitute.For<ILogger<HandleFinishStep>>(), carriedHeaderPolicy: policy);
        var context = CreateFinishContext(InstanceFactory.CreateDefault(), Hdrs());

        (await step.ExecuteAsync(context, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        policy.DidNotReceiveWithAnyArgs().Capture(default);
    }

    private static TransitionExecutionContext CreateFinishContext(Instance instance, Dictionary<string, string?>? headers = null)
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
            Headers = headers ?? new Dictionary<string, string?>(),
            Target = StateFactory.CreateDefault("done", StateType.Finish),
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static TransitionExecutionContext CreateCancelContext(
        Instance instance,
        TerminationContext termination,
        Dictionary<string, string?>? headers = null)
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
            Headers = headers ?? new Dictionary<string, string?>(),
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
