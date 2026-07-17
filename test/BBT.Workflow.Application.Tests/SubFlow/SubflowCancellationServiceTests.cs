using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class SubflowCancellationServiceTests
{
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IComponentCacheStore> _componentCacheStore = new();
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IWorkflowExecutionService> _workflowExecution = new();
    private readonly Mock<ITransitionLockScopeFactory> _lockScopeFactory = new();
    private readonly Mock<ITransitionLockScope> _lockScope = new();
    private readonly Mock<ILogger<SubflowCancellationService>> _logger = new();

    public SubflowCancellationServiceTests()
    {
        _uow.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(x => x.DisposeAsync())
            .Returns(ValueTask.CompletedTask);
        _uowManager.Setup(x => x.Begin(It.IsAny<UnitOfWorkOptions>()))
            .Returns(_uow.Object);
        _lockScope.SetupGet(x => x.IsAcquired).Returns(true);
        _lockScope.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_lockScope.Object);
        _logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public async Task CancellationAsync_WhenParentLockCannotBeAcquired_ShouldFailBeforeRepositoryAccess()
    {
        var input = CreateCanceledInput(Guid.NewGuid(), Guid.NewGuid());
        _lockScope.SetupGet(x => x.IsAcquired).Returns(false);

        var exception = await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CancellationAsync(input));

        exception.Message.ShouldContain(WorkflowErrorCodes.ConflictWorkflow);
        _lockScopeFactory.Verify(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}",
            It.IsAny<CancellationToken>()), Times.Once);
        _instanceRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancellationAsync_BlockingSubFlow_ShouldCommitThenResumeWithoutMappingOrIncident()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
        var input = CreateCanceledInput(parent.Id, subInstanceId);
        var committed = false;
        SetupBlockingParent(parent);
        _uow.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => committed = true)
            .Returns(Task.CompletedTask);
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => committed.ShouldBeTrue())
            .ReturnsAsync(Success(parent));

        await CreateService().CancellationAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(input.SubInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
        correlation.CompletedAt.ShouldBe(input.CanceledAt);
        correlation.SubFlowCurrentState.ShouldBe(input.CanceledState);
        correlation.SubFlowStateChangedAt.ShouldBe(input.CanceledAt);
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _workflowExecution.Verify(x => x.ExecuteTransitionAsync(
            It.Is<WorkflowExecutionContext>(c =>
                c.TriggerType == TriggerType.Automatic &&
                c.Mode == ExecMode.Resume &&
                c.CallerMode == ExecMode.Async &&
                c.Execution!.ResumeFrom == LifecycleOrder.ClearBusyOnResumeStep &&
                c.Execution.IsSubFlowResume &&
                c.Execution.SubFlowResumeInstanceId == input.SubInstanceId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellationAsync_SubProcess_ShouldOnlyCloseCorrelation()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        var input = CreateCanceledInput(parent.Id, subInstanceId);
        SetupParent(parent);

        await CreateService().CancellationAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.IsCompleted.ShouldBeTrue();
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
        correlation.CompletedAt.ShouldBe(input.CanceledAt);
        correlation.SubFlowCurrentState.ShouldBe(input.CanceledState);
        correlation.SubFlowStateChangedAt.ShouldBe(input.CanceledAt);
        _instanceRepository.Verify(x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _componentCacheStore.VerifyNoOtherCalls();
        _workflowExecution.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancellationAsync_WhenPersistedCanceledOutcomeAlreadyRecorded_ShouldLoadAllAndNoOp()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var originalCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Canceled, originalCompletedAt);
        var input = CreateCanceledInput(parent.Id, subInstanceId);
        SetupParent(parent);

        await CreateService().CancellationAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        correlation.SubFlowCurrentState.ShouldBeNull();
        VerifyCommonPhaseLoadedAllCorrelations(parent.Id);
        VerifyNoMutationOrResume();
    }

    [Fact]
    public async Task CancellationAsync_WhenPersistedDifferentOutcomeAlreadyRecorded_ShouldLoadAllAndNotOverwrite()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        Activity? terminalActivity = null;
        using var activityListener = CaptureTerminalActivity(parent.Id, activity => terminalActivity = activity);
        var originalCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Faulted, originalCompletedAt);
        var input = CreateCanceledInput(parent.Id, subInstanceId);
        SetupParent(parent);

        await CreateService().CancellationAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        VerifyCommonPhaseLoadedAllCorrelations(parent.Id);
        VerifyNoMutationOrResume();
        VerifyTerminalConflictLogged("Faulted");
        GetTelemetryScope()[TelemetryConstants.TagNames.SubItemType]
            .ShouldBe(SubFlowType.SubFlow.Code);
        terminalActivity!.GetTagItem(TelemetryConstants.TagNames.SubItemType)
            .ShouldBe(SubFlowType.SubFlow.Code);
    }

    [Fact]
    public async Task CancellationAsync_WhenPersistedLegacyOutcomeIsMissing_ShouldLoadAllAndNotOverwrite()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        parent.CompleteCorrelation(subInstanceId);
        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        typeof(InstanceCorrelation).GetProperty(nameof(InstanceCorrelation.TerminalOutcome))!
            .SetValue(correlation, null);
        var originalCompletedAt = correlation.CompletedAt;
        var input = CreateCanceledInput(parent.Id, subInstanceId);
        SetupParent(parent);

        await CreateService().CancellationAsync(input);

        correlation.TerminalOutcome.ShouldBeNull();
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        VerifyCommonPhaseLoadedAllCorrelations(parent.Id);
        VerifyNoMutationOrResume();
        VerifyTerminalConflictLogged("legacy");
    }

    [Fact]
    public async Task CancellationAsync_WhenParentIsTerminal_ShouldNoOp()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        parent.Complete("bank");
        SetupParent(parent);

        await CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        VerifyNoMutationOrResume();
    }

    [Fact]
    public async Task CancellationAsync_SubProcessWithTerminalParent_ShouldCloseCorrelationOnly()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        parent.Complete("bank");
        parent.SetEffectiveState("terminal-parent");
        var canceledAt = DateTime.UtcNow.AddMinutes(-1);
        var input = CreateCanceledInput(parent.Id, subInstanceId) with { CanceledAt = canceledAt };
        SetupParent(parent);

        await CreateService().CancellationAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
        correlation.CompletedAt.ShouldBe(canceledAt);
        correlation.SubFlowCurrentState.ShouldBe(input.CanceledState);
        parent.Status.ShouldBe(InstanceStatus.Completed);
        parent.GetEffectiveState.ShouldBe("terminal-parent");
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _componentCacheStore.VerifyNoOtherCalls();
        _workflowExecution.VerifyNoOtherCalls();
        _instanceRepository.Verify(
            x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancellationAsync_WhenParentIsPassive_ShouldNoOp()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        typeof(Instance).GetProperty(nameof(Instance.Status))!
            .SetValue(parent, InstanceStatus.Passive);
        SetupParent(parent);

        await CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId));

        parent.IsCompleted.ShouldBeTrue();
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        VerifyNoMutationOrResume();
    }

    [Fact]
    public async Task CancellationAsync_WhenCorrelationDoesNotExist_ShouldNoOp()
    {
        var parent = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
        SetupParent(parent);

        await CreateService().CancellationAsync(CreateCanceledInput(parent.Id, Guid.NewGuid()));

        VerifyNoMutationOrResume();
    }

    [Theory]
    [InlineData(WorkflowErrorCodes.AutoTransitionConditionNotMet)]
    [InlineData(WorkflowErrorCodes.InstanceCompleted)]
    public async Task CancellationAsync_WhenResumeHasSoftResult_ShouldKeepCommittedOutcome(string errorCode)
    {
        var parent = CreateParentInstance(out var subInstanceId);
        SetupBlockingParent(parent);
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(Error.Validation(errorCode, "soft result")));

        await CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.TerminalOutcome
            .ShouldBe(SubItemTerminalOutcome.Canceled);
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _uowManager.Verify(x => x.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
    }

    [Fact]
    public async Task CancellationAsync_WhenResumeReturnsHardFailure_ShouldReloadAllCorrelationsAndRevert()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        SetupBlockingParent(parent);
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parent.Id)));
        var reloaded = CloneParentWithCanceledCorrelation(parent.Id, subInstanceId);
        _instanceRepository
            .SetupSequence(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent)
            .ReturnsAsync(reloaded);

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId)));

        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(reloaded, true, It.IsAny<CancellationToken>()), Times.Once);
        _uowManager.Verify(x => x.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Exactly(2));
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CancellationAsync_WhenParentTerminatesBeforeCompensation_ShouldLockReloadAndNotReopen()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var input = CreateCanceledInput(parent.Id, subInstanceId);
        SetupBlockingParent(parent);
        var terminalReload = CloneParentWithCanceledCorrelation(parent.Id, subInstanceId);
        terminalReload.Complete("bank");
        var order = new List<string>();
        var lockCall = 0;
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(
                $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}",
                It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"lock-{++lockCall}"))
            .ReturnsAsync(_lockScope.Object);
        var loadCall = 0;
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"load-{++loadCall}"))
            .ReturnsAsync(() => loadCall == 1 ? parent : terminalReload);
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("resume"))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parent.Id)));

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CancellationAsync(input, CancellationToken.None));

        order.ShouldBe(["lock-1", "load-1", "resume", "lock-2", "load-2"]);
        terminalReload.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeTrue();
        _lockScopeFactory.Verify(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}", CancellationToken.None), Times.Exactly(2));
        _instanceRepository.Verify(x => x.UpdateAsync(
            terminalReload, true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationAsync_WhenResumeThrows_ShouldRevertAndRethrowOriginalException()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        SetupBlockingParent(parent);
        var expected = new InvalidOperationException("resume failed");
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);
        var reloaded = CloneParentWithCanceledCorrelation(parent.Id, subInstanceId);
        _instanceRepository
            .SetupSequence(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent)
            .ReturnsAsync(reloaded);

        var actual = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId)));

        actual.ShouldBeSameAs(expected);
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task CancellationAsync_WhenCompensationLockFails_ShouldLogAndPreserveOriginalFailure()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        SetupBlockingParent(parent);
        var expected = new InvalidOperationException("original resume failure");
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);
        var failedLock = new Mock<ITransitionLockScope>();
        failedLock.SetupGet(x => x.IsAcquired).Returns(false);
        failedLock.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _lockScopeFactory
            .SetupSequence(x => x.AcquireAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(_lockScope.Object)
            .ReturnsAsync(failedLock.Object);

        var actual = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateService().CancellationAsync(
                CreateCanceledInput(parent.Id, subInstanceId),
                CancellationToken.None));

        actual.ShouldBeSameAs(expected);
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeTrue();
        _instanceRepository.Verify(x => x.FindWithAllCorrelationsAsync(
            parent.Id, It.IsAny<CancellationToken>()), Times.Once);
        _logger.Verify(x => x.Log(
            LogLevel.Warning,
            WorkflowEventIds.SubItemCorrelationRevertFailed,
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task CancellationAsync_WhenCallerCancellationAbortsResume_ShouldCompensateWithoutCanceledToken()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        SetupBlockingParent(parent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), cancellation.Token))
            .ThrowsAsync(expected);
        var reloaded = CloneParentWithCanceledCorrelation(parent.Id, subInstanceId);
        _instanceRepository
            .SetupSequence(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent)
            .ReturnsAsync(reloaded);

        var actual = await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateService().CancellationAsync(
                CreateCanceledInput(parent.Id, subInstanceId),
                cancellation.Token));

        actual.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(parent.Id, CancellationToken.None), Times.Once);
        _instanceRepository.Verify(
            x => x.UpdateAsync(reloaded, true, CancellationToken.None), Times.Once);
        _uow.Verify(x => x.CommitAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task CancellationAsync_WhenParentWorkflowCannotBeLoaded_ShouldFailBeforeCommit()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        SetupParent(parent);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Fail(
                Error.NotFound(WorkflowErrorCodes.NotFoundWorkflow, "not found", "parent-flow")));

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId)));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _workflowExecution.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, ExecMode.Sync)]
    [InlineData(false, ExecMode.Async)]
    public async Task CancellationAsync_ShouldPreserveCallerMode(bool sync, ExecMode expected)
    {
        var parent = CreateParentInstance(out var subInstanceId);
        SetupBlockingParent(parent);
        _workflowExecution
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(parent));

        await CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId) with { Sync = sync });

        _workflowExecution.Verify(x => x.ExecuteTransitionAsync(
            It.Is<WorkflowExecutionContext>(c => c.CallerMode == expected),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private SubflowCancellationService CreateService() => new(
        _uowManager.Object,
        _componentCacheStore.Object,
        _instanceRepository.Object,
        _workflowExecution.Object,
        _lockScopeFactory.Object,
        _logger.Object);

    private void SetupBlockingParent(Instance parent)
    {
        SetupParent(parent);
        var workflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));
    }

    private void SetupParent(Instance parent)
    {
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance instance, bool _, CancellationToken _) => instance);
    }

    private void VerifyCommonPhaseLoadedAllCorrelations(Guid parentId)
    {
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(parentId, It.IsAny<CancellationToken>()),
            Times.Once);
        _instanceRepository.Verify(
            x => x.FindAsync(parentId, It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void VerifyNoMutationOrResume()
    {
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _workflowExecution.VerifyNoOtherCalls();
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyTerminalConflictLogged(string existingOutcome)
    {
        _logger.Verify(x => x.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((value, _) =>
                value.ToString()!.Contains("terminal outcome conflict") &&
                value.ToString()!.Contains(existingOutcome)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private Dictionary<string, object> GetTelemetryScope() =>
        _logger.Invocations
            .Single(x => x.Method.Name == nameof(ILogger.BeginScope))
            .Arguments[0]
            .ShouldBeOfType<Dictionary<string, object>>();

    private static ActivityListener CaptureTerminalActivity(Guid parentInstanceId, Action<Activity> onStopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.SubFlow",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem(TelemetryConstants.TagNames.InstanceId)?.ToString() == parentInstanceId.ToString())
                {
                    onStopped(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static Result<TransitionOutput> Success(Instance parent) =>
        Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = parent.Id,
            Status = InstanceStatus.Active
        });

    private static Instance CloneParentWithCanceledCorrelation(Guid parentId, Guid subInstanceId)
    {
        var clone = CreateParentInstance(parentId, subInstanceId, SubFlowType.SubFlow);
        clone.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Canceled, DateTime.UtcNow);
        return clone;
    }

    private static SubItemCanceledInput CreateCanceledInput(Guid parentId, Guid childId) => new()
    {
        InstanceId = parentId,
        SubInstanceId = childId,
        Domain = "bank",
        Flow = "parent-flow",
        Version = "1.0.0",
        CanceledState = "child-canceled",
        CanceledAt = DateTime.UtcNow,
        Termination = TerminationContext.Direct(childId)
    };

    private static Instance CreateParentInstance(
        out Guid subInstanceId,
        SubFlowType? subFlowType = null)
    {
        subInstanceId = Guid.NewGuid();
        return CreateParentInstance(Guid.NewGuid(), subInstanceId, subFlowType ?? SubFlowType.SubFlow);
    }

    private static Instance CreateParentInstance(Guid parentId, Guid subInstanceId, SubFlowType subFlowType)
    {
        var parent = Instance.Create(parentId, "parent-flow", "1.0.0", "parent-key");
        parent.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        parent.SetEffectiveState("child-active");
        parent.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), parent.Id, "waiting-child", subInstanceId,
            subFlowType.Code, "bank", "child-flow", "1.0.0"));
        return parent;
    }
}
