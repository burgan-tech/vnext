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
| `storeName` | all | Optional Dapr state store component. When omitted, the executing runtime's `DAPR_STATE_STORE_NAME` configuration value is used, so each runtime targets its own component. |
| `key` | get / set / single delete | Cache key. Stored under the fixed `custom:` prefix (see Key naming convention). |
| `keys` | delete | List of keys for bulk delete. Each entry gets the `custom:` prefix. |
| `query` | delete | Dapr state Query API filter (JSON) for tag/pattern delete. Requires a query-capable state store. |
| `value` | set | Value to store (JSON). |
| `ttlInSeconds` | set | Optional TTL (Dapr `ttlInSeconds` metadata). |
| `etag` | get / set | Optimistic concurrency token. |
| `concurrency` | set | `FirstWrite` \| `LastWrite`. |
| `consistency` | get / set | `Eventual` \| `Strong`. |
| `metadata` | all | Optional component-specific metadata. |

## Key naming convention

The state store is shared with the engine's own cache consumers (`CacheSet` entries like
`{Component}:{domain}:{key}:latest`, the post-commit idempotency store's
`postcommit:idempotency:{key}`, …). To prevent collisions and establish a naming convention,
every task-supplied key is stored under the fixed **`custom:`** prefix:

- Task config `key: "customer:42"` → store key `custom:customer:42`
- Combined with the Redis component's `keyPrefix: "vnext"`, the physical Redis key becomes
  `vnext||custom:customer:42`.

The prefix is applied by the invoker on `get`, `set` and `delete` (single key and `keys[]`).
Keys matched by a `query` are returned by the store already prefixed and are deleted as-is.
Result metadata (`Key`) reports the prefixed store key.

## Semantics

- **Cache miss** (`get` for a missing key) returns **success** with `data = null` and
  metadata `Found = false`. It does **not** trip the error boundary — the output mapping decides
  what a miss means.
- **TTL** is passed to Dapr as `ttlInSeconds` metadata on `set`.
- **Query/tag delete** uses the Dapr state Query API to resolve matching keys and then bulk
  deletes them. If the configured state store does not support querying, the task returns an
  informative failure rather than throwing.

## Component requirement

The invoker runs in the **Execution** service. When `storeName` is omitted, the store is resolved
from the Execution runtime's `DAPR_STATE_STORE_NAME` configuration value (`vnext-state` in the
shipped environments), so no component name is hard-coded. An explicit `storeName` must be
exposed by the Execution sidecar — the shipped `etc/execution/dapr/components/state.yaml`
defines the `vnext-state` component (Redis, `keyPrefix: vnext`), matching the
orchestration-side component.

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
- `etc/execution/dapr/components/state.yaml`
