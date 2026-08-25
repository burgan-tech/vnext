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
    /// UpdateData: accepted unconditionally — no Busy check, no reserve, no status flip. The
    /// execution never owns the status lifecycle; instance-data writes are serialized by the
    /// FOR UPDATE write funnel. A satisfied auto transition is handed to a real owner at the
    /// continuation boundary (the pipeline reserves there, or drops the continuation when a
    /// competing chain holds the instance).
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
/// What an accept did to the instance status under the admission lock. Tells the accept path
/// whether the chain reserve was taken here (so the relay may claim it) and what to compensate
/// when the work that follows the flip fails.
/// </summary>
public enum AcceptFlip
{
    /// <summary>No status flip: updateData, an owner re-entry, or an internal resume.</summary>
    None = 0,

    /// <summary>Active→Busy reserve; this accept owns the Busy flag.</summary>
    Reserved = 1,

    /// <summary>Unconditional Busy flip for cancel/exit/timeout, and it actually changed the status.</summary>
    TakenOver = 2,

    /// <summary>The whole active SubFlow chain was marked Busy down to the leaf.</summary>
    ChainReserved = 3
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

    /// <summary>
    /// Reserves the whole active SubFlow chain for a request that <see cref="IsSubflowForward"/>
    /// classifies as a relay: under the short status lock, marks this instance Busy and propagates
    /// down every active SubFlow correlation to the leaf.
    /// <para>
    /// The relay levels are already Busy for their subflow's lifetime by design, so in practice
    /// this flips only the leaf — and the leaf is the instance a long-polling client actually
    /// observes, because the state function reports the deepest active subflow's status. Without
    /// it an accepted async request answers the caller while the leaf still reads Active, and the
    /// client concludes nothing is in progress.
    /// </para>
    /// <para>
    /// Only the first hop runs under this context's status lock; each nested level flips under its
    /// own lock, the same guarantee the existing Busy propagation provides.
    /// </para>
    /// </summary>
    Task<Result> ReserveSubflowChainAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compensates a successful <see cref="ReserveSubflowChainAsync"/> whose follow-up work never
    /// ran. Releases only what the chain reserve actually flipped — levels holding an open SubFlow
    /// correlation are recursed past, not settled. Never throws.
    /// </summary>
    Task ReleaseSubflowChainAsync(TransitionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admits an asynchronous accept under a SINGLE status lock: acquires
    /// <see cref="TransitionExecutionContext.LockKey"/> once, performs the status flip the
    /// request's <see cref="AdmissionKind"/> calls for, then runs <paramref name="underLock"/>
    /// (the duplicate-job guard and the durable enqueue) while still holding it. A failure or
    /// throw from <paramref name="underLock"/> compensates the flip before the lock is released.
    /// <para>
    /// This is the accept path's only distributed lock. It exists because the duplicate-active-job
    /// check-and-insert has no database constraint behind it, so it has to be serialized with the
    /// same critical section as the status flip rather than with a second lock of its own.
    /// </para>
    /// <para>
    /// The callbacks run with the lock HELD: never call <see cref="ReserveAsync"/>,
    /// <see cref="TakeOverAsync"/>, <see cref="ReserveSubflowChainAsync"/> or either Release method
    /// from inside <paramref name="underLock"/> — they acquire the same key and the status lock is
    /// a single-attempt, non-reentrant TryAcquire, so the nested call would fail to acquire.
    /// </para>
    /// <para>
    /// EXCEPTION — <see cref="AdmissionKind.Unconditional"/> (updateData): the callback runs with
    /// NO lock held and <see cref="AcceptFlip.None"/>, mirroring the sync path, which never locked
    /// this kind. updateData is status-neutral (nothing to flip) and must accept parallel requests
    /// (the duplicate-job guard does not apply to it; job identity is unique per enqueue), so
    /// there is nothing left for the lock to serialize. Its instance-data writes are serialized
    /// downstream by the per-instance write funnel.
    /// </para>
    /// </summary>
    Task<Result> AcceptAsync(
        TransitionExecutionContext context,
        Func<AcceptFlip, CancellationToken, Task<Result>> underLock,
        CancellationToken cancellationToken = default);
}
