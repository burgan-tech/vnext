# Cache-Aside Task

## Purpose

`CacheAsideTask` is a built-in task type (`TaskType = 18`, JSON discriminator `"18"`, execution key
`cacheaside`) that implements the **cache-aside (read-through)** pattern as a single first-class
workflow task. It replaces the manual "check cache → call service → write cache" wiring of three
separate tasks and centralizes TTL, consistency and cache-failure semantics in the engine.

On execution it:

1. Resolves the cache `key` and reads it from the configured Dapr state store (the cache).
2. **Cache hit** — returns the cached value as the task result; the source task is **not** executed.
3. **Cache miss** (or `forceRefresh: true`) — executes the referenced `sourceTask`, writes its raw
   result back to the cache with `ttlInSeconds` + `consistency`, and returns it.

## Architecture

It follows the exact same split as the [State Store task](./state-store-task.md):

- **`CacheAsideTaskExecutor`** (Orchestration / Application) runs the input mapping, resolves the source
  task into an envelope, then sends a `cacheaside` `TaskEnvelope` to the Execution service through
  `IRemoteInvokerService`, and finally runs the output mapping.
- **`CacheAsideTaskInvoker`** (Execution) performs the actual state-store access through `DaprClient`
  (get / set), applying the shared `custom:` key prefix, TTL and consistency — mirroring
  `StateStoreTaskInvoker`. On a miss it dispatches the pre-resolved **source task envelope** through the
  local `ITaskInvokerRegistry`, so an HTTP source runs on the same Execution service (its own invoker),
  and writes the raw result back to the cache.

Because the scripting engine only exists in the Orchestration runtime, the **cache stores the raw source
result** and any shaping (`sourceMapping`) is applied by the executor's output stage on every read (hit
and miss). The dynamic cache key is likewise resolved in Orchestration (input mapping). The task result
participates in instance-data versioning (Patch bump) exactly like any other task result.

## Config fields

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `key` | string | yes | Static cache key, used verbatim. Overridden at runtime by `keyExpression` (if set) or by a mapping `InputHandler` that calls `task.SetCacheKey(...)`. |
| `keyExpression` | mapping | no | A **Dynamic Expresso** expression (`ScriptCode` with `location: "dynamicExpresso"`) that computes the cache key from the request/context and returns a string — e.g. `"customer:" + context.Headers.customerId + ":profile"`. Evaluated in the executor's input stage; its result **overrides** `key`. This is the lightweight way to derive a vary-by-correct key from user-supplied data without a full `.csx` mapping, reusing the same interpreter the condition rules use. |
| `storeName` | string | no | Dapr state store component used as the cache. When omitted, the Execution runtime's `DAPR_STATE_STORE_NAME` value is used. |
| `ttlInSeconds` | int | no | TTL for the cached entry. When absent or `0`, the entry has **no expiry**. |
| `consistency` | string | no | `Eventual` (default) or `Strong` — passed through to the state store on read and write. |
| `sourceTask` | task ref | yes | Reference (`key`/`domain`/`flow`/`version`) to the task executed on a cache miss. Must be a remotely-invokable task type (e.g. HTTP/SOAP/Dapr/GetInstanceData). `flow` defaults to the runtime tasks schema when omitted. |
| `sourceMapping` | mapping | no | `.csx` mapping applied to the cached (raw source) result before it is returned. Runs as the mapping's `OutputHandler` in the executor's output stage, on both hits and misses. |
| `bypassOnCacheError` | bool | no | `true` (default): cache read/write failures fall back to the source task instead of failing the pipeline. `false`: cache errors surface as a task failure (error boundary applies). |
| `forceRefresh` | bool | no | `true`: skip the cache read, always execute the source task and overwrite the entry. |

## Key naming convention

Cache keys share the same `custom:` prefix as the [State Store task](./state-store-task.md), so a
`CacheAsideTask` and a `StateStoreTask` targeting the same logical key hit the same physical entry.
This lets designers pre-warm or invalidate a cache-aside entry with a plain State Store `set`/`delete`
task. Example: `key: "customer:42:profile"` → store key `custom:customer:42:profile`.

## Semantics

- **Cache hit** — resolved key found: return the cached value; the source task is not executed.
- **Cache miss** — execute `sourceTask`, write its raw result to the cache with `ttlInSeconds` +
  `consistency`, and return it (shaped by `sourceMapping` on the way out).
