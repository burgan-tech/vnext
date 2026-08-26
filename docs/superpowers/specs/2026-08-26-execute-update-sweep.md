# ExecuteUpdate sweep — set-based write conversion candidates

**Date:** 2026-08-26 · **Scope:** `src/BBT.Workflow.Infrastructure`, `src/BBT.Workflow.Application`, `workers/`, `monitoring/` — every EF-touching write path.

**Question answered:** which `SELECT + tracked mutate + SaveChanges` writes can become a single
`ExecuteUpdateAsync` (the pattern `EfCoreInstanceJobRepository.MarkAsProcessedAsync` now uses), and
which must not.

---

## Cross-cutting facts the classification rests on

### Audit columns are written by Aether's SaveChanges interceptor — ExecuteUpdate bypasses it

`BBT.Aether.Domain.EntityFrameworkCore.Interceptors.AuditInterceptor` (registered by
`AddAetherNpgsql`/`AddAetherDbContext`, not by this repo) is the **sole writer** of
`CreatedBy` / `CreatedByBehalfOf` / `ModifiedBy` / `ModifiedByBehalfOf` — nothing in `src/` assigns
them, yet `docs/domain/role-grant-authorization.md` depends on them. Any conversion must set the
entity's audit columns explicitly. Per-entity matrix:

| Entity | Audit columns present |
|---|---|
| `Instance` | full set + computed `LastTouchedAt` (`COALESCE(ModifiedAt, CreatedAt)`) — keep `ModifiedAt` fresh or `LastTouchedAt` stops advancing |
| `InstanceCorrelation` | full set |
| `InstanceTransition` | Created* only — no Modified* |
| `InstanceJob` | `CreatedAt` + `ModifiedAt` only — **no ModifiedBy** (the existing `MarkAsProcessedAsync` conversion therefore lost nothing) |
| `InstanceTask` | `CreatedAt` only |
| `InstanceAction` | `CreatedAt` only |

### Other invariants

- **Concurrency tokens: none anywhere** in the model — no rowversion/xmin. Not a blocker for any site.
- **Domain events:** `Instance` is the only event-raising entity (`AddDistributedEvent` in
  `Complete`, `Fault`, `Cancel`, `ChangeState`, `PropagateEffectiveStateToParent`). Events are
  collected during SaveChanges and routed to the outbox — **an ExecuteUpdate on `Instances`
  silently drops them.** Non-raising mutations (convertible in principle): `Busy()`, `Active()`,
  `ArmLongPollAck`, `ClearLongPollAck`, `SetEffectiveState`, `SetStage`, `AddTags`.
- **Data sinks:** `EfCoreInstanceRepository` / `EfCoreInstanceTaskRepository` /
  `EfCoreInstanceTransitionRepository` fan `UpdateAsync` out to `IDataSinkManager`. No concrete
  sink is registered in the repo today, so this is a contract-level concern — but
  `EfCoreInstanceRepository.UpdateAsync` **also emits the status-change metric** from the change
  tracker, and that IS live (a conversion must emit it at the call site).
- **Multi-schema:** all repos resolve the schema-bound context through
  `IAetherDbContextProvider` + per-schema compiled models — `ExecuteUpdateAsync` through
  `GetDbSetAsync()` inherits the right schema everywhere.
- **Deletes:** zero delete write-paths exist in the repo (`ExecuteDeleteAsync` has no applicable site).
- **Workers/monitoring:** Outbox/Inbox/job-store writes live inside Aether packages (out of reach);
  `monitoring/` is entirely read-only.

---

## Existing set-based precedents

| Site | Shape |
|---|---|
| `EfCoreInstanceJobRepository.MarkAsProcessedAsync` | the reference conversion (`IsActive` + `ModifiedAt`, WHERE matches the partial index) |
| `EfCoreInstanceTransitionRepository.UpdateCompletedAsync` | ExecuteUpdate — **defective, see F1** |
| `InstanceDataWriteService` (demote-stale-latest) | raw `UPDATE ... SET "IsLatest"=FALSE` |

---

## Defects found by the sweep (fix regardless of conversions)

### F1 — `UpdateCompletedAsync` writes 3 of the 6 columns its caller mutates, and the row is written twice

`EfCoreInstanceTransitionRepository.UpdateCompletedAsync` sets only `ToState`, `FinishedAt`,
`Duration`; `InstanceTransition.Completed(...)` also mutates `EffectiveState`,
`EffectiveStateType`, `EffectiveStateSubType`, `Stage`. Those four survive **only** because
`FinalizeTransitionStep` loaded the entity tracked and a later flush writes them — a hidden
dependency on tracking, plus **two UPDATEs on the same `InstanceTransitions` row on every
transition** (the hottest write in the product). If the load ever becomes `AsNoTracking`, four
columns are silently lost; the existing test asserts only the three written ones.

**Fix:** add the four missing `SetProperty` calls (self-sufficient statement) and switch
`FinalizeTransitionStep`'s load to `AsNoTracking` (the entity mutation only feeds the statement's
parameters) → one UPDATE per transition. Pin all six columns in the test.

### F2 — `GetResultAsync(includeDetails: false)` ignores its flag

