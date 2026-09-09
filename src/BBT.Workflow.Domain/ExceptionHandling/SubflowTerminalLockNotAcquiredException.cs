using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Thrown when a subflow propagation to the parent — a terminal outcome (completion / fault /
/// cancellation) or a state change — cannot acquire the per-sub-item lock. This is an EXPECTED
/// contention condition — another delivery for the same sub-item holds it — not an internal
/// error. It maps to HTTP 503
/// (transient) so the inbox orchestration relay redelivers the message; it must NOT map to
/// 409, which <see cref="BBT.Workflow.Shared.TransientHttpStatus"/> treats as permanent and
/// would cause the relay to drop the message, leaving the parent stuck.
/// </summary>
public sealed class SubflowTerminalLockNotAcquiredException(
    string domain,
    string flow,
    string instance,
    string outcome)
    : UserFriendlyException(
        code: WorkflowErrorCodes.SubflowTerminalLockNotAcquired,
        message:
        $"Parent instance terminal lock could not be acquired for {outcome} propagation. " +
        $"domain: {domain}, flow: {flow}, instance: {instance}.");
