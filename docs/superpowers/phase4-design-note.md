# Phase 4 — Transaction Boundary Refactor: Design + Characterization Gate (Decision B)

> Task 4.1. **DESIGN ONLY — no production code changed in this gate.** This note maps
> the current transaction design, resolves the three high-risk interactions, and
> specifies the concrete Option-B cut for a later task (4.2+).

Branch: `feature/phase4-transaction-boundary`. All `file:line` refs are at the time of writing.

---

## Problem statement (confirmed)

Today the synchronous transition pipeline holds **one** explicit DB transaction (Aether UoW,
`RequiresNew`) open across the **entire auto-chain**, including the synchronous remote task calls
(`taskCoordinator.ExecuteWithDetailsAsync` → Dapr → Execution service, 30–60s). The pooled Npgsql
connection is pinned (idle) during that network I/O → pool exhaustion under load.

**Decision B (target):** Remove the single outer `RequiresNew` UoW in `TransitionRunner`. Each step
manages its own short UoW; **no** DB transaction is held across any `taskCoordinator` remote call.
Cross-step atomicity is intentionally dropped in favour of: early transition record + per-task
`successfulTaskIds` idempotency + crash-resume (`ResumePointStepOrder`) + ChainReaper recovery — the
same philosophy already used by the async path (`AsyncTransitionStrategy`).

---

## Q1 — Where the explicit transaction opens/commits; when the connection is acquired

- **Open:** `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs:134-136`
  — `uowManager.BeginAsync(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }, ct)`,
  inside a child DI scope created by `_scopeFactory.ExecuteWithWorkflowAsync(...)` (line 125).
- **Body:** `await core.ExecuteTransitionCoreAsync(context, ct)` (line 138) — this runs the **entire**
  pipeline **and the whole inline auto-chain** (see Q2).
- **Commit:** `await uow.CommitAsync(ct)` at `TransitionRunner.cs:142` — exactly **one** commit for
  the whole chain. Deferred distributed events are published **after** commit (line 144,
  `PublishDeferredEventsAsync`), outside the transaction, exceptions swallowed.
- **Retry wrapper (Faz 2):** the whole `ScopeDelegate` invocation is wrapped by a Polly
  `ResiliencePipeline` at `TransitionRunner.cs:99-101`; `ShouldHandle` only retries genuinely
  transient DB faults via `IDbTransientErrorClassifier.IsRetriableTransient` (line 68-69). On retry
  the **entire** scope+UoW+chain re-runs from scratch.
- **Connection acquisition (Aether):** Aether's EF Core UoW acquires the physical connection
  **eagerly at `BeginTransaction`** (the `RequiresNew` scope opens an `IDbContextTransaction`, which
  opens the connection). NOTE: the Aether `framework/` sources are **not vendored in this checkout**
  (`find .../framework` → not found), so this is asserted from behaviour and the documented pool-pinning
  symptom rather than read from source. **The 4.2 implementer must confirm against the Aether
  `EfCoreTransactionSource` / `UnitOfWorkManager` that no transaction is opened until the first
  `SaveChanges` in the per-step UoW model**, otherwise short UoWs that only read will still pin a
  connection. (Open question — see Risks.)

`WorkflowExecutionService.ExecuteTransitionCoreAsync` (`.../Services/WorkflowExecutionService.cs`)
deliberately has **no** `[UnitOfWork]` attribute — the XML doc states UoW is owned by `TransitionRunner`.

## Q2 — One transaction for the whole chain, or one-per-hop? Lock & busy placement

- **One transaction for the whole chain.** `ExecuteTransitionCoreAsync` → strategy →
  `TransitionPipeline.RunChainAsync`
  (`.../Execution/Transitions/Pipeline/TransitionPipeline.cs`) loops `while (true)` over the first
  transition **plus** every inline auto-chained hop, all inside the single UoW opened in Q1. The
  per-hop lock TTL is extended between iterations (`lockScope.ExtendAsync`), not the transaction.
