using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
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


    /// <summary>
    /// The lock is handed back on the ambient unit of work's OnCompleted, not inline. Releasing before
    /// the commit would announce "the section is free" for a transition that can still roll back, and
    /// a second instance could walk straight into it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Finish_ReleasesTrackedLocksOnlyAfterTheCommit()
    {
        var repository = Substitute.For<IInstanceRepository>();
        var locks = Substitute.For<IResourceLockService>();
        var (uowManager, completeAsync) = CreateUowManagerCapturingOnCompleted();
        var instance = InstanceFactory.CreateDefault();
        instance.TrackResourceLock("txnlimit:a");
        instance.TrackResourceLock("txnlimit:b");
        var step = new HandleFinishStep(
            repository, Substitute.For<ILogger<HandleFinishStep>>(),
            resourceLockService: locks, uowManager: uowManager);

        var result = await step.ExecuteAsync(CreateFinishContext(instance), CancellationToken.None);

        // The step itself must not have freed anything yet — the transaction is still open.
        result.IsSuccess.ShouldBeTrue();
        await locks.DidNotReceive().ReleaseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await completeAsync();

        var owner = instance.Id.ToString();
        await locks.Received(1).ReleaseAsync("txnlimit:a", owner, Arg.Any<CancellationToken>());
        await locks.Received(1).ReleaseAsync("txnlimit:b", owner, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Cancel is a terminal path too, so it hands the locks back on the same hook.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Cancel_ReleasesTrackedLocksAfterTheCommit()
    {
        var repository = Substitute.For<IInstanceRepository>();
        var locks = Substitute.For<IResourceLockService>();
        var (uowManager, completeAsync) = CreateUowManagerCapturingOnCompleted();
        var instance = CreateSubItem();
        instance.TrackResourceLock("txnlimit:c");
        var step = new HandleFinishStep(
            repository, Substitute.For<ILogger<HandleFinishStep>>(),
            resourceLockService: locks, uowManager: uowManager);

        await step.ExecuteAsync(
            CreateCancelContext(instance, TerminationContext.Direct(Guid.NewGuid())), CancellationToken.None);
        await completeAsync();

        await locks.Received(1).ReleaseAsync(
            "txnlimit:c", instance.Id.ToString(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A release that throws must never take down a transition that already committed — the TTL is the
    /// safety net, and the remaining keys are still attempted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Finish_WhenAReleaseThrows_StillReleasesTheRest()
    {
        var repository = Substitute.For<IInstanceRepository>();
        var locks = Substitute.For<IResourceLockService>();
        locks.ReleaseAsync("txnlimit:a", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("lock store down"));
        var (uowManager, completeAsync) = CreateUowManagerCapturingOnCompleted();
        var instance = InstanceFactory.CreateDefault();
        instance.TrackResourceLock("txnlimit:a");
        instance.TrackResourceLock("txnlimit:b");
        var step = new HandleFinishStep(
            repository, Substitute.For<ILogger<HandleFinishStep>>(),
            resourceLockService: locks, uowManager: uowManager);

        await step.ExecuteAsync(CreateFinishContext(instance), CancellationToken.None);
        await Should.NotThrowAsync(completeAsync);

        await locks.Received(1).ReleaseAsync("txnlimit:b", instance.Id.ToString(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An instance that never took a lock must not arm anything — no hook, no call.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Finish_WithNoTrackedLocks_ArmsNothing()
    {
        var repository = Substitute.For<IInstanceRepository>();
        var locks = Substitute.For<IResourceLockService>();
        var uow = Substitute.For<IUnitOfWork>();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Current.Returns(uow);
        var step = new HandleFinishStep(
            repository, Substitute.For<ILogger<HandleFinishStep>>(),
            resourceLockService: locks, uowManager: uowManager);

        await step.ExecuteAsync(CreateFinishContext(InstanceFactory.CreateDefault()), CancellationToken.None);

        uow.DidNotReceive().OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>());
        await locks.DidNotReceive().ReleaseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A unit-of-work manager whose Current hands back a UoW that captures its OnCompleted callback,
    /// plus the func that runs it — standing in for the commit.
    /// </summary>
    private static (IUnitOfWorkManager Manager, Func<Task> CompleteAsync) CreateUowManagerCapturingOnCompleted()
    {
        var uow = Substitute.For<IUnitOfWork>();
        Func<IUnitOfWork, Task>? captured = null;
        uow.When(u => u.OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>()))
            .Do(call => captured = call.ArgAt<Func<IUnitOfWork, Task>>(0));

        var manager = Substitute.For<IUnitOfWorkManager>();
        manager.Current.Returns(uow);

        return (manager, async () =>
        {
            captured.ShouldNotBeNull("the step never armed an OnCompleted callback");
            await captured!(uow);
        });
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
