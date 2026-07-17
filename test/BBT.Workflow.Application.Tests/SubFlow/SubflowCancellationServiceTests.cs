using System;
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
    private readonly Mock<ILogger<SubflowCancellationService>> _logger = new();

    public SubflowCancellationServiceTests()
    {
        _uow.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(x => x.DisposeAsync())
            .Returns(ValueTask.CompletedTask);
        _uowManager.Setup(x => x.Begin(It.IsAny<UnitOfWorkOptions>()))
            .Returns(_uow.Object);
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
    public async Task CancellationAsync_WhenCanceledOutcomeAlreadyRecorded_ShouldNoOp()
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
        VerifyNoMutationOrResume();
    }

    [Fact]
    public async Task CancellationAsync_WhenDifferentOutcomeAlreadyRecorded_ShouldNotOverwrite()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var originalCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Faulted, originalCompletedAt);
        var input = CreateCanceledInput(parent.Id, subInstanceId);
        SetupParent(parent);

        await CreateService().CancellationAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        VerifyNoMutationOrResume();
        VerifyTerminalConflictLogged("Faulted");
    }

    [Fact]
    public async Task CancellationAsync_WhenLegacyCompletedCorrelationHasNoOutcome_ShouldNotOverwrite()
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
            Times.Never);
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
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reloaded);

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId)));

        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()), Times.Once);
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(reloaded, true, It.IsAny<CancellationToken>()), Times.Once);
        _uowManager.Verify(x => x.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Exactly(2));
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
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
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reloaded);

        var actual = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateService().CancellationAsync(CreateCanceledInput(parent.Id, subInstanceId)));

        actual.ShouldBeSameAs(expected);
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
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
            .Setup(x => x.FindAsync(parent.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance instance, bool _, CancellationToken _) => instance);
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
