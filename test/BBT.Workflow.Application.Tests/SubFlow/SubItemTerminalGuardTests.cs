using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// Covers the lock-free pre-check that keeps duplicate at-least-once terminal deliveries off the
/// per-subInstance distributed lock — and, critically, the settlement rule that stops it from
/// acknowledging a blocking SubFlow whose parent resume may still roll back.
/// </summary>
public sealed class SubItemTerminalGuardTests
{
    private readonly Mock<IInstanceCorrelationRepository> _correlationRepository = new();
    private readonly Mock<ILogger<SubItemTerminalGuard>> _logger = new();

    private readonly Guid _parentId = Guid.NewGuid();
    private readonly Guid _subId = Guid.NewGuid();

    public SubItemTerminalGuardTests()
        => _logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

    private SubItemTerminalGuard CreateGuard()
        => new(_correlationRepository.Object, _logger.Object);

    private void SetupSnapshot(InstanceCorrelation? correlation)
        => _correlationRepository
            .Setup(x => x.FindBySubInstanceIdAsReadOnlyAsync(_subId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(correlation);

    private InstanceCorrelation CreateCorrelation(
        SubItemTerminalOutcome? terminal,
        SubFlowType? subFlowType = null)
    {
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(), _parentId, "waiting-state", _subId,
            (subFlowType ?? SubFlowType.SubProcess).Code, "bank", "child-flow", "1.0.0");

        if (terminal.HasValue)
        {
            correlation.ApplyTerminalOutcome(terminal.Value, DateTime.UtcNow);
        }

        return correlation;
    }

    [Fact]
    public async Task ProbeAsync_SubProcess_WhenSameOutcomePersisted_ShouldReportAlreadySettled()
    {
        SetupSnapshot(CreateCorrelation(SubItemTerminalOutcome.Completed, SubFlowType.SubProcess));

        var result = await CreateGuard()
            .ProbeAsync(_parentId, _subId, SubItemTerminalOutcome.Completed);

        // A SubProcess commits its correlation and returns — no second phase, so the flag is final.
        result.ShouldBe(SubItemTerminalProbe.AlreadySettled);
    }

    [Fact]
    public async Task ProbeAsync_SubProcess_WhenDifferentOutcomePersisted_ShouldReportConflict()
    {
        SetupSnapshot(CreateCorrelation(SubItemTerminalOutcome.Faulted, SubFlowType.SubProcess));

        var result = await CreateGuard()
            .ProbeAsync(_parentId, _subId, SubItemTerminalOutcome.Completed);

        result.ShouldBe(SubItemTerminalProbe.Conflict);
    }

    [Fact]
    public async Task ProbeAsync_BlockingSubFlow_WhenSameOutcomePersisted_ShouldStillProceedToLockedPath()
    {
        SetupSnapshot(CreateCorrelation(SubItemTerminalOutcome.Completed, SubFlowType.SubFlow));

        var result = await CreateGuard()
            .ProbeAsync(_parentId, _subId, SubItemTerminalOutcome.Completed);

        // A blocking SubFlow marks the correlation terminal, releases the lock, and only then
        // resumes the parent — reverting the correlation if that resume fails. Acknowledging here
        // would consume a durable delivery whose work is about to be rolled back.
        result.ShouldBe(SubItemTerminalProbe.Proceed);
    }

    [Fact]
    public async Task ProbeAsync_BlockingSubFlow_WhenDifferentOutcomePersisted_ShouldStillProceedToLockedPath()
    {
        SetupSnapshot(CreateCorrelation(SubItemTerminalOutcome.Faulted, SubFlowType.SubFlow));

        var result = await CreateGuard()
            .ProbeAsync(_parentId, _subId, SubItemTerminalOutcome.Completed);

        result.ShouldBe(SubItemTerminalProbe.Proceed);
    }

    [Fact]
    public async Task ProbeAsync_WhenCorrelationStillOpen_ShouldProceedToLockedPath()
    {
        SetupSnapshot(CreateCorrelation(terminal: null));

        var result = await CreateGuard()
            .ProbeAsync(_parentId, _subId, SubItemTerminalOutcome.Completed);

        result.ShouldBe(SubItemTerminalProbe.Proceed);
    }

    [Fact]
    public async Task ProbeAsync_WhenCorrelationNotVisibleYet_ShouldProceedToLockedPath()
    {
        // The writer's transaction may still be open, so an absent snapshot must never be treated
        // as "already done" — that would silently drop a delivery that still has work to do.
        SetupSnapshot(null);

        var result = await CreateGuard()
            .ProbeAsync(_parentId, _subId, SubItemTerminalOutcome.Completed);

        result.ShouldBe(SubItemTerminalProbe.Proceed);
    }

    [Fact]
    public async Task ProbeAsync_WhenSnapshotReadThrows_ShouldFallBackToLockedPath()
    {
        _correlationRepository
            .Setup(x => x.FindBySubInstanceIdAsReadOnlyAsync(_subId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("snapshot unavailable"));

        var result = await CreateGuard()
            .ProbeAsync(_parentId, _subId, SubItemTerminalOutcome.Completed);

        // Correctness never depends on this optimisation succeeding.
        result.ShouldBe(SubItemTerminalProbe.Proceed);
    }
}
