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
    public async Task<SubItemTerminalProbe> ProbeAsync(
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
            return SubItemTerminalProbe.Proceed;
        }

        // Unknown or still-open correlation: the locked path owns the decision. A correlation that
        // is not yet visible here (writer's transaction still open) MUST reach the lock, otherwise
        // a delivery that genuinely still has work to do would be dropped.
        if (correlation is null || !correlation.IsCompleted)
        {
            return SubItemTerminalProbe.Proceed;
        }

        // A persisted terminal outcome only proves settlement for a non-blocking SubProcess, which
        // commits its correlation and returns. A blocking SubFlow releases the lock and resumes the
        // parent in a second phase, reverting the correlation if that resume fails — acknowledging
        // from the flag alone would consume a durable delivery whose work is about to roll back.
        if (!correlation.SubFlowType.Equals(SubFlowType.SubProcess))
        {
            logger.SubItemTerminalSettlementNotProvable(
                correlation.SubFlowType.Code,
                parentInstanceId,
                subInstanceId);

            return SubItemTerminalProbe.Proceed;
        }

        if (correlation.TerminalOutcome == incomingOutcome)
        {
            logger.SubItemTerminalDuplicateSkippedPreLock(
                incomingOutcome.ToString(),
                parentInstanceId,
                subInstanceId);

            return SubItemTerminalProbe.AlreadySettled;
        }

        logger.SubItemTerminalConflict(
            parentInstanceId,
            subInstanceId,
            correlation.TerminalOutcome?.ToString() ?? "legacy",
            incomingOutcome.ToString());

        return SubItemTerminalProbe.Conflict;
    }
}
