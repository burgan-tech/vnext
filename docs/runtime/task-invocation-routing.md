# Task Invocation Routing (Local vs. Remote)

## Purpose

Every task with a prepared binding — HTTP, Dapr service invocation, SOAP, state store,
cache-aside, and any future type that gets an in-process invoker — can run two ways:

- **Local**: inside the Orchestration host, in the same process as the pipeline step that
  triggered it.
- **Remote**: shipped as a `TaskEnvelope` to the Execution service over Dapr service
  invocation, exactly as every task ran before issue #1007.

`ITaskInvocationRouter` decides per call which path a task takes. This page is the single
source for the resolution order, the shipped configuration, what the local path gains and
loses relative to the remote path, and how to revert a type to Remote.

## Resolution order

`TaskInvocationRouter.Resolve` (`src/BBT.Workflow.Application/Tasks/Invocation/TaskInvocationRouter.cs`)
answers first match wins, in this order:

1. **Task override hook** — a per-task-definition override (`config.executionMode`). This is a
   seam for a future vnext-schema release; `TryGetTaskOverride` always returns `null` today, so
   this step never actually decides anything yet. It exists so adding the field later never has
   to reshape the resolution order.
2. **Per-type host configuration** — `Workflow:TaskInvocation:Modes:{wireTaskType}` (see
   below), matched case-insensitively against the wire task type constants in
   `BBT.Workflow.Execution.TaskTypes`.
3. **Configured default** — `Workflow:TaskInvocation:DefaultMode`.
4. **Capability gate** — unconditional and always applied last: if the resolved mode is
   `Local` but no `ILocalTaskInvoker` is registered for that wire task type, the router
   degrades the decision to `Remote` instead of failing the task. The router never promises a
   local path it cannot perform.

The capability gate is why a configuration mistake (naming a type with no local invoker) is a
silent fallback to the pre-#1007 behavior, not an outage.

## Shipped configuration

`orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`, under the existing
`"Workflow"` root (alongside `InstanceFiltering` and `FanOut` — never a second `"Workflow"` key):

```json
"Workflow": {
  "TaskInvocation": {
    "DefaultMode": "Remote",
    "Modes": {
      "http": "Local",
      "daprservice": "Local",
      "soap": "Local",
      "statestore": "Local",
      "cacheaside": "Local"
    }
  }
}
```

`DefaultMode` stays `Remote`: only the five measured and tested types below are local. A task
type added to the runtime tomorrow with no entry in `Modes` runs Remote automatically — it does
not silently start performing egress from the orchestrator just by existing. Pinned by
`TaskInvocationDefaultsTests` (`test/BBT.Workflow.Application.Tests/Tasks/Invocation/`), which
binds this exact shipped file.

`ExecutionMode.Custom` is rejected at options-validation time (`TaskInvocationOptionsValidator`,
`ValidateOnStart`) for both `DefaultMode` and every `Modes` entry — it exists in the shared enum
for a plugin story that was never built, and would otherwise behave as an inert `Remote` with no
indication anything is wrong.

## Which types have a local invoker

| Wire type | Task type(s) | Local invoker | Notes |
| --- | --- | --- | --- |
| `http` | HTTP (type `6`) | `LocalHttpTaskInvoker` | Same `HttpTaskInvocation` core as the Execution service's `HttpTaskInvoker` and the (now redundant) type-`22` `ExternalHttpTaskInvoker`. See [Task Executors and Invokers](task-executors-and-invokers.md). |
| `daprservice` | Dapr service invocation (type `7`) | `LocalDaprServiceTaskInvoker` | Calls another domain app directly from Orchestration instead of relaying through Execution. |
| `soap` | SOAP (type `8`) | `LocalSoapTaskInvoker` | Shares the same named HTTP clients (and the same connection cap) as `http`. |
| `statestore` | State Store (type `17`) | `LocalStateStoreTaskInvoker` | Runs through `StateStoreInvocation` over the shared `IStateStoreClient`; also backs the function response cache (`StateStoreCacheGateway`). See [State Store Task](state-store-task.md). |
| `cacheaside` | Cache-Aside (type `18`) | `LocalCacheAsideTaskInvoker` | On a cache miss, dispatches the pre-resolved source-task envelope back through the same router/dispatcher — so a `http` source task run from a `cacheaside` task is itself subject to this table. See [Cache-Aside Task](cache-aside-task.md). |

Every other wire type (`daprbinding`, `daprhttpendpoint`, `daprpubsub`, `daprconversation`,
`python`, trigger/query types, …) has no local invoker today, so the capability gate always
resolves them to `Remote` regardless of configuration.

## What the local path loses

Running a task in-process is not free — it trades away two things the Dapr hop to Execution
used to provide:

1. **No Dapr sidecar circuit breaker.** The remote path's resiliency policy for the
   Orchestration → Execution hop is circuit-breaker-only (no retry, because retrying could
   re-invoke a task with side effects — see
   [Dapr Invocation Transport](dapr-invocation-transport.md)). A locally invoked task has no
   such breaker between it and its target: a failing/slow downstream is felt directly by the
   orchestrator's own thread pool and connection pool, not absorbed by a separate service's
   sidecar first.
