# Task Executors and Invokers

## Purpose

Task execution is split into executors and invokers. Executors always run inside
Orchestration and understand workflow context. Invokers execute the typed binding and perform
the external call — **where an invoker runs is mode-driven, not fixed**: five wire task types
(`http`, `daprservice`, `soap`, `statestore`, `cacheaside`) run through an in-process invoker
inside Orchestration by default, and everything else still ships to the Execution service.
See [Task Invocation Routing](task-invocation-routing.md) for the resolution order, the
config, and what the local path trades away.

## Boundaries

| Concept | Runs in | Responsibility |
| --- | --- | --- |
| Task executor | Orchestration/Application | Resolve workflow task definition, build binding, hand it to `ITaskInvocationDispatcher`. |
| Invocation router/dispatcher | Orchestration/Application | Per call, decides Local vs. Remote (`ITaskInvocationRouter`) and either runs a local invoker or forwards through `IRemoteInvokerService` (`ITaskInvocationDispatcher`). See [Task Invocation Routing](task-invocation-routing.md). |
| Local task invoker | **Orchestration**/Application (`Tasks/Invocation/Local/`) | For the five locally-routed types: executes the typed binding in-process against the same shared cores (`HttpTaskInvocation`, `StateStoreInvocation`, …) the Execution invokers use. |
| Task envelope | Execution abstractions | Stable request contract between Orchestration and Execution — unchanged whether or not a given call actually crosses the wire. |
| Task invoker | **Execution** | Executes typed binding and returns invocation result, for any type still resolved Remote. |
| Invoker registry | Execution | Routes `TaskType` to the correct remote invoker. |

## Architecture Flow

1. Pipeline reaches an OnExecute, OnExit, or OnEntry step.
2. Step asks `ITaskExecutorRegistry` for the task executor.
3. Executor evaluates workflow context and task configuration.
4. Executor either performs local (Orchestration-owned) work directly, or creates a typed
   binding and calls `ITaskInvocationDispatcher.DispatchAsync`.
5. The dispatcher asks `ITaskInvocationRouter` to resolve Local vs. Remote for this call
   (see [Task Invocation Routing](task-invocation-routing.md) for the resolution order), then:
   - **Local**: runs the matching `ILocalTaskInvoker` in-process and returns its result.
   - **Remote**: `IRemoteInvokerService` sends the `TaskEnvelope` to Execution over Dapr
     service invocation; Execution's controller calls `ITaskInvokerRegistry`, its invoker
     performs the side effect, and the result travels back the same way it always did.
6. Executor maps the result back into pipeline data or error handling — identical either way;
   the executor and everything downstream of the dispatcher are unaware which path ran.

## Contracts

