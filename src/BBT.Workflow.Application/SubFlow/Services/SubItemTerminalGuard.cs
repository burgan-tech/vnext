using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubItemTerminalGuard" />
public sealed class SubItemTerminalGuard(
    IInstanceCorrelationRepository correlationRepository,
    ILogger<SubItemTerminalGuard> logger) : ISubItemTerminalGuard
{
    /// <inheritdoc />
    public Task<bool> TryMarkSettledAsync(
        Guid subInstanceId,
        SubItemTerminalOutcome outcome,
        DateTime settledAt,
        CancellationToken cancellationToken = default) =>
        correlationRepository.TryMarkSettledAsync(subInstanceId, outcome, settledAt, cancellationToken);

    /// <inheritdoc />
    public async Task<SubItemTerminalProbe> ProbeAsync(
        Guid parentInstanceId,
        Guid subInstanceId,
        SubItemTerminalOutcome incomingOutcome,
        CancellationToken cancellationToken = default)
    {
        var result = await ProbeWithSnapshotAsync(
            parentInstanceId,
            subInstanceId,
            incomingOutcome,
            cancellationToken);
        return result.Decision;
    }

    /// <inheritdoc />
    public async Task<SubItemTerminalProbeResult> ProbeWithSnapshotAsync(
        Guid parentInstanceId,
        Guid subInstanceId,
        SubItemTerminalOutcome incomingOutcome,
        CancellationToken cancellationToken = default)
    {
        InstanceCorrelation? correlation;

        try
        {
            correlation = await correlationRepository.FindBySubInstanceIdAsReadOnlyAsync(
                subInstanceId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The probe is a pure fast-path optimisation. If the snapshot read fails for any
            // reason, fall through to the authoritative locked path rather than failing the
            // delivery — correctness never depends on this read succeeding.
            logger.SubItemTerminalProbeFailed(ex, parentInstanceId, subInstanceId);
            return new(SubItemTerminalProbe.Proceed, null, null);
        }

        // Unknown or still-open correlation: the locked path owns the decision. A correlation that
        // is not yet visible here (writer's transaction still open) MUST reach the lock, otherwise
        // a delivery that genuinely still has work to do would be dropped.
        if (correlation is null || !correlation.IsCompleted)
        {
            return new(
                SubItemTerminalProbe.Proceed,
                correlation?.ParentState,
                correlation?.SubFlowType);
        }

        // Legacy SubProcess rows are settled by definition because they have no resume phase.
        // Blocking SubFlows are provably settled only after their durable marker is present.
        if (!correlation.SubFlowType.Equals(SubFlowType.SubProcess) && correlation.SettledAt is null)
        {
            logger.SubItemTerminalSettlementNotProvable(
                correlation.SubFlowType.Code,
                parentInstanceId,
                subInstanceId);

            return new(SubItemTerminalProbe.Proceed, correlation.ParentState, correlation.SubFlowType);
        }

        if (correlation.TerminalOutcome == incomingOutcome)
        {
            logger.SubItemTerminalDuplicateSkippedPreLock(
                incomingOutcome.ToString(),
                parentInstanceId,
                subInstanceId);

            return new(SubItemTerminalProbe.AlreadySettled, correlation.ParentState, correlation.SubFlowType);
        }

        logger.SubItemTerminalConflict(
            parentInstanceId,
            subInstanceId,
            correlation.TerminalOutcome?.ToString() ?? "legacy",
            incomingOutcome.ToString());

        return new(SubItemTerminalProbe.Conflict, correlation.ParentState, correlation.SubFlowType);
    }
}