- **Distributed lock** is acquired in `TransitionPipeline.RunAsync` **before** `RunChainAsync`
  (normal path: `_lockScopeFactory.AcquireAsync(context.LockKey, ...)`), and is **outside / independent
  of** the DB transaction — it lives in the orchestration scope, not the UoW. Reserved transitions take
  their own type-scoped lock.
- **Busy mark** is set **after lock acquisition** via `_busyMarker.MarkBusyAsync(...)` and, within the
  pipeline, `SetBusyStep` (order 19) calls `instance.BeginChain(token)` + `UpdateAsync(saveChanges:true)`.
  Under today's model the busy write is buffered in the single UoW and only durably committed at the
  final `CommitAsync`. **This is the key behaviour Option B changes**: busy must commit early (its own
  short UoW) so it is durable before the long remote calls and recoverable on crash.

## Q3 — Steps that write to DB / rely on the ambient UoW (`saveChanges:true`)

All run inside the single ambient UoW today; each calls `instanceRepository.UpdateAsync(instance, true, ct)`
(the `true` = saveChanges flushes into the ambient transaction, not a separate commit):

| Order | Step | DB writes |
|------|------|-----------|
| 19 | `SetBusyStep` | `instance.BeginChain(token)`; `UpdateAsync(true)` — status→Busy + chain token (`SetBusyStep.cs`) |
| 20 | `CreateTransitionRecordStep` | `UpdateAsync(instance, true)` (mapped data + key) **and** `instanceTransitionRepository.InsertAsync(instanceTransition, saveChanges:true)` — the audit/transition record |
| 25 | `ResourceLockStep` | resource-lock script side effects (script-managed) |
| 30 | `RunOnExecuteTasksStep` | **remote** `taskCoordinator.ExecuteWithDetailsAsync(...)` THEN `ApplyScriptContextChanges` + `UpdateAsync(instance, true)` |
| 40 | `RunOnExitTasksStep` | same shape as 30 (remote tasks → instance update) |
| 50 | `ChangeStateStep` | `instance.ChangeState(target)`; `ExtractAndDeferInstanceEvents()`; `UpdateAsync(instance, true)` |
| 60 | `RunOnEntryTasksStep` | same shape as 30 (remote tasks → instance update) |
| 70 | `HandleSubFlowStep` | correlation start / enqueue StartSubflowJob (post-commit job) |
| 100 | `HandleFinishStep` | complete/cancel instance writes |
| 110 | `FinalizeTransitionStep` | completes transition record; **`instance.ClearResumePoint()`** (`FinalizeTransitionStep.cs:45`); `UpdateAsync` |
| 112 | `ResolveAvailableStep` | applies deferred Active status |

Fault path: `TransitionPipeline.MarkInstanceFaultedAsync` / `...FromPostCommitAsync` add an incident,
call `instance.Fault(domain)`, and `UpdateAsync(instance, true)`.

## Q4 — Remote task invocation & where InstanceTask results persist (child scope confirmed)

- `RunOnExecute/OnExit/OnEntry` steps call `taskCoordinator.ExecuteWithDetailsAsync(..., successfulTaskIds, ct)`
  (`RunOnExecuteTasksStep.cs:57-59`, mirrored in `RunOnExitTasksStep` / `RunOnEntryTasksStep`).
- **CONFIRMED child-scope:** `TaskCoordinator` runs each task in its **own** DI scope —
  `await using var scope = _serviceScopeFactory.CreateAsyncScope();` then resolves a scoped
  `ITaskExecutionEngine` (`TaskCoordinator.cs`, parallel-group path). `InstanceTask` result rows are
  persisted in that child scope, i.e. **outside** the main transition UoW. So **task results are
  already durable independent of the main transaction commit** — this is what makes Option B safe for
  idempotency: on re-run, completed tasks are visible via `successfulTaskIds`.