| Task family | Executor examples | Invoker examples |
| --- | --- | --- |
| HTTP/SOAP | `HttpTaskExecutor`, `SoapTaskExecutor` | Local by default: `LocalHttpTaskInvoker` / `LocalSoapTaskInvoker` (Orchestration). Falls back to `HttpTaskInvoker` / `SoapTaskInvoker` (Execution) if reconfigured Remote. See [Task Invocation Routing](task-invocation-routing.md). |
| External HTTP (type `22`, **deprecated**) | `ExternalHttpTaskExecutor` | None — `ExternalHttpTaskInvoker` runs the shared `HttpTaskInvocation` core in-process inside Orchestration (issue #399). Redundant since type `6` (HTTP) also runs local by default; see below. |
| Dapr service invocation | `DaprServiceTaskExecutor` | Local by default: `LocalDaprServiceTaskInvoker` (Orchestration). Falls back to the Execution-side Dapr service invoker if reconfigured Remote. |
| Dapr (pubsub/binding/conversation) | `DaprPubSubTaskExecutor`, `DaprBindingTaskExecutor`, `DaprConversationTaskExecutor` | Matching Dapr invokers — always Remote (Execution); no local invoker exists for these wire types. |
| State store | `StateStoreTaskExecutor` | Local by default: `LocalStateStoreTaskInvoker` (Orchestration), sharing `IStateStoreClient` with the function response cache. Falls back to `StateStoreTaskInvoker` (Execution) if reconfigured Remote. Dapr state store cache access ([details](state-store-task.md)) |
| Trigger | `StartTriggerTaskExecutor`, `DirectTriggerTaskExecutor`, `SubProcessTaskExecutor` | Remote trigger invokers |
| Data query | `GetInstancesTaskExecutor`, `GetInstanceDataTaskExecutor`, `GetInstanceTaskExecutor` (type `19`) | Remote data / instance invokers |
| Cache-aside | `CacheAsideTaskExecutor` (type `18`) | Local by default: `LocalCacheAsideTaskInvoker` (Orchestration); a source-task miss re-enters the same router. Falls back to `CacheAsideTaskInvoker` (Execution) if reconfigured Remote. Dapr state-store read-through ([details](cache-aside-task.md)) |
| Fan-out | `FanOutTaskExecutor` (type `21`) | Orchestrates inner tasks; see [FanOut Task](../domain/fan-out-task.md) |
| Script | `ScriptTaskExecutor` | Executes in Orchestration through scripting module |
| Python | `PythonTaskExecutor` | `PythonTaskInvoker` selects an explicit Python.NET, process, or container runtime ([details](python-task.md)) |
| Human/notification | Human and notification executors | May remain application-owned depending on side effect type |

## Failure Modes

- Unknown task type fails registry lookup.
- Invalid binding returns validation failure before external work.
- Remote Execution failure returns an invocation result that the executor maps into
  task/pipeline error handling.
- Non-blocking task failures are stored in pipeline context and finalized according to
  the task semantics.

## Observability

Execution controller begins a log scope with domain, workflow key, instance id, task key,
and task type from the trace context and envelope. Executors should preserve correlation
and task metadata when sending remote envelopes.

Python is the first task family with a second runtime registry behind its invoker. The
`PythonTaskInvoker` resolves exactly one configured `IPythonExecutionRuntime`; a disabled or
unavailable requested mode is a task failure and never causes a silent mode fallback.

## External HTTP tasks (type `22`, deprecated)

**As of this change, type `6` (the ordinary HTTP task) also runs locally by default** — see
[Task Invocation Routing](task-invocation-routing.md). That makes `ExternalHttpTask` (type
`22`) functionally redundant: both types now execute the same call, in the same host, through
the same shared send core, under the shipped configuration. Type `22` is **deprecated, not
removed** (`vnext-meta/deprecations.json`, id `external-http-task`) — existing definitions keep
working unchanged, but new definitions should use type `6`. The distinction that used to matter
— "runs in Orchestration vs. runs in Execution" — is now a router configuration decision, not a
task-type decision.

`ExternalHttpTask` (`type: "22"`, config identical to the type-6 HTTP task) is executed **directly by
the Orchestrator**: the executor flattens the task through the same `TaskBindingMapper` as the
remote path and hands the `HttpTaskBinding` to `ExternalHttpTaskInvoker`.
`/execution/invoke/{type}/{key}` is never called — this was true before this change and is
unaffected by it; type `22` never went through the router at all, it was always
unconditionally local.

Both HTTP task types run **one shared send implementation** — `HttpTaskInvocation` in
`BBT.Workflow.Execution.Abstractions` (referenced by Orchestration, Execution, and the local
invoker; it stays package-free by taking the named-client resolver as a `Func<string,
HttpClient>`). The Execution service's `HttpTaskInvoker` (type 6, remote path),
`LocalHttpTaskInvoker` (type 6, local path — the default) and the Orchestrator's
`ExternalHttpTaskInvoker` (type 22, always local) are thin wrappers adding host-specific
logging/metrics and, on the orchestrator side, the mapping to the orchestrator-side
`TaskInvocationResult` twin. Named clients (`validateSsl: false` selects the SSL-bypass client,
and its `MaxConnectionsPerServer` cap — configurable via
`Workflow:TaskInvocation:MaxConnectionsPerServer`, default `50` — is shared by every HTTP/SOAP
egress path from Orchestration, type 22 included), header/Content-Type semantics, response
parsing and accepted-status-code matching are therefore identical by construction across all
three.

Historical trade-offs of type `22` over type `6` (now equally true of type `6` under its
default Local configuration — see
[What the local path loses](task-invocation-routing.md#what-the-local-path-loses)):

- No Dapr hop means no sidecar circuit breaker and no `ExecutionApi:InvocationTimeoutSeconds`
  layer. **The replacement bound differs between the two types, and this is the one place they are
  not equivalent:** a local type `6` goes through `TaskInvocationDispatcher`, which wraps every
  in-process call in `Workflow:TaskInvocation:LocalInvocationTimeoutSeconds` (default 60s), so its
  layering is `timeoutSeconds ⊂ LocalInvocationTimeoutSeconds ⊂ job budget`. Type `22` does **not**
  go through the dispatcher — `ExternalHttpTaskExecutor` calls `IExternalHttpTaskInvoker` directly
  — so for it the task's own `timeoutSeconds` (default 30) really is the only bound below the job
  budget. See [Timeout layering](task-invocation-routing.md#timeout-layering).
- The outbound call runs in the host that owns the database; the Execution service exists
  precisely to isolate arbitrary egress. For untrusted or high-volume targets, revert type `6`
  to `Remote` per-type (see [Task Invocation Routing](task-invocation-routing.md)) rather than
  reaching for type `22`, which has no such switch.
- `ExternalHttpTask` derives from `HttpTask`, so mapping scripts (`task as HttpTask`, `SetUrl`,
  `SetHeaders`, `SetBody`) work unchanged for both types.
- Behavioral parity is by construction: shared binding mapper + the single shared send core
  (`HttpTaskInvocation`); there is no second HTTP implementation to keep in sync.

## Change Safety

- A new **remote** task type needs definition, executor, binding contract, invoker, registry
  registration, and tests for envelope routing.
- A new **orchestrator-executed** task type (like Script, Notification or External HTTP) needs
  definition, executor, `[JsonDerivedType]` discriminator, `TaskType` enum member, executor
  registration, and — if it declares `acceptedStatusCodes` — a match in
  `TaskExecutorBase.GetAcceptedStatusCodes` (External HTTP inherits the `HttpTask` arm).
- Keep bindings strongly typed; do not pass opaque JSON when a stable contract exists.
- Do not let invokers mutate workflow instance state.
- Keep task result mapping in executors or application services, not in Execution host controllers.

## References

- `src/BBT.Workflow.Domain/Tasks/Executors/Core/ITaskExecutor.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/`
- `src/BBT.Workflow.Application/Tasks/Invocation/` — router, dispatcher, local invokers (see [Task Invocation Routing](task-invocation-routing.md))
- `src/BBT.Workflow.Application/Tasks/Executors/Remote/RemoteInvokerService.cs`
- `src/BBT.Workflow.Execution.Abstractions/TaskEnvelope.cs`
- `execution/BBT.Workflow.Execution.HttpApi.Host/Controllers/Executions/ExecutionController.cs`
- `src/BBT.Workflow.Execution/Invokers/`
- `src/BBT.Workflow.Execution/Services/TaskInvokerRegistry.cs`
- `src/BBT.Workflow.Execution/Python/PythonRuntimeRegistry.cs`
