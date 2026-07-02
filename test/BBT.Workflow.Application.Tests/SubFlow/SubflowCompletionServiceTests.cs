using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
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
    }

    [Fact]
    public async Task CompletionAsync_WhenParentWorkflowLoadFails_FaultsParentWithIncident()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parentInstance.Id, subInstanceId);

        _instanceRepository
            .Setup(x => x.FindAsync(parentInstance.Id, true, It.IsAny<CancellationToken>()))
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
            .Setup(x => x.FindAsync(parentInstance.Id, true, It.IsAny<CancellationToken>()))
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

    private SubflowCompletionService CreateService()
        => new(
            _uowManager.Object,
            _componentCacheStore.Object,
            _instanceRepository.Object,
            _runtimeInfoProvider.Object,
            _workflowExecutionService.Object,
            _outputMappingService.Object,
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