`EfCoreInstanceRepository.GetResultAsync` takes `includeDetails` and never uses it — every call
loads the full aggregate (`DataList` + `ChildCorrelations`, 3 split queries).
`InstanceBusyManager.MarkBusyAsync` / `TryReleaseAsync` pass `false` and still pay the full load,
under the instance status lock. (Superseded on those two paths if C2 below lands; the flag should
be honored regardless.)

---

## Conversion candidates (by call frequency)

### C1 — `InstanceTask` completion write (hottest: once per executed task)

`StandardTaskPersistenceStrategy.HandleCompletionAsync` attaches a **detached** entity and
full-row-updates every column — including the `Request`/`Response`/`InvocationResult` jsonb — in
its own RequiresNew UoW. Replace with a repo method
`MarkCompletedAsync(id, response, status, businessStatus, finishedAt, duration)` doing one
`ExecuteUpdateAsync` over exactly the columns `InstanceTask.Completed()`/`Faulted()` mutate
(`Status`, `BusinessStatus`, `Response`, `FinishedAt`, `Duration`). Timestamps are passed from the
already-computed entity values so row and object agree. No audit columns to preserve.

### C2 — `Instance` Busy⇄Active flips as compare-and-set (shrinks the status-lock hold)

`InstanceBusyManager` (4 sites, ~2 per transition, under the distributed status lock) loads the
aggregate to write the single `Status` column. `Busy()`/`Active()` raise **no events** — the only
Instance mutations for which conversion is legal. The simple flips fold the guard into the WHERE:

```csharp
var affected = await dbSet
    .Where(i => i.Id == instanceId && i.Status == expected)
    .ExecuteUpdateAsync(s => s
        .SetProperty(i => i.Status, next)
        .SetProperty(i => i.ModifiedAt, DateTime.UtcNow), ct);
return affected == 1;
```

The load disappears entirely for `MarkBusyAsync`/`TryReleaseAsync`; the propagation variants keep
their (narrower) load for the correlation walk but write set-based. Decisions to make explicit:
`ModifiedBy` is not re-stamped (system operation; matches the `MarkAsProcessedAsync` precedent) —
inject `ICurrentUser` if the team wants it; the status-change metric currently emitted inside
`EfCoreInstanceRepository.UpdateAsync` must be emitted at the call site. Verify EF translates
`SetProperty(Status, ...)` through `InstanceStatusConverter`.

### C3 — cancellation loops: N tracked updates → one `WHERE Id IN (...)`

`InstanceCancellationService` closes jobs one tracked entity at a time after the per-job
`CancelWaitingAsync` verdicts. Collect the winning ids, then a new
`IInstanceJobRepository.MarkManyAsProcessedAsync(ids)` — one statement, `AND IsActive = true` for
idempotency and index alignment. Runs on every terminal transition, every scheduled-state exit,
and **every long-poll acknowledge**.

### C4 — `ArmLongPollAck` single-column write (low)

`HandleLongPollTerminationStep` saves the whole aggregate to write one `uuid` column. Convert to
`SetProperty(LongPollAckToken) + ModifiedAt`. Caveat: the current `autoSave: true` also flushed
the pipeline's pending changes; the immediately following job insert flushes anyway — verify.

### C5 — `ScheduleTransitionsStep`: N×SaveChanges → one flush (insert batching, not ExecuteUpdate)

One `InsertAsync(autoSave: true)` per timer ⇒ N round trips; switch to `autoSave: false` and one
flush at step end (the ambient UoW commit already exists).

---

## Not convertible (blocking reason)

- **Every event-raising `Instance` mutation** — `ChangeStateStep`, `HandleFinishStep`,
  fault paths (`TransitionPipeline`, `JobTimeoutRecoveryService`, `PostCommitParentMutationService`),
  all `Subflow*Service` correlation/fault/cancel writes: events ride SaveChanges → outbox;
  ExecuteUpdate drops them. Several also write owned collections/navigations (incidents,
  correlations) or multi-column conditional sets.
- **Script-driven mutations** — the `RunOn*` steps' instance saves after task-output merges,
  `Mutations.ApplyTo(instance)` in `StartSubflowJobHandler`: arbitrary column sets unknown at
  compile time.
- **Flushes the pipeline relies on** — `SetBusyStep` (rare safety net) and
  `TransitionSettlement.Active()` write through the live pipeline aggregate whose pending changes
  the same SaveChanges carries.
- **`CreateTransitionRecordStep` retry-path full-row write** — intended (re-mapped jsonb body).
- **Aether-internal writes** (outbox/inbox/job store) and **monitoring** (read-only).

---

## Verification plan

1. Unit: C1 column set; F1 six-column pin + single-UPDATE assertion after the AsNoTracking switch;
   C2 CAS return values (`Active→Busy` succeeds, `Busy→Busy` affected=0) + explicit status metric;
   C3 IN-list idempotency.
2. Suites compared name-by-name against a stashed-baseline run (the established method) — zero new
   failures.
3. Measurement: UPDATE counts per transition on `InstanceTransitions`/`InstanceTasks`
   (`pg_stat_statements`) before/after; admission lock-hold p50 after C2.
