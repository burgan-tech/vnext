using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
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
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class SubflowCompletionServiceTests
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
    private readonly Mock<ILogger<SubflowCompletionService>> _logger = new();

    public SubflowCompletionServiceTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _uowManager
            .Setup(m => m.Begin(It.IsAny<UnitOfWorkOptions>()))
            .Returns(_uow.Object);
        _lockScope.SetupGet(x => x.IsAcquired).Returns(true);
        _lockScope.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_lockScope.Object);
        _logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public async Task CompletionAsync_WhenParentLockCannotBeAcquired_ShouldThrowRetryableLockException()
    {
        var input = CreateInput(Guid.NewGuid(), Guid.NewGuid());
        _lockScope.SetupGet(x => x.IsAcquired).Returns(false);

        var exception = await Should.ThrowAsync<SubflowTerminalLockNotAcquiredException>(
            () => CreateService().CompletionAsync(input));

        exception.Code.ShouldBe(WorkflowErrorCodes.SubflowTerminalLockNotAcquired);
        _lockScopeFactory.Verify(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}",
            It.IsAny<CancellationToken>()), Times.Once);
        _instanceRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompletionAsync_WhenCompletedOutcomeAlreadyRecorded_ShouldNoOp()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        Activity? terminalActivity = null;
        using var activityListener = CaptureTerminalActivity(parent.Id, activity => terminalActivity = activity);
        var originalCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Completed, originalCompletedAt);
        SetupCompletedCorrelationPath(parent);

        await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Completed);
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        VerifyNoMappingOrResume();
        VerifyTerminalDuplicateLogged();
        GetTelemetryScope()[TelemetryConstants.TagNames.SubItemType]
            .ShouldBe(SubFlowType.SubFlow.Code);
        terminalActivity!.GetTagItem(TelemetryConstants.TagNames.SubItemType)
            .ShouldBe(SubFlowType.SubFlow.Code);
    }

    [Fact]
    public async Task CompletionAsync_WhenCorrelationAlreadyCanceled_ShouldNotMapOrResume()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var originalCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Canceled, originalCompletedAt);
        SetupCompletedCorrelationPath(parent);

        await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        VerifyNoMappingOrResume();
        VerifyTerminalConflictLogged("Canceled");
    }

    [Fact]
    public async Task CompletionAsync_WhenLegacyCompletedCorrelationHasNoOutcome_ShouldNotOverwrite()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        parent.CompleteCorrelation(subInstanceId);
        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        typeof(InstanceCorrelation).GetProperty(nameof(InstanceCorrelation.TerminalOutcome))!
            .SetValue(correlation, null);
        var originalCompletedAt = correlation.CompletedAt;
        SetupCompletedCorrelationPath(parent);

        await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

        correlation.TerminalOutcome.ShouldBeNull();
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        VerifyNoMappingOrResume();
        VerifyTerminalConflictLogged("legacy");
    }

    [Fact]
    public async Task CompletionAsync_SubProcess_ShouldOnlyCloseCorrelation()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        parent.SetEffectiveState("child-active");
        var completedAt = DateTime.UtcNow.AddMinutes(-1);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateService().CompletionAsync(
            CreateInput(parent.Id, subInstanceId) with { CompletedAt = completedAt });

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.IsCompleted.ShouldBeTrue();
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Completed);
        correlation.CompletedAt.ShouldBe(completedAt);
        correlation.SubFlowCurrentState.ShouldBe("child-done");
        correlation.SubFlowStateChangedAt.ShouldBe(completedAt);
        parent.Status.ShouldBe(InstanceStatus.Active);
        parent.GetEffectiveState.ShouldBe("child-active");
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _instanceRepository.Verify(
            x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompletionAsync_SubProcessWithTerminalParent_ShouldCloseCorrelationOnly()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        parent.Complete("bank");
        parent.SetEffectiveState("terminal-parent");
        var completedAt = DateTime.UtcNow.AddMinutes(-1);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateService().CompletionAsync(
            CreateInput(parent.Id, subInstanceId) with { CompletedAt = completedAt });

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.IsCompleted.ShouldBeTrue();
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Completed);
        correlation.CompletedAt.ShouldBe(completedAt);
        parent.Status.ShouldBe(InstanceStatus.Completed);
        parent.GetEffectiveState.ShouldBe("terminal-parent");
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _instanceRepository.Verify(
            x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompletionAsync_BlockingSubFlowWithTerminalParent_ShouldNoOp()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
        parent.Complete("bank");
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        parent.Status.ShouldBe(InstanceStatus.Completed);
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompletionAsync_BlockingSubFlowWithPassiveParent_ShouldNoOp()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
        typeof(Instance).GetProperty(nameof(Instance.Status))!
            .SetValue(parent, InstanceStatus.Passive);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompletionAsync_SubProcessWithPassiveParent_ShouldCloseCorrelationOnly()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        typeof(Instance).GetProperty(nameof(Instance.Status))!
            .SetValue(parent, InstanceStatus.Passive);
        parent.SetEffectiveState("terminal-parent");
        var completedAt = DateTime.UtcNow.AddMinutes(-1);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateService().CompletionAsync(
            CreateInput(parent.Id, subInstanceId) with { CompletedAt = completedAt });

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Completed);
        correlation.SubFlowCurrentState.ShouldBe("child-done");
        correlation.SubFlowStateChangedAt.ShouldBe(completedAt);
        parent.Status.ShouldBe(InstanceStatus.Passive);
        parent.GetEffectiveState.ShouldBe("terminal-parent");
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompletionAsync_WhenParentWorkflowLoadFails_FaultsParentWithIncident()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parentInstance.Id, subInstanceId);

        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parentInstance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);

        // Parent workflow definition cannot be loaded from the component cache.
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Fail(
                Error.NotFound(WorkflowErrorCodes.NotFoundWorkflow, "not found", "parent-flow")));

        await CreateService().CompletionAsync(input, CancellationToken.None);

        parentInstance.Status.ShouldBe(InstanceStatus.Faulted);
        parentInstance.HasActiveIncident.ShouldBeTrue();
        _instanceRepository.Verify(
            x => x.UpdateAsync(parentInstance, true, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // Pipeline resume must NOT be attempted when the definition is unavailable.
        _workflowExecutionService.Verify(
            x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true, ExecMode.Sync)]
    [InlineData(false, ExecMode.Async)]
    public async Task CompletionAsync_ResumesParentPipelineWithCallerModeFromInput(bool sync, ExecMode expectedCallerMode)
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parentInstance.Id, subInstanceId, sync);
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");

        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parentInstance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(
                parentInstance,
                parentWorkflow,
                It.IsAny<string>(),
                It.IsAny<JsonElement?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = parentInstance.Id,
                Status = InstanceStatus.Active
            }));

        await CreateService().CompletionAsync(input, CancellationToken.None);

        _workflowExecutionService.Verify(
            x => x.ExecuteTransitionAsync(
                It.Is<WorkflowExecutionContext>(ctx =>
                    ctx.Mode == ExecMode.Resume &&
                    ctx.CallerMode == expectedCallerMode &&
                    ctx.Execution!.IsSubFlowResume),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompletionAsync_ResumesParentPipelineWithSubFlowResumeInstanceId()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parentInstance.Id, subInstanceId);
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");

        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parentInstance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parentInstance, parentWorkflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        WorkflowExecutionContext? captured = null;
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((ctx, _) => captured = ctx)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = parentInstance.Id,
                Status = InstanceStatus.Active
            }));

        await CreateService().CompletionAsync(input, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Execution!.IsSubFlowResume.ShouldBeTrue();
        captured.Execution.SubFlowResumeInstanceId.ShouldBe(subInstanceId);
    }

    [Fact]
    public async Task CompletionAsync_WhenResumeFails_RevertsCorrelationViaUnfilteredLoad()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parentInstance.Id, subInstanceId);
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");

        var reloaded = CloneParentWithCompletedCorrelation(parentInstance, subInstanceId);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(
                parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(
                parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reloaded);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance i, bool _, CancellationToken _) => i);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parentInstance, parentWorkflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Resume fails hard (lock conflict) → revert path must run.
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parentInstance.Id)));

        // The revert reload returns a fresh entity with the completed correlation.

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CompletionAsync(input, CancellationToken.None));

        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAndDataAsync(parentInstance.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(parentInstance.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(reloaded, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompletionAsync_WhenParentTerminatesBeforeCompensation_ShouldLockReloadAndNotReopen()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parent.Id, subInstanceId);
        var workflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        var terminalReload = CloneParentWithCompletedCorrelation(parent, subInstanceId);
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
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"load-{++loadCall}"))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"load-{++loadCall}"))
            .ReturnsAsync(terminalReload);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance instance, bool _, CancellationToken _) => instance);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parent, workflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("resume"))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parent.Id)));

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CompletionAsync(input, CancellationToken.None));

        order.ShouldBe(["lock-1", "load-1", "resume", "lock-2", "load-2"]);
        terminalReload.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeTrue();
        _lockScopeFactory.Verify(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}", CancellationToken.None), Times.Exactly(2));
        _instanceRepository.Verify(x => x.UpdateAsync(
            terminalReload, true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompletionAsync_WhenCompensationReloadMissesParent_ShouldPreserveOriginalFailureWithoutDetachedFallback()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parent.Id, subInstanceId);
        var workflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance?)null);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance instance, bool _, CancellationToken _) => instance);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parent, workflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
        var expected = new InvalidOperationException("original resume failure");
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expected);

        var actual = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateService().CompletionAsync(input, CancellationToken.None));

        actual.ShouldBeSameAs(expected);
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeTrue();
        _instanceRepository.Verify(x => x.FindWithAllCorrelationsAndDataAsync(
            parent.Id, CancellationToken.None), Times.Once);
        _instanceRepository.Verify(x => x.FindWithAllCorrelationsAsync(
            parent.Id, CancellationToken.None), Times.Once);
        VerifyRevertFailureLogged();
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

    private SubflowCompletionService CreateService()
        => new(
            _uowManager.Object,
            _componentCacheStore.Object,
            _instanceRepository.Object,
            _runtimeInfoProvider.Object,
            _workflowExecutionService.Object,
            _outputMappingService.Object,
            _lockScopeFactory.Object,
            _logger.Object);

    private void SetupCompletedCorrelationPath(Instance parent)
    {
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parent, parentWorkflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = parent.Id,
                Status = InstanceStatus.Active
            }));
    }

    private void VerifyNoMappingOrResume()
    {
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAndDataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _instanceRepository.Verify(
            x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyTerminalDuplicateLogged()
    {
        _logger.Verify(x => x.Log(
            LogLevel.Debug,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((value, _) =>
                value.ToString()!.Contains("Duplicate Completed SubItem terminal outcome")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
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

    private void VerifyRevertFailureLogged()
    {
        _logger.Verify(x => x.Log(
            LogLevel.Warning,
            WorkflowEventIds.SubItemCorrelationRevertFailed,
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
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

    private static Instance CreateParentInstance(
        out Guid subInstanceId,
        SubFlowType? subFlowType = null)
    {
        subInstanceId = Guid.NewGuid();
        var parentInstance = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
        parentInstance.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        parentInstance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "waiting-child",
            subInstanceId,
            (subFlowType ?? SubFlowType.SubFlow).Code,
            "bank",
            "child-flow",
            "1.0.0"));

        return parentInstance;
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
