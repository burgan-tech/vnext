using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class SubflowFaultServiceTests
{
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IComponentCacheStore> _componentCacheStore = new();
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IWorkflowExecutionService> _workflowExecutionService = new();
    private readonly Mock<ISubflowOutputMappingService> _outputMappingService = new();
    private readonly Mock<ITransitionLockScopeFactory> _lockScopeFactory = new();
    private readonly Mock<ISubItemTerminalGuard> _terminalGuard = new();
    private readonly Mock<ITransitionLockScope> _lockScope = new();
    private readonly Mock<ILogger<SubflowFaultService>> _logger = new();

    public SubflowFaultServiceTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _uowManager
            .Setup(m => m.Begin(It.IsAny<UnitOfWorkOptions>()))
            .Returns(_uow.Object);

        _outputMappingService
            .Setup(x => x.ApplyAsync(
                It.IsAny<Instance>(),
                It.IsAny<Definitions.Workflow>(),
                It.IsAny<string>(),
                It.IsAny<JsonElement?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
        _lockScope.SetupGet(x => x.IsAcquired).Returns(true);
        _lockScope.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_lockScope.Object);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(
                It.IsAny<string>(), It.IsAny<LockAcquireWait>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_lockScope.Object);
        // Default: the pre-lock probe finds nothing terminal, so every test exercises the
        // authoritative locked path unless it explicitly overrides this.
        _terminalGuard
            .Setup(x => x.ProbeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<SubItemTerminalOutcome>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubItemTerminalProbe.Proceed);
        _logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public async Task FaultAsync_WhenParentLockCannotBeAcquired_ShouldThrowRetryableLockException()
    {
        var input = CreateInput(Guid.NewGuid(), Guid.NewGuid(), CreateJsonElement("{}"));
        _lockScope.SetupGet(x => x.IsAcquired).Returns(false);

        var exception = await Should.ThrowAsync<SubflowTerminalLockNotAcquiredException>(
            () => CreateService().FaultAsync(input));

        exception.Code.ShouldBe(WorkflowErrorCodes.SubflowTerminalLockNotAcquired);
        _lockScopeFactory.Verify(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}:sub:{input.SubInstanceId:N}",
            It.IsAny<LockAcquireWait>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _instanceRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FaultAsync_WithAbortBoundary_ShouldCompleteCorrelationRunMappingAndFaultParent()
    {
        var childData = CreateJsonElement("""{"childStatus":"Faulted"}""");
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(ErrorBoundary.AbortAll);
        var input = CreateInput(parentInstance.Id, subInstanceId, childData);

        SetupParent(parentInstance, parentWorkflow);

        await CreateService().FaultAsync(input, CancellationToken.None);

        var correlation = parentInstance.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.IsCompleted.ShouldBeTrue();
        correlation.SubFlowCurrentState.ShouldBe("child-task");
        parentInstance.GetEffectiveState.ShouldBe("waiting-child");
        parentInstance.Status.ShouldBe(InstanceStatus.Faulted);
        parentInstance.HasActiveIncident.ShouldBeTrue();

        _outputMappingService.Verify(
            x => x.ApplyAsync(
                parentInstance,
                parentWorkflow,
                "waiting-child",
                It.Is<JsonElement?>(body => body.HasValue && body.Value.GetProperty("childStatus").GetString() == "Faulted"),
                CancellationToken.None),
            Times.Once);
        _workflowExecutionService.Verify(
            x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FaultAsync_WhenOutputMappingFailsTransiently_ShouldPropagateWithoutCommitting()
    {
        // Pins today's behaviour after the Task 4 classification split: SubflowOutputMappingService
        // rethrows a transient infrastructure fault instead of returning a failed Result, and this
        // service's own catch (line 288) logs it and rethrows rather than swallowing it. If that catch
        // is ever changed to swallow, redelivery breaks silently and the child's data is dropped with
        // no signal — this test exists so that regression is caught here, not in production.
        var childData = CreateJsonElement("""{"childStatus":"Faulted"}""");
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(ErrorBoundary.AbortAll);
        var input = CreateInput(parentInstance.Id, subInstanceId, childData);

        SetupParent(parentInstance, parentWorkflow);
        _outputMappingService
            .Setup(x => x.ApplyAsync(
                It.IsAny<Instance>(), It.IsAny<Definitions.Workflow>(), It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileLoadException("Assembly with same name is already loaded"));

        await Should.ThrowAsync<FileLoadException>(
            () => CreateService().FaultAsync(input, CancellationToken.None));

        // Nothing is committed: the correlation-completion write and the fault write both roll back
        // with the transaction, so a redelivered fault signal finds the correlation still open.
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FaultAsync_WithNotifyBoundary_ShouldExecuteFallbackAsErrorBoundaryTransition()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(
            ErrorBoundary.Builder()
                .OnError(ErrorAction.Notify, transition: "error-fallback")
                .Build());
        var input = CreateInput(parentInstance.Id, subInstanceId, CreateJsonElement("""{"result":404}"""));

        WorkflowExecutionContext? capturedContext = null;
        SetupParent(parentInstance, parentWorkflow);
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((context, _) => capturedContext = context)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = parentInstance.Id,
                Status = InstanceStatus.Active
            }));

        await CreateService().FaultAsync(input, CancellationToken.None);

        parentInstance.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeTrue();
        parentInstance.Status.ShouldBe(InstanceStatus.Busy);
        capturedContext.ShouldNotBeNull();
        capturedContext!.TransitionKey.ShouldBe("error-fallback");
        capturedContext.IsErrorBoundaryTransition.ShouldBeTrue();
        capturedContext.IsReentry.ShouldBeTrue();
        capturedContext.Mode.ShouldBe(ExecMode.Sync);
    }

    [Fact]
    public async Task FaultAsync_WhenFallbackTransitionFails_ShouldRevertCorrelationForRetry()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(
            ErrorBoundary.Builder()
                .OnError(ErrorAction.Notify, transition: "error-fallback")
                .Build());
        var input = CreateInput(parentInstance.Id, subInstanceId, CreateJsonElement("""{"result":404}"""));

        SetupParent(parentInstance, parentWorkflow);
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(Error.Validation("transition-failed", "Fallback failed")));

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().FaultAsync(input, CancellationToken.None));

        parentInstance.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        parentInstance.Status.ShouldBe(InstanceStatus.Busy);
        _uowManager.Verify(
            x => x.Begin(It.IsAny<UnitOfWorkOptions>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task FaultAsync_ShouldRecordIncidentBeforeRunningOutputMapping()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(ErrorBoundary.AbortAll);
        var input = CreateInput(parentInstance.Id, subInstanceId, CreateJsonElement("""{"childStatus":"Faulted"}"""));

        SetupParent(parentInstance, parentWorkflow);

        // Capture whether the parent already carries the subflow incident at the moment
        // output mapping runs, so the mapping script can route on the fault.
        var incidentVisibleToMapping = false;
        _outputMappingService
            .Setup(x => x.ApplyAsync(
                It.IsAny<Instance>(),
                It.IsAny<Definitions.Workflow>(),
                It.IsAny<string>(),
                It.IsAny<JsonElement?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => incidentVisibleToMapping = parentInstance.HasActiveIncident)
            .ReturnsAsync(Result.Ok());

        await CreateService().FaultAsync(input, CancellationToken.None);

        incidentVisibleToMapping.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, ExecMode.Sync)]
    [InlineData(false, ExecMode.Async)]
    public async Task FaultAsync_WithNotifyBoundary_ShouldPropagateCallerModeFromInput(bool sync, ExecMode expectedCallerMode)
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(
            ErrorBoundary.Builder()
                .OnError(ErrorAction.Notify, transition: "error-fallback")
                .Build());
        var input = CreateInput(parentInstance.Id, subInstanceId, CreateJsonElement("""{"result":404}"""), sync);

        SetupParent(parentInstance, parentWorkflow);
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = parentInstance.Id,
                Status = InstanceStatus.Active
            }));

        await CreateService().FaultAsync(input, CancellationToken.None);

        _workflowExecutionService.Verify(
            x => x.ExecuteTransitionAsync(
                It.Is<WorkflowExecutionContext>(ctx =>
                    ctx.TransitionKey == "error-fallback" &&
                    ctx.CallerMode == expectedCallerMode),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FaultAsync_WithIgnoreBoundary_ResumesParentPipelineWithSubFlowResumeInstanceId()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(
            ErrorBoundary.Builder()
                .OnError(ErrorAction.Ignore)
                .Build());
        var input = CreateInput(parentInstance.Id, subInstanceId, CreateJsonElement("""{"result":404}"""));

        WorkflowExecutionContext? captured = null;
        SetupParent(parentInstance, parentWorkflow);
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((ctx, _) => captured = ctx)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = parentInstance.Id,
                Status = InstanceStatus.Active
            }));

        await CreateService().FaultAsync(input, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Execution!.IsSubFlowResume.ShouldBeTrue();
        captured.Execution.SubFlowResumeInstanceId.ShouldBe(subInstanceId);
    }

    [Fact]
    public async Task FaultAsync_WhenIgnoreBoundaryResumeFails_RevertsCorrelationViaUnfilteredLoad()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var parentWorkflow = CreateParentWorkflow(
            ErrorBoundary.Builder()
                .OnError(ErrorAction.Ignore)
                .Build());
        var input = CreateInput(parentInstance.Id, subInstanceId, CreateJsonElement("""{"result":404}"""));

        SetupParent(parentInstance, parentWorkflow);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance i, bool _, CancellationToken _) => i);

        // Resume fails hard (lock conflict) -> revert path must run.
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parentInstance.Id)));

        // The revert reload returns a fresh entity WITH the completed correlation
        // (what FindWithAllCorrelationsAsync guarantees and FindAsync does not).
        var reloaded = CloneParentWithCompletedCorrelation(parentInstance, subInstanceId);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reloaded);

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().FaultAsync(input, CancellationToken.None));

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
    public async Task FaultAsync_WhenParentTerminatesBeforeCompensation_ShouldLockReloadAndNotReopen()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var workflow = CreateParentWorkflow(ErrorBoundary.Builder().OnError(ErrorAction.Ignore).Build());
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        var terminalReload = CloneParentWithCompletedCorrelation(parent, subInstanceId);
        terminalReload.Complete("bank");
        var order = new List<string>();
        var lockCall = 0;
        // Main path takes the waiting overload; the compensation path still fails fast.
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(
                $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}:sub:{input.SubInstanceId:N}",
                It.IsAny<LockAcquireWait>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"lock-{++lockCall}"))
            .ReturnsAsync(_lockScope.Object);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(
                $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}:sub:{input.SubInstanceId:N}",
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
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("resume"))
            .ReturnsAsync(Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(parent.Id)));

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().FaultAsync(input, CancellationToken.None));

        order.ShouldBe(["lock-1", "load-1", "resume", "lock-2", "load-2"]);
        terminalReload.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeTrue();
        _lockScopeFactory.Verify(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}:sub:{input.SubInstanceId:N}",
            It.IsAny<LockAcquireWait>(), CancellationToken.None), Times.Once);
        _lockScopeFactory.Verify(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}:sub:{input.SubInstanceId:N}", CancellationToken.None), Times.Once);
        _instanceRepository.Verify(x => x.UpdateAsync(
            terminalReload, true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FaultAsync_SubProcess_ShouldOnlyCloseCorrelation()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        var faultedAt = DateTime.UtcNow.AddMinutes(-1);
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}")) with
        {
            FaultedAt = faultedAt
        };
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(input.SubInstanceId)!;
        correlation.IsCompleted.ShouldBeTrue();
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        correlation.CompletedAt.ShouldBe(faultedAt);
        correlation.SubFlowCurrentState.ShouldBe(input.FaultedState);
        correlation.SubFlowStateChangedAt.ShouldBe(faultedAt);
        parent.Status.ShouldBe(InstanceStatus.Active);
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _instanceRepository.Verify(
            x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()),
            Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FaultAsync_SubProcessWithTerminalParent_ShouldCloseCorrelationOnly()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        parent.Complete("bank");
        parent.SetEffectiveState("terminal-parent");
        var faultedAt = DateTime.UtcNow.AddMinutes(-1);
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}")) with
        {
            FaultedAt = faultedAt
        };
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        correlation.CompletedAt.ShouldBe(faultedAt);
        correlation.SubFlowCurrentState.ShouldBe(input.FaultedState);
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
    public async Task FaultAsync_WhenFaultOutcomeAlreadyRecorded_ShouldNoOp()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        Activity? terminalActivity = null;
        using var activityListener = CaptureTerminalActivity(parent.Id, activity => terminalActivity = activity);
        var originalCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Faulted, originalCompletedAt);
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        correlation.SubFlowCurrentState.ShouldBeNull();
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        GetTelemetryScope()[TelemetryConstants.TagNames.SubItemType]
            .ShouldBe(SubFlowType.SubProcess.Code);
        terminalActivity!.GetTagItem(TelemetryConstants.TagNames.SubItemType)
            .ShouldBe(SubFlowType.SubProcess.Code);
    }

    [Fact]
    public async Task FaultAsync_WhenDifferentOutcomeAlreadyRecorded_ShouldNotOverwriteOrRunBlockingPath()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        var originalCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Canceled, originalCompletedAt);
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        correlation.SubFlowCurrentState.ShouldBeNull();
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        VerifyTerminalConflictLogged("Canceled");
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FaultAsync_WhenLegacyCompletedCorrelationHasNoOutcome_ShouldNotOverwrite()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        parent.CompleteCorrelation(subInstanceId);
        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        typeof(InstanceCorrelation).GetProperty(nameof(InstanceCorrelation.TerminalOutcome))!
            .SetValue(correlation, null);
        var originalCompletedAt = correlation.CompletedAt;
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        correlation.TerminalOutcome.ShouldBeNull();
        correlation.CompletedAt.ShouldBe(originalCompletedAt);
        correlation.SubFlowCurrentState.ShouldBeNull();
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        VerifyTerminalConflictLogged("legacy");
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FaultAsync_WhenParentIsTerminal_ShouldNoOpAndCommit()
    {
        var parent = CreateParentInstance(out var subInstanceId);
        parent.Complete("bank");
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FaultAsync_BlockingSubFlowWithPassiveParent_ShouldNoOpAndCommit()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
        typeof(Instance).GetProperty(nameof(Instance.Status))!
            .SetValue(parent, InstanceStatus.Passive);
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        parent.GetIncidentsForMonitor().ShouldBeEmpty();
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FaultAsync_SubProcessWithPassiveParent_ShouldCloseCorrelationOnly()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
        typeof(Instance).GetProperty(nameof(Instance.Status))!
            .SetValue(parent, InstanceStatus.Passive);
        parent.SetEffectiveState("terminal-parent");
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        correlation.SubFlowCurrentState.ShouldBe(input.FaultedState);
        parent.Status.ShouldBe(InstanceStatus.Passive);
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
    public async Task FaultAsync_WhenCorrelationDoesNotExist_ShouldNoOpAndCommit()
    {
        var parent = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
        var input = CreateInput(parent.Id, Guid.NewGuid(), CreateJsonElement("{}"));
        SetupParentInstance(parent);

        await CreateService().FaultAsync(input);

        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _componentCacheStore.VerifyNoOtherCalls();
        _outputMappingService.VerifyNoOtherCalls();
        _workflowExecutionService.VerifyNoOtherCalls();
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FaultAsync_WhenPayloadClaimsSubProcessButStoredTypeIsSubFlow_ShouldRunBlockingPath()
    {
        var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
        var workflow = CreateParentWorkflow(ErrorBoundary.AbortAll);
        var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}"));
        var subItemTypeProperty = typeof(SubFlowFaultedInput).GetProperty("SubItemType");
        subItemTypeProperty.ShouldNotBeNull();
        subItemTypeProperty.SetValue(input, SubItemType.SubProcess);
        SetupParent(parent, workflow);

        await CreateService().FaultAsync(input);

        parent.Status.ShouldBe(InstanceStatus.Faulted);
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.TerminalOutcome
            .ShouldBe(SubItemTerminalOutcome.Faulted);
        _outputMappingService.Verify(
            x => x.ApplyAsync(
                parent,
                workflow,
                "waiting-child",
                It.IsAny<JsonElement?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void SubFlowFaultedInput_ShouldExposeTerminalPropagationContract()
    {
        typeof(SubFlowFaultedInput).GetProperty("SubItemType").ShouldNotBeNull();
        typeof(SubFlowFaultedInput).GetProperty("Termination").ShouldNotBeNull();
        typeof(SubFlowFaultedInput).GetProperty("RootInstanceId").ShouldNotBeNull();
    }

    private static Instance CloneParentWithCompletedCorrelation(Instance source, Guid subInstanceId)
    {
        var clone = Instance.Create(source.Id, "parent-flow", "1.0.0", "parent-key");
        clone.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        clone.SetEffectiveState("child-active");
        clone.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), clone.Id, "waiting-child", subInstanceId,
            SubFlowType.SubFlow.Code, "bank", "child-flow", "1.0.0"));
        clone.CompleteCorrelation(subInstanceId);
        return clone;
    }

    private SubflowFaultService CreateService()
    {
        var resolver = new ErrorBoundaryResolver(Mock.Of<ILogger<ErrorBoundaryResolver>>());
        var executor = new ErrorActionExecutor(Mock.Of<ILogger<ErrorActionExecutor>>());

        return new SubflowFaultService(
            _uowManager.Object,
            _componentCacheStore.Object,
            _instanceRepository.Object,
            _workflowExecutionService.Object,
            _outputMappingService.Object,
            resolver,
            executor,
            _lockScopeFactory.Object,
            _terminalGuard.Object,
            Options.Create(new WorkflowExecutionOptions()),
            _logger.Object);
    }

    private void VerifyTerminalConflictLogged(string existingOutcome)
    {
        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) =>
                    value.ToString()!.Contains("terminal outcome conflict") &&
                    value.ToString()!.Contains(existingOutcome)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
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

    private void SetupParent(Instance parentInstance, Definitions.Workflow parentWorkflow)
    {
        SetupParentInstance(parentInstance);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parentInstance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
    }

    private void SetupParentInstance(Instance parentInstance)
    {
        // Main load (correlations + data) and compensation reload (correlations only)
        // both resolve to the same entity unless a test overrides the revert path.
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAndDataAsync(parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
    }

    private static Instance CreateParentInstance(
        out Guid subInstanceId,
        SubFlowType? subFlowType = null)
    {
        subInstanceId = Guid.NewGuid();
        var parentInstance = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
        parentInstance.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        parentInstance.SetEffectiveState("child-active");
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

    private static Definitions.Workflow CreateParentWorkflow(ErrorBoundary boundary)
    {
        var workflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");
        workflow.SetErrorBoundary(boundary);
        workflow.AddState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        return workflow;
    }

    private static SubFlowFaultedInput CreateInput(
        Guid parentInstanceId,
        Guid subInstanceId,
        JsonElement childData,
        bool sync = false)
    {
        return new SubFlowFaultedInput
        {
            Sync = sync,
            InstanceId = parentInstanceId,
            Domain = "bank",
            Flow = "parent-flow",
            Version = "1.0.0",
            SubInstanceId = subInstanceId,
            FaultedState = "child-task",
            FaultedStateType = (int)StateType.Intermediate,
            FaultedStateSubType = (int)StateSubType.None,
            InstanceData = childData,
            FaultedAt = DateTime.UtcNow,
            SubFlowName = "child-flow",
            IncidentMessage = "HTTP 404",
            IncidentErrorCode = "Http.NotFound",
            IncidentErrorLayer = ErrorLayer.Task.ToString(),
            IncidentStatusCode = 404,
            IncidentStackTrace = "at Child.CallApi() in Child.cs:line 12",
            IncidentTaskKey = "call-child-api",
            IncidentTransition = "submit"
        };
    }

    private static JsonElement CreateJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
