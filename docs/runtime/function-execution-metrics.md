# Function Execution Metrics

A journal of domain-function invocations and the paged endpoints that read it
(vnext-client-sdk-core#60, items C1 and D). It answers "is this function slow, is it erroring" for the
functions a domain authors — the ones listed in `sys-catalog → Functions`.

A function is **not** record-based: one `sys-functions` key runs across many instances, across flows,
and domain-scoped inside no record at all. So its telemetry is not an attempts model (that is the
[transition/state metrics](transition-and-state-metrics.md)); it is a **paged execution series**.

## What is recorded — and what is not

**Opt-in per function.** Recording is off by default. A function is journaled only when its definition
declares `executionLog: ENABLED`; absent or `DISABLED`, nothing is recorded. This is deliberately
non-breaking — every existing definition, and any authored without the field, keeps the no-logging
behaviour — and it keeps the table to the functions a domain actually wants to watch rather than every
GET/POST/PATCH. The field is the value object `ExecutionLogSetting` (`ENABLED`/`DISABLED`), authored on
the function definition (`vnext-schema` `function-definition.schema.json`), and gates the journal only —
OTel/APM tracing (the `Function.Execute` span) is always emitted regardless.

**Recorded: opted-in domain-registered functions only.** Of the invocations that run through
`FunctionAppService.ExecuteFunctionAsync` — the functions in the `sys-functions` registry — only those
with `executionLog: ENABLED` are journaled. The gate and the write sit in that one method, so the scope
is enforced structurally: the runtime's built-in instance read functions (`state`, `data`, `view`,
`schema`, `tasks`, `actions`, `hierarchy`, …) are served by their own handlers and never reach it, so
they are never journaled here. Their telemetry lives in APM (the
`Instance.Read/{kind}` and `Function.Execute` spans) — which is where the team decided the built-in
read firehose belongs ("elk var diye"). Journaling `state`, the highest-QPS route, would swamp the
table and tax the hottest path for data a monitor does not show.

**Not recorded: the tasks a function runs internally.** `InstanceTasks` is keyed by a transition; a
function has no transition, and function-origin tasks are deliberately not persisted
(`ExtensionTaskPersistenceStrategy` is a no-op). One journal row = one function invocation, which is
what the D endpoint's item shape wants.

## The table

`FunctionExecution` lives in the fixed **`sys_metrics`** schema (its own `MetricsDbContext`, migrated by
a dedicated `MigrateMetricsDbContext()` call) — domain-wide, not per-flow, the same posture as the
messaging tables in `sys_queues`. A function can run domain-scoped with no flow, so `Workflow` and
`InstanceId` are **nullable** columns, populated only for flow/instance-scoped calls — the API's
`scope` D/F/I is a column, not a note.

Columns: `Id`, `Domain`, `FunctionKey`, `FunctionVersion`, `Scope` (D/F/I), `Workflow?`,
`InstanceId?`, `InvokedAt`, `DurationMs`, `Succeeded`, `StatusCode?`, `ErrorCode?`, `FromCache`,
`TraceId?` (the OTel trace id captured from `Activity.Current`, so a row links to its full trace in
APM/ELK), plus `CreatedBy`/`CreatedByBehalfOf` (the caller — the `invokedBy`/`invokedByBehalfOf`). One
index, `(FunctionKey, InvokedAt)`, serves the only access pattern: one function's runs, newest first,
bounded by a window.

Because the write is now asynchronous (below), the caller can no longer be audit-stamped by Aether's
interceptor — the request `ICurrentUser` is gone by save time. So `FunctionExecution` deliberately does
**not** implement `ICreationAuditedObject`; the caller is captured on the request scope
(`ActorUserName` → `CreatedBy`, `UserName` → `CreatedByBehalfOf`, the same mapping the interceptor
would have applied) and set explicitly, and `CreatedAt` is set to `InvokedAt` so it reflects the
invocation, not the later save.

**Retention:** rows are kept — there is no cleanup or TTL job in this phase (a deliberate first-cut
decision). The index keeps range queries cheap as the table grows; a retention job is a separable
follow-up.

## The write — asynchronous, best-effort, off the caller's path

For an opted-in function, one row is produced per `ExecuteFunctionAsync` call — but the function path
never writes it. The captured facts are **enqueued** on a bounded in-process queue
(`FunctionExecutionJournal`, a singleton `System.Threading.Channels` channel) via a non-blocking
`TryWrite`, and a `BackgroundService` consumer (`FunctionExecutionJournalWriter`) drains the queue in
batches and bulk-inserts each batch on its own short-lived DI scope, inside a `RequiresNew`
non-transactional unit of work. So the durable write happens entirely off the request path: the
function's execution time is unaffected, which is the hard requirement.

Journaling is **best-effort by design** — 100% completeness is explicitly not required:

- **Non-blocking producer.** The queue is bounded (default 50 000). `TryWrite` never blocks; when the
  queue is full the newest record is dropped and counted (the writer reports the running total via a
  throttled `WorkflowLogs` warning, off the hot path). Under sustained load the runtime sheds telemetry
  rather than becoming backpressure on function execution. **Using Dapr's scheduler for this was a
  deliberate non-choice** — per-invocation journaling stays in-process to keep high-volume telemetry off
  the scheduler; an in-process channel has no such external dependency in the write path.
