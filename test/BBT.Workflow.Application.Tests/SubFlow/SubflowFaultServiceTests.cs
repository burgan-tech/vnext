using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
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
    private readonly Mock<ILogger<SubflowFaultService>> _logger = new();

    public SubflowFaultServiceTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _uowManager
            .Setup(m => m.BeginAsync(It.IsAny<UnitOfWorkOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_uow.Object);
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
            x => x.BeginAsync(It.IsAny<UnitOfWorkOptions>(), It.IsAny<CancellationToken>()),
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
            .Returns(Task.CompletedTask);

        await CreateService().FaultAsync(input, CancellationToken.None);

        incidentVisibleToMapping.ShouldBeTrue();
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
            _logger.Object);
    }

    private void SetupParent(Instance parentInstance, Definitions.Workflow parentWorkflow)
    {
        _instanceRepository
            .Setup(x => x.FindAsync(parentInstance.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parentInstance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
    }

    private static Instance CreateParentInstance(out Guid subInstanceId)
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
            SubFlowType.SubFlow.Code,
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
        JsonElement childData)
    {
        return new SubFlowFaultedInput
        {
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
