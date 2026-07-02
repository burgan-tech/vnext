# State Store Task

## Purpose

`StateStoreTask` is a built-in task type (`TaskType = 17`, JSON discriminator `"17"`, execution
key `statestore`) that reads and writes a **Dapr state store** component from the workflow /
function pipeline. It is the caching primitive for flows: read a cached value, write/update one,
or delete one or more. Command names mirror the Dapr state API verbs.

It follows the same split as the other Dapr tasks: `StateStoreTaskExecutor` (Orchestration /
Application) performs input/output mapping and sends a `TaskEnvelope` to the Execution service,
where `StateStoreTaskInvoker` performs the state store call through `DaprClient`.

## Commands

| Command | Behavior | Dapr API |
| --- | --- | --- |
| `get` | Read the value for `key`. | `GetStateAndETagAsync` |
| `set` | Write / update `key` with `value`. | `SaveStateAsync`, or `TrySaveStateAsync` when `etag` is set |
| `delete` | Delete `key`, `keys[]`, or a tag/pattern `query`. | `DeleteStateAsync` / `DeleteBulkStateAsync` / `QueryStateAsync` + bulk delete |

## Binding fields

| Field | Applies to | Notes |
| --- | --- | --- |
| `command` | all | `get` \| `set` \| `delete`. |
| `storeName` | all | Dapr state store component; defaults to `vnext-state`. |
| `key` | get / set / single delete | Cache key. |
| `keys` | delete | List of keys for bulk delete. |
| `query` | delete | Dapr state Query API filter (JSON) for tag/pattern delete. Requires a query-capable state store. |
| `value` | set | Value to store (JSON). |
| `ttlInSeconds` | set | Optional TTL (Dapr `ttlInSeconds` metadata). |
| `etag` | get / set | Optimistic concurrency token. |
| `concurrency` | set | `FirstWrite` \| `LastWrite`. |
| `consistency` | get / set | `Eventual` \| `Strong`. |
| `metadata` | all | Optional component-specific metadata. |

## Semantics

- **Cache miss** (`get` for a missing key) returns **success** with `data = null` and
  metadata `Found = false`. It does **not** trip the error boundary — the output mapping decides
  what a miss means.
- **TTL** is passed to Dapr as `ttlInSeconds` metadata on `set`.
- **Query/tag delete** uses the Dapr state Query API to resolve matching keys and then bulk
  deletes them. If the configured state store does not support querying, the task returns an
  informative failure rather than throwing.

## Component requirement

The invoker runs in the **Execution** service, whose Dapr sidecar must expose the target state
store. The repo ships `etc/execution/dapr/components/vnext-state.yaml` (Redis, `keyPrefix: vnext`)
so `vnext-state` resolves there, matching the orchestration-side component.

## Example task definition

```json
{
  "type": "17",
  "command": "set",
  "storeName": "vnext-state",
  "key": "customer:{{instanceId}}:profile",
  "value": { "name": "Ada" },
  "ttlInSeconds": 300,
  "consistency": "strong",
  "concurrency": "lastWrite"
}
```

## References

- `src/BBT.Workflow.Domain/Definitions/Tasks/StateStoreTask.cs`
- `src/BBT.Workflow.Execution.Abstractions/Bindings/StateStoreBinding.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Dapr/StateStoreTaskExecutor.cs`
- `src/BBT.Workflow.Application/Tasks/Mapping/TaskBindingMapper.cs`
- `src/BBT.Workflow.Execution/Invokers/StateStoreTaskInvoker.cs`
- `etc/execution/dapr/components/vnext-state.yaml`
