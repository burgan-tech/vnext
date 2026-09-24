# Function Execution Metrics

A journal of domain-function invocations and the paged endpoints that read it
(vnext-client-sdk-core#60, items C1 and D). It answers "is this function slow, is it erroring" for the
functions a domain authors — the ones listed in `sys-catalog → Functions`.

A function is **not** record-based: one `sys-functions` key runs across many instances, across flows,
and domain-scoped inside no record at all. So its telemetry is not an attempts model (that is the
[transition/state metrics](transition-and-state-metrics.md)); it is a **paged execution series**.

## What is recorded — and what is not

**Recorded: domain-registered functions only.** Exactly the invocations that run through
`FunctionAppService.ExecuteFunctionAsync` — the functions in the `sys-functions` registry. The write
sits in that one method, so the scope is enforced structurally: the runtime's built-in instance read
functions (`state`, `data`, `view`, `schema`, `tasks`, `actions`, `hierarchy`, …) are served by their
own handlers and never reach it, so they are never journaled here. Their telemetry lives in APM (the
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
APM/ELK), plus the audited `CreatedBy`/`CreatedByBehalfOf` (the caller — audit-stamped from the
request `ICurrentUser`, so they are the `invokedBy`/`invokedByBehalfOf`). One index, `(FunctionKey,
InvokedAt)`, serves the only access pattern: one function's runs, newest first, bounded by a window.

**Retention:** rows are kept — there is no cleanup or TTL job in this phase (a deliberate first-cut
decision). The index keeps range queries cheap as the table grows; a retention job is a separable
follow-up.

## The write — best-effort, off the caller's outcome

One row per `ExecuteFunctionAsync` call, written after the work completes, inside its own
`RequiresNew` non-transactional unit of work (a function is a read and may carry no committing ambient
scope — this mirrors the `InstanceTask` journal writer). Journaling is **best-effort**: a write
failure is logged and swallowed, never surfaced to the function's caller. `FromCache` is true when the
read-through cache served the response (its tasks were skipped). Every outcome is recorded exactly
once via a `try/finally`: a success, a `Result.Fail` (auth/verb rejection, validation failure, task
failure — all attempted invocations, `Succeeded = false` with their `ErrorCode`), and a **thrown**
exception (still an erroring execution — `Succeeded = false`, `ErrorCode` = the exception type — then
rethrown to the caller). So the failure-rate counts thrown failures too, not only `Result.Fail`.

`Succeeded` + `StatusCode` + `ErrorCode` are the outcome. A function has **no** task-style
`businessStatus`, so — unlike the issue's draft item shape — the journal carries none. The D item
exposes `succeeded` (bool), `statusCode` and `error`, plus a derived `status` string
(`"completed"`/`"faulted"`) that mirrors the task-metrics `status` vocabulary so a client reads one
grammar across both surfaces; only the second `businessStatus` axis is deliberately absent.

**Why the write is synchronous.** The journal insert is awaited inline (not fire-and-forget) because
the writer runs in the request DI scope — a detached background write would race the scope's disposal
of `MetricsDbContext`. The cost is one indexed insert on a dedicated connection, per domain-function
call — and domain functions already do task/HTTP work in the tens of milliseconds, so it is
negligible against that, and it is off the hot path entirely (the high-QPS built-in reads never reach
this method). If a domain ever drives enough function volume for that insert to matter, the escape
hatch is a batched/async writer behind the same `IFunctionExecutionJournal` seam — no caller change.

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
- Write: `IFunctionExecutionJournal` / `FunctionExecutionJournal`, invoked from
  `FunctionAppService.ExecuteFunctionAsync`.
- Read: `IFunctionMetricsAppService` / `FunctionMetricsAppService`; controller routes on
  `FunctionController`.
- No state-function involvement: no `ResponseShapeVersion` or fingerprint change.
