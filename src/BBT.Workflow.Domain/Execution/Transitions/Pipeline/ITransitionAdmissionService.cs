using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// How a transition request relates to the Busy-as-mutex admission gate.
/// </summary>
public enum AdmissionKind
{
    /// <summary>
    /// Regular transition (state transitions and shared transitions): rejected with 409 while
    /// the instance is Busy; otherwise reserved (Active→Busy + chain token) under the short
    /// status lock before the pipeline body runs.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Cancel / exit / timeout: exempt from the Busy 409 — they take over the instance under
    /// the short status lock by rotating the chain token, which invalidates any in-flight chain.
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
    /// background-job re-entry carrying the chain token stamped at accept time. No reserve —
    /// ownership is verified against the instance's durable chain token instead.
    /// </summary>
    OwnerReentry = 3
}

/// <summary>
/// Admission gate for the Busy-as-mutex execution model
/// (<c>WorkflowExecutionOptions.UseBusyAsMutex</c>): classifies transition requests, rejects
/// Busy instances up front, and performs the short-lock status check-and-set that replaces the
/// legacy whole-chain lock. All methods are flag-aware — with the flag off, checks succeed
/// unconditionally so legacy call sites can invoke them unguarded.
/// </summary>
public interface ITransitionAdmissionService
{
    /// <summary>Classifies the transition request for admission purposes.</summary>
    AdmissionKind Classify(TransitionExecutionContext context);

    /// <summary>
    /// Cheap Busy pre-check using the already-loaded aggregate (no extra DB round trip):
    /// fails with <c>Instance:100031</c> (409) when the instance is Busy and the request is
    /// <see cref="AdmissionKind.Normal"/>. This is a fast-fail optimization only — the
    /// authoritative check is the re-read inside <see cref="ReserveAsync"/> under the lock.
    /// </summary>
    Result CheckAdmission(TransitionExecutionContext context);

    /// <summary>
    /// Reserves the instance for a <see cref="AdmissionKind.Normal"/> request: under the short
    /// status lock, re-checks the status and flips Active→Busy stamping a fresh chain ownership
    /// token (with subflow Busy propagation). Returns the token that the pipeline body and its
    /// continuations must carry.
    /// </summary>
    Task<Result<Guid>> ReserveAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes over a possibly-Busy instance for a <see cref="AdmissionKind.BypassBusyCheck"/>
    /// request (cancel/exit/timeout): under the short status lock, rotates the chain token and
    /// marks Busy. The rotation is the chain-invalidation signal — an in-flight chain detects
    /// the mismatch at its next hop-start ownership check or status write and stops.
    /// </summary>
    Task<Result<Guid>> TakeOverAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that an <see cref="AdmissionKind.OwnerReentry"/> request still owns the
    /// instance: the context's chain token must match the instance's durable token (checked via
    /// the execution-snapshot projection). Directive-driven resumes without a token pass —
    /// their ownership is carried by the resume directive itself.
    /// </summary>
    Task<Result> VerifyOwnershipAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compensates a successful <see cref="ReserveAsync"/> whose follow-up work failed before
    /// any job or pipeline could run (e.g. async enqueue failure): under the short status lock,
    /// settles Busy back to Active if — and only if — the instance still carries the supplied
    /// token. Never throws; failure to release is logged and left to the chain reaper.
    /// </summary>
    Task ReleaseReservationAsync(TransitionExecutionContext context, Guid chainToken, CancellationToken cancellationToken = default);
}
