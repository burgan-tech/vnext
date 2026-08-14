# Task Executors and Invokers

## Purpose

Task execution is split into executors and invokers. Executors run inside Orchestration
and understand workflow context. Invokers run inside Execution and perform stateless
external calls from strongly typed bindings.

## Boundaries

| Concept | Runs in | Responsibility |
| --- | --- | --- |
| Task executor | Orchestration/Application | Resolve workflow task definition, build binding, call local service or remote invoker. |
| Remote invoker service | Orchestration/Application | Sends task envelope to Execution. |
| Task envelope | Execution abstractions | Stable request contract between Orchestration and Execution. |
| Task invoker | Execution | Executes typed binding and returns invocation result. |
| Invoker registry | Execution | Routes `TaskType` to the correct invoker. |

## Architecture Flow

1. Pipeline reaches an OnExecute, OnExit, or OnEntry step.
2. Step asks `ITaskExecutorRegistry` for the task executor.
3. Executor evaluates workflow context and task configuration.
4. Executor either performs local work or creates a typed binding.
5. Remote invoker service sends `TaskEnvelope` to Execution.
6. Execution controller calls `ITaskInvokerRegistry`.
7. Invoker performs the side effect and returns `TaskInvocationResult`.
8. Executor maps the result back into pipeline data or error handling.

## Contracts

| Task family | Executor examples | Invoker examples |
| --- | --- | --- |
| HTTP/SOAP | `HttpTaskExecutor`, `SoapTaskExecutor` | `HttpTaskInvoker`, `SoapTaskInvoker` |
| External HTTP (type `21`) | `ExternalHttpTaskExecutor` | None — `ExternalHttpTaskInvoker` runs the shared `HttpTaskInvocation` core in-process inside Orchestration (issue #399) |
| Dapr | `DaprServiceTaskExecutor`, `DaprPubSubTaskExecutor`, `DaprBindingTaskExecutor` | Matching Dapr invokers |
| State store | `StateStoreTaskExecutor` | `StateStoreTaskInvoker` — Dapr state store cache access ([details](state-store-task.md)) |
| Trigger | `StartTriggerTaskExecutor`, `DirectTriggerTaskExecutor`, `SubProcessTaskExecutor` | Remote trigger invokers |
| Data query | `GetInstancesTaskExecutor`, `GetInstanceDataTaskExecutor` | Remote data invokers |
| Script | `ScriptTaskExecutor` | Executes in Orchestration through scripting module |
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

## External HTTP tasks (type `21`)

`ExternalHttpTask` (`type: "21"`, config identical to the type-6 HTTP task) is executed **directly by
the Orchestrator**: the executor flattens the task through the same `TaskBindingMapper` as the
remote path and hands the `HttpTaskBinding` to `ExternalHttpTaskInvoker`.
`/execution/invoke/{type}/{key}` is never called.

Both HTTP task types run **one shared send implementation** — `HttpTaskInvocation` in
`BBT.Workflow.Execution.Abstractions` (the only assembly both hosts reference; it stays
package-free by taking the named-client resolver as a `Func<string, HttpClient>`). The Execution
service's `HttpTaskInvoker` (type 6) and the Orchestrator's `ExternalHttpTaskInvoker` (type 21)
are thin wrappers adding host-specific logging/metrics and, on the orchestrator side, the mapping
to the orchestrator-side `TaskInvocationResult` twin. Named clients (`validateSsl: false` selects
the SSL-bypass client), header/Content-Type semantics, response parsing and accepted-status-code
matching are therefore identical by construction.

Trade-offs to be aware of when choosing type 21 over type 6:

- No Dapr hop means no sidecar circuit breaker and no `ExecutionApi:InvocationTimeoutSeconds`
  layer — the task's own `timeoutSeconds` (default 30) is the only bound below the job budget.
- The outbound call runs in the host that owns the database; the Execution service exists
  precisely to isolate arbitrary egress. Prefer type 6 for untrusted or high-volume targets and
  keep type 21 for calls that must not depend on the Execution service's availability.
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
- `src/BBT.Workflow.Application/Tasks/Executors/Remote/RemoteInvokerService.cs`
- `src/BBT.Workflow.Execution.Abstractions/TaskEnvelope.cs`
- `execution/BBT.Workflow.Execution.HttpApi.Host/Controllers/Executions/ExecutionController.cs`
- `src/BBT.Workflow.Execution/Invokers/`
- `src/BBT.Workflow.Execution/Services/TaskInvokerRegistry.cs`