2. **No `ExecutionApi:InvocationTimeoutSeconds` layer (60s).** On the remote path this sits
   between the task's own `timeoutSeconds` and the job execution budget (see Timeout layering
   below). The local path has no equivalent middle layer — see below.

Trade-offs for `http` specifically (shared with the now-deprecated type-`22` External HTTP
task) are covered in [Task Executors and Invokers](task-executors-and-invokers.md).

## Timeout layering

**Remote path** (unchanged, still enforced by `WorkflowExecutionOptionsValidator` — see
`src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptionsValidator.cs`):

```
task's own timeoutSeconds  ⊂  ExecutionApi:InvocationTimeoutSeconds (60s)  ⊂  TransitionJobTimeoutSeconds (job budget, 300s)  ⊂  chain lock lease (330s)
```

Each layer must fit inside the next; the validator fails fast at options resolution if it
doesn't.

**Local path** — the 60s `ExecutionApi:InvocationTimeoutSeconds` layer does not exist between
the task and the job budget, because there is no second hop to bound:

```
task's own timeoutSeconds (default 30s)  ⊂  TransitionJobTimeoutSeconds (job budget, 300s)  ⊂  chain lock lease (330s)
```

A locally-run task whose `timeoutSeconds` is configured at or above the job budget is **not**
rejected at publish time — deliberately. The plan that produced this page originally proposed a
`WorkflowValidator` rule to reject such a definition, and it was dropped: it would turn
definitions that publish cleanly today into publish-time errors, which this repo's
no-breaking-change policy forbids, and the harm it would prevent is minor — the job budget
already cancels a task that outlives it, exactly as it would on the remote path once the task
outlives `InvocationTimeoutSeconds` there. In practice a `timeoutSeconds` at or above the job
budget is simply inert: the job budget fires first and the task's own timeout never gets a
chance to. `WorkflowExecutionOptionsValidator` is intentionally untouched by this change.

## Observability: `vnext.task.invocation.mode`

`TaskInvocationDispatcher.DispatchAsync` tags the ambient task span with
`vnext.task.invocation.mode` set to the resolved `ExecutionMode` (`Local` or `Remote`) before
dispatching, so which path a given task invocation took is answerable from a trace instead of
from configuration archaeology. The decision's `Reason` (`task-override` / `type-config` /
`default` / `no-local-invoker`) is logged (`TaskInvokedLocally`) but is not currently a separate
span tag.

## Environment prerequisites and Helm follow-up

The local `statestore`/`cacheaside` invokers resolve their Dapr state store component through
`DAPR_STATE_STORE_NAME`, read from whichever host actually executes the task (see
[State Store Task](state-store-task.md)). Local dev and docker already supply this to the
Orchestration host — `launchSettings.json`, `etc/docker/.env.orchestration.dev`,
`etc/docker/.env.orchestration.stage` — so no `appsettings.json` change was needed for it.

**Helm follow-up (not done in this change):** the production Helm chart
(`vnext-helm-charts`, `charts/vnext`) is a separate repository this change does not touch. It
must supply `DAPR_STATE_STORE_NAME` to the Orchestration deployment (mirroring what it already
gives Execution) and expose the Orchestration pod's own state-store Dapr component scope, or the
`statestore`/`cacheaside` local invokers will fail to resolve a store name in that environment
even though the router correctly routes them Local. See
[Sibling repositories](../../AGENTS.md) for the `vnext-helm-charts` repo pointer.

## Reverting a type to Remote

Flip one entry in the shipped `appsettings.json` (or override it per environment):

```json
"Workflow": {
  "TaskInvocation": {
    "Modes": {
      "http": "Remote"
    }
  }
}
```

No code change, no redeploy of Execution required — the next request re-resolves the router
against the new configuration. To revert every type at once, set `DefaultMode` to `Remote` and
remove the `Modes` entries (this is already the shipped default for every type without an
entry).

## References

- `src/BBT.Workflow.Application/Tasks/Invocation/TaskInvocationOptions.cs`
- `src/BBT.Workflow.Application/Tasks/Invocation/TaskInvocationOptionsValidator.cs`
- `src/BBT.Workflow.Application/Tasks/Invocation/TaskInvocationRouter.cs`
- `src/BBT.Workflow.Application/Tasks/Invocation/TaskInvocationDispatcher.cs`
- `src/BBT.Workflow.Application/Tasks/Invocation/LocalTaskInvokerRegistry.cs`
- `src/BBT.Workflow.Application/Tasks/Invocation/Local/`
- `src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptionsValidator.cs`
- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`
- `test/BBT.Workflow.Application.Tests/Tasks/Invocation/`
- [Task Executors and Invokers](task-executors-and-invokers.md)
- [Dapr Invocation Transport](dapr-invocation-transport.md)
- [State Store Task](state-store-task.md), [Cache-Aside Task](cache-aside-task.md)