- **Failures swallowed.** A batch persistence failure is logged and swallowed so the writer loop
  survives a transient DB fault; it never surfaces to the function's caller.
- **Graceful drain on shutdown.** `StopAsync` completes the queue and the writer flushes whatever
  remains before exiting, so an orderly shutdown does not silently lose already-enqueued rows.

### Tuning (`Workflow:FunctionExecutionJournal`)

Two knobs, both in `FunctionExecutionJournalOptions`:

| Setting | Default | Effect | Reloadable? |
|---|---|---|---|
| `BatchSize` | 1000 | Rows per `SaveChanges`. The throughput lever — fewer, larger batches mean fewer DB round-trips. Measured locally: batch 200 ≈ 17k rows/s (round-trip bound), batch ≥500 ≈ 28–30k rows/s (DB-bound plateau). | **Yes** — re-read each drain cycle via `IOptionsMonitor`, so a config change applies to the next batch with no restart. |
| `QueueCapacity` | 50000 | Burst absorption (~10 MB worst case at ~200 B/row). Not throughput — a shock absorber ahead of the drain. | **No** — a bounded channel's bound is fixed at creation, so a change applies only after a process restart. |

"Reloadable" holds only when the deployment delivers config as a **reloadable file** (a mounted appsettings file with `reloadOnChange`, which `WebApplication.CreateBuilder` enables by default). If config is injected as **environment variables**, both settings are process-fixed and a change needs a pod restart — env-var config is never hot-reloaded by .NET. Watch `WorkflowLogs` **event 80008** (records dropped) as the signal that you are at the ceiling and should raise `BatchSize` (then `QueueCapacity` for burstier traffic).

`FromCache` is true when the read-through cache served the response (its tasks were skipped). Every
outcome is enqueued exactly once via a `try/finally`: a success, a `Result.Fail` (auth/verb rejection,
validation failure, task failure — all attempted invocations, `Succeeded = false` with their
`ErrorCode`), and a **thrown** exception (still an erroring execution — `Succeeded = false`, `ErrorCode`
= the exception type — then rethrown to the caller). So the failure-rate counts thrown failures too, not
only `Result.Fail`. When a function is **not** opted in, the gate is checked before any of this
telemetry work runs, so an un-opted function pays nothing.

`Succeeded` + `StatusCode` + `ErrorCode` are the outcome. A function has **no** task-style
`businessStatus`, so — unlike the issue's draft item shape — the journal carries none. The D item
exposes `succeeded` (bool), `statusCode` and `error`, plus a derived `status` string
(`"completed"`/`"faulted"`) that mirrors the task-metrics `status` vocabulary so a client reads one
grammar across both surfaces; only the second `businessStatus` axis is deliberately absent.

## The endpoints (D)

```
GET /{domain}/functions/{functionKey}/metrics?page&pageSize&from&to&succeeded
GET /{domain}/workflows/{workflow}/functions/{functionKey}/metrics   // flow-scoped sibling
  → { links:{self,first,next,prev},
      items:[ { executionId, functionVersion, invokedAt, durationMs, scope, workflow?, instanceId?,
                succeeded, status, statusCode?, error?, fromCache, traceId?,
                invokedBy?, invokedByBehalfOf? } ],
      summary: { count, p50Ms, p95Ms, failureRate } }
```

The paging envelope is the **same** `{ links, items }` as the instance queries (one grammar for the
client). `summary` aggregates the whole filtered window — `count`, `p50`/`p95` latency
(`percentile_cont` in one raw statement) and `failureRate` — the "slow or erroring" read. The list
fetches one row beyond the page to decide `next` without a separate count. Like the other read
surfaces there is no in-process `queryRoles` gate (the gateway asks `authorize`).

## Implementation map

- Entity + repository contract: `FunctionExecution`, `IFunctionExecutionRepository`
  (`src/BBT.Workflow.Domain/Metrics/`).
- Context + repository: `MetricsDbContext` (`sys_metrics`), `EfCoreFunctionExecutionRepository`
  (paged LINQ read + raw-ADO `percentile_cont` summary), migration `Migrations/MetricsDb/`.
- Opt-in field: `ExecutionLogSetting` value object + `Function.ExecutionLog` /
  `Function.ExecutionLoggingEnabled` (`src/BBT.Workflow.Domain/Definitions/Functions/`); contract in
  `vnext-schema` `function-definition.schema.json`.
- Write (producer): `IFunctionExecutionJournal` / `FunctionExecutionJournal` (bounded channel), enqueued
  from `FunctionAppService.ExecuteFunctionAsync` (gated on `ExecutionLoggingEnabled`).
- Write (consumer): `FunctionExecutionJournalWriter` (`BackgroundService`) → batched
  `IFunctionExecutionRepository.InsertBatchAsync`; registered in the Orchestration host's
  `AddHostedServices` (the only host that executes domain functions).
- Read: `IFunctionMetricsAppService` / `FunctionMetricsAppService`; controller routes on
  `FunctionController`.
- No state-function involvement: no `ResponseShapeVersion` or fingerprint change.
