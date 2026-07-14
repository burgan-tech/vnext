# Cache-Aside Task

## Purpose

`CacheAsideTask` is a built-in task type (`TaskType = 18`, JSON discriminator `"18"`) that
implements the **cache-aside (read-through)** pattern as a single first-class workflow task.
It replaces the manual "check cache → call service → write cache" wiring of three separate tasks
and centralizes TTL, consistency and cache-failure semantics in the engine.

On execution it:

1. Resolves the cache `key` template against the script context and reads it from the configured
   Dapr state store (the cache).
2. **Cache hit** — returns the cached value as the task result; the source task is **not** executed.
3. **Cache miss** (or `forceRefresh: true`) — executes the referenced `sourceTask`, applies the
   optional `sourceMapping` (`.csx`) to shape the result, writes the shaped value back to the cache
   with `ttlInSeconds` + `consistency`, and returns it.

Unlike the Dapr remote tasks, `CacheAsideTask` runs entirely as an **Orchestration-side executor**
(`CacheAsideTaskExecutor`). The cache read/write is done directly against the state store via
`IStateStoreAccessor` (no extra service-invocation hop), and the source task is executed through its
own executor. The task result participates in instance-data versioning (Patch bump) exactly like any
other task result.

## Config fields

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `key` | string | yes* | Cache key template. Supports `{context.<path>}` placeholder interpolation from the script context (e.g. `{context.Headers.customerId}`, `{context.Instance.Data.orderId}`, `{context.Body.accountId}`). *Not required when `keyMapping` is supplied. |
| `keyMapping` | mapping | no | Optional `.csx` mapping that **computes** the cache key from the full script context instead of interpolating the `key` template. Its `OutputHandler` returns the key as a plain string in `Data`. Returning `null`/empty signals the result is **not cacheable** for this request — the source task then runs directly with no cache read/write. Domain-agnostic: the runtime imposes no semantics; the mapping alone derives a content-aware, vary-by-correct key and decides cacheability (e.g. a domain may bypass caching when the evaluation depends on database variables or other externally-mutable data whose values can change at any time). Runs as the mapping's `OutputHandler`, before the cache read, so it can inspect earlier pipeline task outputs (`context.OutputResponse`). |
| `storeName` | string | no | Dapr state store component used as the cache. When omitted, the runtime's `DAPR_STATE_STORE_NAME` value is used. |
| `ttlInSeconds` | int | no | TTL for the cached entry. When absent or `0`, the entry has **no expiry**. |
| `consistency` | string | no | `Eventual` (default) or `Strong` — passed through to the state store on read and write. |
| `sourceTask` | task ref | yes | Reference (`key`/`domain`/`flow`/`version`) to the task executed on a cache miss. `flow` defaults to the runtime tasks schema when omitted. |
| `sourceMapping` | mapping | no | `.csx` mapping (`location` + base64 `code`) applied to the source task result before caching/returning. Runs as the mapping's `OutputHandler`. |
| `bypassOnCacheError` | bool | no | `true` (default): cache read/write failures fall back to the source task instead of failing the pipeline. `false`: cache errors surface as a task failure (error boundary applies). |
| `forceRefresh` | bool | no | `true`: skip the cache read, always execute the source task and overwrite the entry. |

## Key naming convention

Cache keys share the same `custom:` prefix as the [State Store task](./state-store-task.md), so a
`CacheAsideTask` and a `StateStoreTask` targeting the same logical key hit the same physical entry.
This lets designers pre-warm or invalidate a cache-aside entry with a plain State Store `set`/`delete`
task. Example: `key: "customer:42:profile"` → store key `custom:customer:42:profile`.

## Semantics

- **Cache hit** — resolved key found: return the cached value; the source task is not executed.
- **Cache miss** — execute `sourceTask`, apply `sourceMapping` (if present), write the result to the
  cache with `ttlInSeconds` + `consistency`, and return the result.
- **`forceRefresh: true`** — behave as a miss regardless of cache content and refresh the entry.
- **Cache infrastructure error with `bypassOnCacheError: true`** — log a warning, execute the source
  task, and return its result (best-effort cache write; a failed write is ignored).
- **Cache infrastructure error with `bypassOnCacheError: false`** — the task fails and flows into the
  error boundary chain.
- **Source task business failure** — propagated as this task's failure (nothing is cached).
- **Key interpolation failure** — an unresolved `{context.…}` placeholder fails the task rather than
  silently producing a colliding key.

## Component requirement

The executor runs in the **Orchestration** runtime and talks to the state store through the local
Dapr sidecar. When `storeName` is omitted, the store is resolved from the Orchestration runtime's
`DAPR_STATE_STORE_NAME` value (`vnext-state` in the shipped environments); an explicit `storeName`
must be exposed by the Orchestration sidecar (`etc/orchestration/dapr/components/state.yaml`).

## Example task definition

```json
{
  "type": "18",
  "config": {
    "key": "customer:{context.Headers.customerId}:profile",
    "storeName": "customer-cache-store",
    "ttlInSeconds": 300,
    "consistency": "Eventual",
    "sourceTask": { "key": "get-customer-http", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
    "sourceMapping": { "location": "./src/mappings/get-customer-cached.csx", "code": "<base64>" },
    "bypassOnCacheError": true,
    "forceRefresh": false
  }
}
```

## References

- `src/BBT.Workflow.Domain/Definitions/Tasks/CacheAsideTask.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Cache/CacheAsideTaskExecutor.cs`
- `src/BBT.Workflow.Application/Tasks/Caching/CacheKeyInterpolator.cs`
- `src/BBT.Workflow.Application/Tasks/Caching/IStateStoreAccessor.cs`
- `src/BBT.Workflow.Application/Tasks/Caching/DaprStateStoreAccessor.cs`
- `docs/runtime/state-store-task.md`
