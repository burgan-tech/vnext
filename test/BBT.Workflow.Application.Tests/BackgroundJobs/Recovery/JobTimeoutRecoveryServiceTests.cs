using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs.Recovery;

public class JobTimeoutRecoveryServiceTests
{
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IInstanceRepository> _instanceRepo = new();
    private readonly Mock<IInstanceTransitionRepository> _transitionRepo = new();
    private readonly Mock<ILogger<JobTimeoutRecoveryService>> _logger = new();

    public JobTimeoutRecoveryServiceTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _uowManager
            .Setup(m => m.BeginAsync(It.IsAny<UnitOfWorkOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_uow.Object);
    }

    private JobTimeoutRecoveryService CreateService(int timeoutSeconds = 300) =>
        new(_uowManager.Object, _instanceRepo.Object, _transitionRepo.Object,
            Options.Create(new WorkflowExecutionOptions { TransitionJobTimeoutSeconds = timeoutSeconds }),
            _logger.Object);

    private static TransitionJobPayload CreatePayload(Guid? instanceId = null) => new()
    {
        JobName = "trans-abc-go",
        InstanceId = instanceId ?? Guid.NewGuid(),
        TransitionKey = "go",
        Domain = "test",
        Workflow = "test-flow",
        Version = "1.0.0"
    };

    /// <summary>
    /// Instance not found → early return, no fault or persistence.
    /// </summary>
    [Fact]
    public async Task FaultInstanceAsync_WhenInstanceNotFound_DoesNothing()
    {
        var payload = CreatePayload();

        _instanceRepo
            .Setup(r => r.FindAsync(payload.InstanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance?)null);

        await CreateService().FaultInstanceAsync(payload, CancellationToken.None);

        _instanceRepo.Verify(
            r => r.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Instance found but not Busy (e.g. already Completed by a race) → skip, no fault.
    /// </summary>
    [Fact]
    public async Task FaultInstanceAsync_WhenInstanceNotBusy_DoesNothing()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        var instance = Instance.Create(instanceId, "test_flow", "1.0.0", "key");
        // Instance.Create starts as Active — not Busy

        _instanceRepo
            .Setup(r => r.FindAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        await CreateService().FaultInstanceAsync(payload, CancellationToken.None);

        _instanceRepo.Verify(
            r => r.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Instance is Busy and has an open TransitionRecord → fault + incident + close transition + commit.
    /// </summary>
    [Fact]
    public async Task FaultInstanceAsync_WhenBusyWithOpenTransition_FaultsAndClosesTransition()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        var instance = Instance.Create(instanceId, "test_flow", "1.0.0", "key");
        instance.Busy();

        var openTransition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "go", "state-a",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}"));

        _instanceRepo
            .Setup(r => r.FindAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        _instanceRepo
            .Setup(r => r.UpdateAsync(instance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        _transitionRepo
            .Setup(r => r.GetLatestIncompleteAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openTransition);
        _transitionRepo
            .Setup(r => r.UpdateCompletedAsync(openTransition, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateService().FaultInstanceAsync(payload, CancellationToken.None);

        // Instance faulted with an incident
        instance.Status.ShouldBe(InstanceStatus.Faulted);
        instance.HasActiveIncident.ShouldBeTrue();

        // Open TransitionRecord was closed
        openTransition.FinishedAt.ShouldNotBeNull();
        _transitionRepo.Verify(
            r => r.UpdateCompletedAsync(openTransition, CancellationToken.None),
            Times.Once);

        // Persistence committed
        _instanceRepo.Verify(
            r => r.UpdateAsync(instance, true, CancellationToken.None),
            Times.Once);
        _uow.Verify(u => u.CommitAsync(CancellationToken.None), Times.Once);
    }

    /// <summary>
    /// Instance is Busy but no open TransitionRecord → fault + incident, no transition update.
    /// </summary>
    [Fact]
    public async Task FaultInstanceAsync_WhenBusyWithNoOpenTransition_FaultsWithoutClosingTransition()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        var instance = Instance.Create(instanceId, "test_flow", "1.0.0", "key");
        instance.Busy();

        _instanceRepo
            .Setup(r => r.FindAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        _instanceRepo
            .Setup(r => r.UpdateAsync(instance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        _transitionRepo
            .Setup(r => r.GetLatestIncompleteAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstanceTransition?)null);

        await CreateService().FaultInstanceAsync(payload, CancellationToken.None);

        instance.Status.ShouldBe(InstanceStatus.Faulted);
        _transitionRepo.Verify(
            r => r.UpdateCompletedAsync(It.IsAny<InstanceTransition>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