- The main UoW only ever holds Instance-aggregate writes + the transition record (Q3).

## Q5 — INTERACTION 1 (critical): Faz 2 retry vs per-step commits — how resume works + recommendation

**How the resume mechanism works today (precise):**
- `Instance.ResumePointStepOrder` is an `int?` column, **persisted** — migration
  `20260611200135_Instance_LockChainToken.cs:29`, mapped in
  `InstancesModelCreatingExtensions.cs:78`, property at `Instance.cs:149`.
- Domain methods: `SetResumePoint(int)` (`Instance.cs:582`) and `ClearResumePoint()` (`Instance.cs:587`).
- **Read path:** `TransitionExecutor.ExecuteOneAsync` (`TransitionExecutor.cs:55-59`): if
  `Instance.ResumePointStepOrder` is set and no in-memory `Directives.ResumeFromOrder` exists, it calls
  `Directives.RequestResumeFrom(resumeOrder + 1)`.
- **Skip mechanism:** `BuildExecutionPlan` (`TransitionExecutor.cs`) consumes `ConsumeResumeFrom()` and
  filters the ordered step list to `s.Order >= startOrder`, so a resumed run **skips all
  already-completed steps** and starts at the next one. Remote-task steps that *do* re-run are made safe
  by `successfulTaskIds` (Q4) — completed tasks are bypassed, not re-invoked.
- **WRITE PATH IS DORMANT:** `SetResumePoint(...)` is **never called in production today** — the only
  caller of either method is `ClearResumePoint()` at `FinalizeTransitionStep.cs:45`. So the column,
  domain method, and `TransitionExecutor` read path are all **wired but not yet armed** — a
  forward-looking hook clearly designed for exactly this refactor (the doc comment on `ResumePointStepOrder`
  literally says "S8 … On crash-resume the pipeline restarts from the next step"). **Today, because there
  is one UoW + Polly retries the whole scope, no per-step checkpoint is needed and none is written.**

**The hazard under Option B:** with per-step commits, the Faz 2 Polly retry currently re-runs the
**entire** `RunAsync` → it would re-execute already-committed steps (double SetBusy / duplicate
transition-record insert / re-applied state change).

**RECOMMENDATION (resolve via resume, not whole-scope retry):**
1. **Arm the dormant checkpoint.** After each step that commits durably, call
   `instance.SetResumePoint(step.Order)` and persist it in that step's short UoW. This makes the
   existing `TransitionExecutor.cs:55-59` read path live: a re-entry skips completed steps.
2. **Narrow the Faz 2 Polly retry from whole-scope to per-step-UoW granularity.** Keep the same
   `IDbTransientErrorClassifier` predicate, but apply it around each step's short UoW commit rather than
   around the whole chain. A transient fault then retries **only the failing step's** commit; steps
   already committed are not touched. The transition-record duplicate-key guard
   (`CreateTransitionRecordStep`) and `successfulTaskIds` make even a step-level re-run idempotent.
3. Do **not** keep a chain-wide retry that re-enters from step 0 — that is the unsafe combination.
   If a transient fault escapes the per-step retry, let it surface; the instance is Busy with a
   persisted `ResumePointStepOrder`, and recovery is the resume path (re-dispatch) or ChainReaper.

Net: Faz 2 transient-retry resilience is **preserved** but moves to per-step; cross-step replay safety
is provided by `ResumePointStepOrder` + `successfulTaskIds` + the duplicate-key guard.

## Q6 — INTERACTION 2: partial-progress recovery (crash after step N commits, before N+1)

- **State after partial progress:** instance is `Busy` (committed by `SetBusyStep`), with a committed
  transition record (`CreateTransitionRecordStep`), any completed remote-task `InstanceTask` rows
  committed in child scopes, and — once Option B arms it — `ResumePointStepOrder = N`.
