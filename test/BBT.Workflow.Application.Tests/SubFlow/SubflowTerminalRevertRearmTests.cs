using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// Pins the B4b fix: a phase-2 resume failure reverts the SubFlow correlation, and — because the
/// lock-free duplicate ACK may already have consumed the original durable delivery before the
/// revert reopens the correlation — the revert re-publishes the terminal event INSIDE the same
/// revert UoW so the outbox row commits atomically with the revert. See
/// <c>SubflowCompletionService.RevertCorrelationInNewUowAsync</c>.
/// </summary>
public sealed class SubflowTerminalRevertRearmTests
{
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IComponentCacheStore> _componentCacheStore = new();
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IRuntimeInfoProvider> _runtimeInfoProvider = new();
    private readonly Mock<IWorkflowExecutionService> _workflowExecutionService = new();
    private readonly Mock<ISubflowOutputMappingService> _outputMappingService = new();
    private readonly Mock<ITransitionLockScopeFactory> _lockScopeFactory = new();
    private readonly Mock<ITransitionLockScope> _lockScope = new();
    private readonly Mock<ISubItemTerminalGuard> _terminalGuard = new();
    private readonly Mock<IDistributedEventBus> _eventBus = new();
    private readonly Mock<ILogger<SubflowCompletionService>> _logger = new();
    private readonly List<string> _order = new();

    public SubflowTerminalRevertRearmTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("commit"))
            .Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _uowManager.Setup(m => m.Begin(It.IsAny<UnitOfWorkOptions>())).Returns(_uow.Object);

        _lockScope.SetupGet(x => x.IsAcquired).Returns(true);
        _lockScope.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_lockScope.Object);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<LockAcquireWait>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_lockScope.Object);

        _terminalGuard
            .Setup(x => x.ProbeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<SubItemTerminalOutcome>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubItemTerminalProbe.Proceed);

        _eventBus
            .Setup(x => x.PublishAsync(
                It.IsAny<InstanceSubCompletedEvent>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("publish"))
            .Returns(Task.CompletedTask);

        _logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public async Task Revert_Republishes_Terminal_Event_In_Same_Uow()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parent.Id, subInstanceId, sync: true);
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        var reloaded = CloneParentWithCompletedCorrelation(parent, subInstanceId);

        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reloaded);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance i, bool _, CancellationToken _) => i);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parent, parentWorkflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Force phase-2 resume to fail (hard failure, not a soft AutoTransitionConditionNotMet /
        // InstanceCompleted) so the catch block in ResumePipelineAsync runs the revert.
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parent.Id)));

        InstanceSubCompletedEvent? captured = null;
        _eventBus
            .Setup(x => x.PublishAsync(
                It.IsAny<InstanceSubCompletedEvent>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<InstanceSubCompletedEvent, string?, bool, CancellationToken>((e, _, _, _) =>
            {
                captured = e;
                _order.Add("publish");
            })
            .Returns(Task.CompletedTask);

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CompletionAsync(input, CancellationToken.None));

        // RevertAndPersistCorrelationAsync ran: the reloaded (tracked) correlation was reopened.
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(x => x.UpdateAsync(reloaded, true, It.IsAny<CancellationToken>()), Times.Once);

        // The republished event is reconstructed from the ORIGINAL input, verbatim.
        captured.ShouldNotBeNull();
        captured!.SubInstanceId.ShouldBe(input.SubInstanceId);
        captured.InstanceId.ShouldBe(input.InstanceId);
        captured.Sync.ShouldBe(input.Sync);
        captured.Domain.ShouldBe(input.Domain);
        captured.Flow.ShouldBe(input.Flow);
        captured.CompletedState.ShouldBe(input.CompletedState);
        captured.RearmAttempt.ShouldBe(1);

        // Ordering: phase-1 commit, then (after resume fails) publish, then the revert UoW commits —
        // the outbox row for the republish must land in the SAME (revert) commit, never after it.
        _order.ShouldBe(["commit", "publish", "commit"]);
    }

    [Fact]
    public async Task Duplicate_Delivery_Still_Acks_On_Completed_Correlation()
    {
        // The IsCompleted short-circuit behavior is UNCHANGED by this fix: a duplicate delivery
        // whose outcome is already persisted still ACKs (returns success) without reverting or
        // republishing anything. The safety net for the stranded-parent bug is the revert-time
        // rearm, not blocking this ACK.
        var parent = CreateParentInstance(out var subInstanceId);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Completed, DateTime.UtcNow);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeTrue();
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _eventBus.Verify(x => x.PublishAsync(
            It.IsAny<InstanceSubCompletedEvent>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _workflowExecutionService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Rearm_Attempts_Are_Capped()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        // The delivery being reverted already carries the maximum rearm attempt budget.
        var input = CreateInput(parent.Id, subInstanceId) with { RearmAttempt = 5 };
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        var reloaded = CloneParentWithCompletedCorrelation(parent, subInstanceId);

        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reloaded);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance i, bool _, CancellationToken _) => i);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parent, parentWorkflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parent.Id)));

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CompletionAsync(input, CancellationToken.None));

        // The revert still happens — the correlation is reopened and persisted...
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(x => x.UpdateAsync(reloaded, true, It.IsAny<CancellationToken>()), Times.Once);

        // ...but NO republish is attempted once the budget is exhausted.
        _eventBus.Verify(x => x.PublishAsync(
            It.IsAny<InstanceSubCompletedEvent>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

        VerifyRearmExhaustedLogged();
    }

    private void VerifyRearmExhaustedLogged()
    {
        _logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((value, _) =>
                value.ToString()!.Contains("re-arm budget exhausted")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private SubflowCompletionService CreateService()
        => new(
            _uowManager.Object,
            _componentCacheStore.Object,
            _instanceRepository.Object,
            _runtimeInfoProvider.Object,
            _workflowExecutionService.Object,
            _outputMappingService.Object,
            _lockScopeFactory.Object,
            _terminalGuard.Object,
            _eventBus.Object,
            Options.Create(new WorkflowExecutionOptions()),
            _logger.Object);

    private static Instance CreateParentInstance(out Guid subInstanceId)
    {
        subInstanceId = Guid.NewGuid();
        var parentInstance = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
        parentInstance.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        parentInstance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "waiting-child",
            subInstanceId,
            SubFlowType.SubFlow.Code,
            "bank",
            "child-flow",
            "1.0.0"));

        return parentInstance;
    }

    private static Instance CloneParentWithCompletedCorrelation(Instance source, Guid subInstanceId)
    {
        var clone = Instance.Create(source.Id, "parent-flow", "1.0.0", "parent-key");
        clone.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        clone.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), clone.Id, "waiting-child", subInstanceId,
            SubFlowType.SubFlow.Code, "bank", "child-flow", "1.0.0"));
        clone.CompleteCorrelation(subInstanceId);
        return clone;
    }

    private static FlowCompletedInput CreateInput(Guid parentInstanceId, Guid subInstanceId, bool sync = false)
        => new()
        {
            InstanceId = parentInstanceId,
            Domain = "bank",
            Flow = "parent-flow",
            Version = "1.0.0",
            SubInstanceId = subInstanceId,
            CompletedState = "child-done",
            InstanceData = CreateJsonElement("""{"result":"ok"}"""),
            CompletedAt = DateTime.UtcNow,
            Sync = sync
        };

    private static JsonElement CreateJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
