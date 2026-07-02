# State Store Task

## Purpose

`StateStoreTask` is a built-in task type (`TaskType = 17`, JSON discriminator `"17"`) that reads
and writes a **Dapr state store** component from the workflow / function pipeline. It is the
caching primitive for flows: read a cached value, write/update one, or delete one or more.

Unlike the other Dapr tasks, it **executes locally inside Orchestration** —
`StateStoreTaskExecutor` calls the state store directly through `DaprClient` (same local pattern
as `NotificationTaskExecutor`). There is no Orchestration → Execution round-trip: a caching
primitive exists for latency, and the Orchestration sidecar already exposes the `vnext-state`
component, so the extra hop would only add overhead.

## Commands

| Command | Behavior | Dapr API |
| --- | --- | --- |
| `get` | Read the value for `key`. | `GetStateAndETagAsync` |
| `set` | Write / update `key` with `value`. | `SaveStateAsync`, or `TrySaveStateAsync` when `etag` is set |
| `delete` | Delete `key`, `keys[]`, or a tag/pattern `query`. | `DeleteStateAsync` / `DeleteBulkStateAsync` / `QueryStateAsync` + bulk delete |

## Configuration fields

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

- **Cache miss** (`get` for a missing key) returns **success** with `data = null` and metadata
  `Found = false`. It does **not** trip the error boundary — the output mapping decides what a
  miss means.
- **TTL** is passed to Dapr as `ttlInSeconds` metadata on `set`.
- **Query/tag delete** uses the Dapr state Query API to resolve matching keys and then bulk
  deletes them. If the configured state store does not support querying, the task returns an
  informative failure rather than throwing.
- Input/output mapping stages work exactly as with other tasks; the invocation result (including
  `Found`, `ETag`, `Saved`, `DeletedCount` metadata) is available to the output handler.

## Component requirement

The executor runs in the **Orchestration** service, whose Dapr sidecar must expose the target
state store. The existing `etc/orchestration/dapr/components/state.yaml` already defines
`vnext-state` (Redis, `keyPrefix: vnext`); no additional component is needed.

## Observability

The base executor wraps invocation in an OpenTelemetry activity (`OperationInvoke`). Failures and
cancellations are logged through `WorkflowLogs.StateStoreOperationFailed` /
`StateStoreOperationCancelled` (EventIds 10133 / 10134).

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
- `src/BBT.Workflow.Application/Tasks/Executors/Dapr/StateStoreTaskExecutor.cs`
- `etc/orchestration/dapr/components/state.yaml`