- **Recovery:**
  - **Resume:** a re-dispatch (sync re-entry or async job) rebuilds the context, `TransitionExecutor`
    reads `ResumePointStepOrder` and **resumes at N+1**, skipping committed steps; completed tasks are
    bypassed via `successfulTaskIds`.
  - **ChainReaper (backstop):** `ChainReaperService.SweepAsync`
    (`.../BackgroundJobs/Recovery/ChainReaperService.cs`) finds instances `Busy` past
    `max(60, TransitionJobTimeoutSeconds*3)` with **no live job** (`GetStuckBusyChainsAsync` +
    `jobRepository.GetListActiveAsync`) and **faults** them (incident `CHAIN_STALLED`, `Abort/Global`).
    Today it is conservative — it faults rather than re-enqueues.
- **Invariants that MUST hold** (assert/preserve in 4.2):
  1. Transition record exists **before** any remote work runs (record at order 20, remote tasks at 30+).
  2. `successfulTaskIds` (`instanceTaskRepository.GetSuccessfulTaskIdsAsync(transitionId)`) prevents
     duplicate execution of business-successful tasks on any re-run.
  3. A failure never leaves the instance in a **falsely-completed** state — finish/complete writes
     (order 100/110) only run after the state change (50) and only on the success path.
  4. Busy is durable before remote work (so ChainReaper can see/recover a stuck chain).
  5. `ResumePointStepOrder` is **cleared** at Finalize (`FinalizeTransitionStep.cs:45`) so it never
     leaks into the next transition.

## Q7 — INTERACTION 3: Busy + lock + crash

- **Busy is committed early** (Option B: its own short UoW in `SetBusyStep`). A crash leaves the
  instance `Busy`.
- **Unstick mechanism:** `ChainReaperService` — heartbeat-threshold sweep (Q6) faults Busy-with-no-live-job
  instances. There is **no dedicated busy-timeout field**; staleness is judged off `ChainHeartbeatAt`
  (refreshed on chain progress) vs `TransitionJobTimeoutSeconds*3`.
- **Lock independence CONFIRMED:** the distributed lock is acquired/extended/released by
  `ITransitionLockScopeFactory` / `ITransitionLockScope` entirely in the orchestration scope
  (`TransitionPipeline.RunAsync`/`RunChainAsync`), **never inside the EF UoW**. Therefore committing and
  releasing a connection **per step while still holding the lock** is safe — the lock outlives any
  individual short UoW. (Caveat: ensure per-step UoW durations stay well under the lock TTL; the chain
  already calls `lockScope.ExtendAsync` between hops — keep extending around long remote steps.)

## Q8 — Concrete Option-B cut (ordered methods/files to change)

Target ordering for 4.2 (no change in this gate):

1. **`TransitionRunner.ExecuteWithScopeAsync` (`TransitionRunner.cs:121-149`)** — remove the outer
   `uowManager.BeginAsync(RequiresNew)` + single `CommitAsync`. Keep the child DI scope,
   `ICurrentUser.ChangeFromHeaders`, and post-commit event publish. The method becomes "run the chain,
   then publish deferred events" — **no ambient transaction**.
2. **`TransitionRunner.RunAsync` Polly wrapper (`TransitionRunner.cs:99-101`)** — change retry scope from
   whole-chain to per-step (Q5). Either move the `ResiliencePipeline` down into the per-step commit, or
   inject a per-step retry executor the steps/pipeline use. Keep the same `IDbTransientErrorClassifier`.
3. **Each writing step (Q3 table: orders 19, 20, 30, 40, 50, 60, 100, 110)** — wrap its
   `UpdateAsync(saveChanges:true)` (and `InsertAsync(saveChanges:true)`) in its **own**
   `RequiresNew` short UoW that commits immediately, mirroring `AsyncTransitionStrategy.SetInstanceBusyAsync`
   (`innerUow = BeginAsync(RequiresNew)` → `UpdateAsync(...,false)` → `innerUow.CommitAsync`). Inject
   `IUnitOfWorkManager` where steps don't already have it.
