using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// How a transition request relates to the Busy-as-mutex admission gate.
/// </summary>
public enum AdmissionKind
{
    /// <summary>
    /// Regular transition (state transitions and shared transitions): rejected with 409 while
    /// the instance is Busy; otherwise reserved (Active→Busy) under the short status lock
    /// before the pipeline body runs. The Busy flag is the mutex for the whole chain — the
    /// pipeline and its auto-chain run with no held lease and no further checks.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Cancel / exit / timeout: exempt from the Busy 409 — accepted even while another
    /// pipeline owns the instance — but still take the short status lock: admission marks the
    /// instance Busy under the lock (<see cref="ITransitionAdmissionService.TakeOverAsync"/>)
    /// so the flip is serialized with every other status write.
    /// </summary>
    BypassBusyCheck = 1,

    /// <summary>
    /// UpdateData: accepted unconditionally — no Busy check, no lock, no status flip. Instance
    /// data writes are safe under the DB versioning trigger (per-instance advisory lock keeps
    /// VersionNo monotonic and IsLatest unique).
    /// </summary>
    Unconditional = 2,

    /// <summary>
    /// A hop that already owns the Busy instance: subflow resume, long-poll acknowledge, or a
    /// background-job re-entry whose accept already reserved the instance
    /// (<see cref="TransitionExecutionContext.IsPreReserved"/>). No check, no reserve.
    /// </summary>
    OwnerReentry = 3
}

/// <summary>
/// Admission gate for the Busy-as-mutex execution model: classifies transition requests,
/// rejects Busy instances up front, and performs the short-lock status check-and-set that
/// replaces a chain-spanning distributed lock. Only the first request-handling hop is gated;
/// once reserved, the pipeline and its auto-chain run unlocked.
/// </summary>
public interface ITransitionAdmissionService
{
    /// <summary>Classifies the transition request for admission purposes.</summary>
    AdmissionKind Classify(TransitionExecutionContext context);

    /// <summary>
    /// Classifies a transition request from the workflow definition and the requested key alone —
    /// no loaded instance required. Used by intake fast-fail paths (the app service checks the
    /// Busy status via a light projection before loading the aggregate). Directive-driven kinds
    /// (timeout, resumes, pre-reserved) cannot be detected here and classify as
    /// <see cref="AdmissionKind.Normal"/>; the pipeline prologue remains authoritative.
    /// </summary>
    AdmissionKind ClassifyKey(Definitions.Workflow workflow, string transitionKey);

    /// <summary>
    /// Returns whether a Busy instance should have this request forwarded to its active SubFlow
    /// instead of being rejected: a <see cref="AdmissionKind.Normal"/> request against a Busy
    /// parent that has an open SubFlow-type correlation. Such requests are admitted without a
    /// reserve — <c>ForwardToActiveSubflowStep</c> forwards them and the subflow runs its own
    /// admission in its own context.
    /// </summary>
    bool IsSubflowForward(TransitionExecutionContext context);

    /// <summary>
    /// Cheap Busy pre-check using the already-loaded aggregate (no extra DB round trip):
    /// fails with <c>Instance:100031</c> (409) when the instance is Busy and the request is
    /// <see cref="AdmissionKind.Normal"/>. This is a fast-fail optimization only — the
    /// authoritative check is the re-read inside <see cref="ReserveAsync"/> under the lock.
    /// </summary>
    Result CheckAdmission(TransitionExecutionContext context);

    /// <summary>
    /// Reserves the instance for a <see cref="AdmissionKind.Normal"/> request: under the short
    /// status lock, re-checks the status and flips Active→Busy (with subflow Busy propagation).
    /// A concurrent winner surfaces as <c>Instance:100031</c> (409).
    /// </summary>
    Task<Result> ReserveAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admits a <see cref="AdmissionKind.BypassBusyCheck"/> request (cancel/exit/timeout):
    /// under the short status lock, marks the instance Busy without checking — exempt from the
    /// 409 but still serialized through the same distributed lock as every other status flip.
    /// Pulling an Active instance to Busy also blocks new Normal admissions while the
    /// cancel/exit pipeline runs. Idempotent when the instance is already Busy or Completed.
    /// </summary>
    Task<Result> TakeOverAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compensates a successful <see cref="ReserveAsync"/> whose follow-up work failed before
    /// any job or pipeline could run (e.g. async enqueue failure): under the short status lock,
    /// settles Busy back to Active. Never throws — failure to release is logged; job-timeout
    /// recovery remains the safety net.
    /// </summary>
    Task ReleaseReservationAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);
}
