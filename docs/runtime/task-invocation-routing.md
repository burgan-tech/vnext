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

## Before you deploy this version

The default store name resolves fine: the Helm chart already emits `DAPR_STATE_STORE_NAME` for
the orchestrator and scopes the `state` component to it (see
[Dapr Component Footprint](dapr-component-footprint.md)). The one thing this change can break
silently is a **custom `storeName`**:

1. Enumerate every `StateStoreTask` / `CacheAsideTask` definition, and every function `cache`
   block, that sets an explicit `storeName` instead of leaving it to resolve from
   `DAPR_STATE_STORE_NAME`.
2. For each one, confirm that Dapr component's `scopes:` includes the **orchestrator** app id
   — not only the execution app id. `statestore`/`cacheaside` now run against the Orchestration
   sidecar by default, so a component scoped only to execution will resolve there under the old
   (Remote) routing but fail at first execution under the new (Local) default, with no
   publish-time or startup signal.
3. If a component cannot be rescoped before this version deploys, set
   `"statestore": "Remote"` and `"cacheaside": "Remote"` under
   `Workflow:TaskInvocation:Modes` (see [Reverting a type to Remote](#reverting-a-type-to-remote)
   below) until it can be — this keeps that store name working exactly as it did before this
   change, at the cost of losing the function-response-cache latency win for that store (see
   [Which types have a local invoker](#which-types-have-a-local-invoker)).

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

**A third key lives under the same section but is absent from the shipped file:**
`Workflow:TaskInvocation:MaxConnectionsPerServer` (int, `[Range(1, int.MaxValue)]`,
`TaskInvocationOptions.MaxConnectionsPerServer`). It is the per-target connection cap
(`HttpClientHandler.MaxConnectionsPerServer`) for the named HTTP clients shared by every
in-process `http`/`soap` task and by the (deprecated) type-`22` External HTTP task. Before
issue #1007 this was hardcoded at `10`, written only for type 22's traffic; now that type `6`
and `soap` also egress from Orchestration by default, it bounds all of that traffic, so the
shipped default moved to **`50`** — as a code default (`TaskInvocationOptions`), not an entry in
`appsettings.json`. An operator who needs a different cap sets
`Workflow:TaskInvocation:MaxConnectionsPerServer` explicitly; nothing needs to change in the
file otherwise. This value only ever applies to Orchestration's own named HTTP clients — the
Execution host's equivalent clients are a separate, still-hardcoded `10` (see
[What the local path loses](#what-the-local-path-loses) and Reverting a type to Remote below for
why that gap matters when reverting under load).

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

**The enforced hierarchy** — `WorkflowExecutionOptionsValidator` (see
`src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptionsValidator.cs`)
fails fast at options resolution if any of these three **options-sourced** values doesn't fit
inside the next, and this is unchanged by this feature:

```
ExecutionApi:InvocationTimeoutSeconds (60s)  ⊂  TransitionJobTimeoutSeconds (job budget, 300s)  ⊂  chain lock lease (330s)
```

The validator never reads a task definition's own `timeoutSeconds` — it has no visibility into
individual task config, only into the three host-level options above. What follows is a
**descriptive**, not enforced, picture of where a task's own `timeoutSeconds` sits relative to
that hierarchy, which differs by path:

**Remote path** — the task's own `timeoutSeconds` sits inside `ExecutionApi:InvocationTimeoutSeconds`
in practice (the Dapr call to Execution is bounded by that 60s regardless of what the task asks
for), which is itself inside the enforced hierarchy above:

```
task's own timeoutSeconds  ⊂  ExecutionApi:InvocationTimeoutSeconds (60s)  ⊂  TransitionJobTimeoutSeconds (job budget, 300s)  ⊂  chain lock lease (330s)
```

**Local path** — the 60s `ExecutionApi:InvocationTimeoutSeconds` layer does not exist between
the task and the job budget, because there is no second hop to bound, so the task's own
`timeoutSeconds` sits directly inside the job budget instead:

```
task's own timeoutSeconds (default 30s)  ⊂  TransitionJobTimeoutSeconds (job budget, 300s)  ⊂  chain lock lease (330s)
```

Neither diagram's outermost relationship (task `timeoutSeconds` vs. the next layer in) is
validator-enforced on either path — it holds because a task's own timeout is architecturally
the innermost clock, not because anything rejects a misconfigured value. A locally-run task
whose `timeoutSeconds` is configured at or above the job budget is **not** rejected at publish
time — deliberately. The plan that produced this page originally proposed a `WorkflowValidator`
rule to reject such a definition, and it was dropped: it would turn definitions that publish
cleanly today into publish-time errors, which this repo's no-breaking-change policy forbids,
and the harm it would prevent is minor — the job budget already cancels a task that outlives
it, exactly as it would on the remote path once the task outlives `InvocationTimeoutSeconds`
there. In practice a `timeoutSeconds` at or above the job budget is simply inert: the job budget
fires first and the task's own timeout never gets a chance to. `WorkflowExecutionOptionsValidator`
is intentionally untouched by this change.

## Observability: `vnext.task.invocation.mode`

`TaskInvocationDispatcher.DispatchAsync` tags the ambient task span with
`vnext.task.invocation.mode` set to the resolved `ExecutionMode` (`Local` or `Remote`) before
dispatching, so which path a given task invocation took is answerable from a trace instead of
from configuration archaeology. The decision's `Reason` (`task-override` / `type-config` /
`default` / `no-local-invoker`) is logged (`TaskInvokedLocally`) but is not currently a separate
span tag.

## Environment prerequisites

The local `statestore`/`cacheaside` invokers resolve their Dapr state store component through
`DAPR_STATE_STORE_NAME`, read from whichever host actually executes the task (see
[State Store Task](state-store-task.md)). Local dev and docker already supply this to the
Orchestration host — `launchSettings.json`, `etc/docker/.env.orchestration.dev`,
`etc/docker/.env.orchestration.stage` — so no `appsettings.json` change was needed for it. The
production Helm chart already supplies `DAPR_STATE_STORE_NAME` to the orchestrator and scopes
the `state` component to it — see
[Dapr Component Footprint](dapr-component-footprint.md#the-matrix) for the evidence-backed
per-host matrix and why. The only thing that can still be mis-scoped is a **custom** `storeName`
on an individual task or function cache — covered above under
[Before you deploy this version](#before-you-deploy-this-version).

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

**This takes effect on the next Orchestration host start, not on the next request.**
`TaskInvocationRouter` is constructed from `IOptions<TaskInvocationOptions>`, which binds once
and is cached for the lifetime of the process — there is no `IOptionsMonitor`/hot-reload wiring
here, deliberately: this configuration arrives as environment variables and ConfigMap entries,
and promising an end-to-end hot-reload path we cannot actually guarantee through that delivery
mechanism is worse than a plain instruction. **Editing the ConfigMap alone changes nothing in a
running pod.** After changing this value, roll the Orchestration deployment (a normal rolling
restart is sufficient — no special drain procedure) and confirm the new pods pick it up by
checking that a subsequent invocation's `vnext.task.invocation.mode` span tag now reads
`Remote` for that type. No code change and no redeploy of the **Execution** service are needed
either way.

To revert every type at once, set `DefaultMode` to `Remote` and remove the `Modes` entries (this
is already the shipped default for every type without an entry).

**Reverting `http`/`soap` to Remote under load also drops the per-target connection ceiling**
from `Workflow:TaskInvocation:MaxConnectionsPerServer` (default `50`, applies only to
Orchestration's own named HTTP clients) down to the Execution host's still-hardcoded `10` for
that same target, once the traffic starts flowing through Execution's named clients instead.
Reverting a hot single-target type back to Remote without also accounting for that drop can turn
a config rollback into a new throughput bottleneck.

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
- [Dapr Component Footprint](dapr-component-footprint.md) — per-host component matrix, including
  why Orchestration needs the `state` component for the platform cache and, since this change,
  for local `statestore`/`cacheaside` domain tasks by default