- **`forceRefresh: true`** — behave as a miss regardless of cache content and refresh the entry.
- **Cache infrastructure error with `bypassOnCacheError: true`** — log a warning, execute the source
  task, and return its result (best-effort cache write; a failed write is ignored).
- **Cache infrastructure error with `bypassOnCacheError: false`** — the task fails and flows into the
  error boundary chain.
- **Source task business failure** — propagated as this task's failure (nothing is cached).

## Component requirement

The cache `get`/`set` runs in the **Execution** service (`CacheAsideTaskInvoker`), dispatched from the
Orchestration executor via Dapr service invocation. When `storeName` is omitted, the store is resolved
from the Execution runtime's `DAPR_STATE_STORE_NAME` value (`vnext-state` in the shipped environments);
an explicit `storeName` must be exposed by the Execution sidecar
(`etc/execution/dapr/components/state.yaml`).

## Example task definition

```json
{
  "type": "18",
  "config": {
    "key": "customer:42:profile",
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

## Related: function-level result caching

A whole **function's** response can be cached with the same read-through semantics, without wiring a
CacheAside task, by adding a `cache` block to the function definition:

```jsonc
"cache": {
  "keyExpression": { "location": "dynamicExpresso",
                     "code": "\"dcs:\" + context.Headers.configKey + \":\" + context.Headers.version + \":\" + sha256(context.Headers.varyBy)" },
  "storeName": "vnext-state",
  "ttlInSeconds": 300,
  "consistency": "Eventual",
  "bypassOnCacheError": true
}
```

`FunctionAppService` wraps execution: it resolves the key (Dynamic Expresso `keyExpression` — evaluated
against the request/script context — or a static `key`), reads the cache; on a **hit** it returns the
cached `FunctionResponseOutput` (Data + StatusCode + Headers) and skips the tasks; on a **miss** it runs
the function and writes the response back. The cache get/set goes through the Execution `statestore`
invoker (same `custom:` prefix / TTL / consistency). Only side-effect-free (read) functions should opt
in. A deterministic `sha256(string)` helper is available in `keyExpression` for bounded, vary-by-correct
keys; the config's own version is available as `context.Instance.Version`, so folding it into the key
makes a new config version produce a new key (no active deletion needed for config changes).

### Invalidation (generation-namespace)

For dependencies that change **without** a version bump (e.g. db-vars), add a `generationKey` /
`generationKeyExpression` — the state key holding a monotonic "generation" stamp. The runtime reads the
stamp and folds it into the cache key (`…:g:{generation}`). Bumping the stamp (a single write on the
dependency-change transition) makes every subsequent request compute a new key, so **all cached variants
of the config are invalidated at once** — old entries are simply never read again and expire via TTL. No
prefix scan / delete is required, so it stays Dapr-store-agnostic. Absent a stamp entry, generation is
`0`; a generation-read failure with `bypassOnCacheError: true` runs the function without caching.

```jsonc
"cache": {
  "keyExpression": { "location": "dynamicExpresso",
                     "code": "\"dcs:\" + context.Headers.configKey + \":\" + context.Instance.Version + \":\" + sha256(context.Headers.varyBy)" },
  "generationKey": "dcs:gen:configA",   // db-var write bumps this → all variants invalidated
  "storeName": "vnext-state", "ttlInSeconds": 300, "bypassOnCacheError": true
}
```

## References

- `src/BBT.Workflow.Domain/Definitions/Tasks/CacheAsideTask.cs`
- `src/BBT.Workflow.Execution.Abstractions/Bindings/CacheAsideBinding.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Cache/CacheAsideTaskExecutor.cs`
- `src/BBT.Workflow.Application/Tasks/Evaluators/DynamicExpressoValueEvaluator.cs`
- `src/BBT.Workflow.Execution/Invokers/CacheAsideTaskInvoker.cs`
- `src/BBT.Workflow.Execution/StateStores/IStateStoreClient.cs`
- `src/BBT.Workflow.Domain/Definitions/Functions/FunctionCache.cs`
- `src/BBT.Workflow.Application/Functions/StateStoreCacheGateway.cs`
- `docs/runtime/state-store-task.md`
