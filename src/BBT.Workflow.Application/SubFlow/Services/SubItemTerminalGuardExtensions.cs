using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Wires <see cref="ISubItemTerminalGuard"/>'s lock-free probe into the terminal services' bounded
/// lock wait, so a backup delivery stops waiting the moment the relay it collided with has committed.
/// </summary>
public static class SubItemTerminalGuardExtensions
{
    /// <summary>
    /// Returns <paramref name="wait"/> with <see cref="LockAcquireWait.KeepWaiting"/> set to the probe:
    /// keep waiting only while the correlation is still open for <paramref name="incomingOutcome"/>.
    /// <para>
    /// The probe is the same READ COMMITTED snapshot read the pre-lock short-circuit uses, so it never
    /// sees the holder's uncommitted write and can only answer "settled" once the holder is done. A
    /// failed probe read answers <c>Proceed</c>, i.e. keeps waiting — correctness never depends on it.
    /// </para>
    /// </summary>
    public static LockAcquireWait WhileCorrelationOpen(
        this LockAcquireWait wait,
        ISubItemTerminalGuard terminalGuard,
        Guid parentInstanceId,
        Guid subInstanceId,
        SubItemTerminalOutcome incomingOutcome) =>
        wait with
        {
            KeepWaiting = async ct =>
                await terminalGuard.ProbeAsync(parentInstanceId, subInstanceId, incomingOutcome, ct)
                == SubItemTerminalProbe.Proceed
        };
}