4. **Remote-task steps (30/40/60)** — ensure `taskCoordinator.ExecuteWithDetailsAsync(...)` runs with
   **no** ambient UoW open (call it before opening the short UoW that persists the post-task instance
   update). Task results already persist in child scopes (Q4).
5. **Arm checkpointing** — after each step's short-UoW commit, `instance.SetResumePoint(step.Order)`
   persisted in the same short UoW (the read path at `TransitionExecutor.cs:55-59` is already there;
   `ClearResumePoint` at Finalize already there).
6. **Verify Aether connection timing** (Q1 open question) — confirm short UoWs that only read don't pin a
   connection.

**Risks:**
- Loss of cross-step atomicity (intended) — a crash mid-chain leaves committed partial progress;
  relies entirely on resume + `successfulTaskIds` + ChainReaper. Non-idempotent OnExit/OnEntry side
  effects that aren't task-journalled could double-execute on resume.
- Per-step UoW under held lock is safe **only if** lock TTL > step duration — long remote steps need
  `ExtendAsync` coverage.
- Aether connection-acquisition timing (Q1) — if a transaction opens on read, the pool-pinning win is
  reduced.
- ChainReaper faults (does not re-drive) stuck chains — partial progress that can't resume becomes a
  fault, not a silent hang. Acceptable, but a behaviour change for some failure modes.

**Rollback:** the change is contained to `TransitionRunner` (UoW boundary) + per-step UoW wrapping.
Reverting `ExecuteWithScopeAsync` to a single outer `RequiresNew`+`CommitAsync` and restoring the
whole-scope Polly wrapper fully restores today's behaviour; the dormant `SetResumePoint` calls are inert
when a single UoW is used (resume point would simply be cleared at Finalize as today).

---

## Intentional behaviour changes (characterization tests must NOT pin these)

These are **today's** behaviours that Option B will deliberately change — the characterization tests
assert the surviving **invariant**, not the soon-to-change detail:

- **Atomicity:** today one commit at end-of-chain; under B, per-step commits. Tests must NOT assert
  "exactly one `UpdateAsync` is durable only at the end" — assert the **final-state invariant** instead.
- **Busy durability timing:** today Busy is durable only at final commit; under B it is durable
  immediately. Tests must NOT assert Busy is invisible mid-chain.
- **Faz 2 retry granularity:** today whole-scope; under B per-step. Tests must NOT assert the whole
  pipeline re-runs on a transient fault.

## Characterization coverage (this gate)

File: `test/BBT.Workflow.Application.Tests/Execution/Transitions/TransactionBoundaryCharacterizationTests.cs`
(step/pipeline level, NSubstitute + Shouldly, matching `RunOnExecuteTasksStep` & pipeline harness).

Locked invariants (must survive the refactor):
1. **Successful transition final-state invariant** — running OnExecute then ChangeState leaves the
   instance in the target state, the transition/task path having been invoked. (`RunOnExecuteTasksStep`
   invokes the coordinator and persists; `ChangeStateStep` changes state + persists.)
2. **No falsely-completed state on failure** — when a remote task step fails, the instance is NOT left
   Completed; the boundary/fault path is taken.
3. **Remote tasks are invoked during the flow** — OnExecute/OnExit/OnEntry each call
   `taskCoordinator.ExecuteWithDetailsAsync`.
4. **Resume skips already-successful tasks** — `RunOnExecuteTasksStep` calls
   `instanceTaskRepository.GetSuccessfulTaskIdsAsync(transitionId)` and passes the result as the bypass
   set to the coordinator (idempotency on re-run).

Deferred to integration: real Npgsql connection-pinning behaviour, real Aether UoW commit timing, true
crash-then-resume across process restarts, and ChainReaper end-to-end sweep — these need a DB and are
out of scope for unit characterization.
