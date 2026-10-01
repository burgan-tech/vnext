# Cache-Aside Task

## Purpose

`CacheAsideTask` is a built-in task type (`TaskType = 18`, JSON discriminator `"18"`) that implements
the **cache-aside (read-through)** pattern as a single first-class workflow task. It replaces the
manual "check cache → call service → write cache" wiring of three separate tasks and centralizes TTL,
consistency and cache-failure semantics in the engine.

On execution it:

1. Resolves the cache key (a static string, or a script — see [Config fields](#config-fields)) and
   reads it from the configured Dapr state store (the cache).
2. **Cache hit** — returns the cached value as the task result; the source task is **not** executed.
3. **Cache miss** (or `forceRefresh: true`) — runs the referenced `sourceTask` **as a task**
   (`sourceMapping` is its mapping: InputHandler → invoke → OutputHandler), writes the shaped output
   to the cache with `ttlInSeconds` + `consistency`, and returns it.
4. In both cases the task-level `onExecutionTasks[].mapping` `OutputHandler` then runs on the result.

```
key (string | script)
   │
   ▼
cache read ──HIT──────────────────────────────────────┐
   │ MISS / forceRefresh                              │
   ▼                                                  │
source task:  sourceMapping.InputHandler              │
              → invoke (the source type's own route)  │
              → sourceMapping.OutputHandler           │
   │ shaped output                                    │
   ▼                                                  │
cache write (TTL, consistency) ───────────────────────┤
                                                      ▼
                         task-level OutputHandler (hits and misses)
```

## Architecture

There is **no `cacheaside` wire type, invoker or routing key** any more (the former
`CacheAsideBinding`, `LocalCacheAsideTaskInvoker` and Execution-side `CacheAsideTaskInvoker` were
removed). Everything runs in the Orchestration executor:

- **`CacheAsideTaskExecutor`** (Orchestration / Application) runs the input stage (task mapping
  `InputHandler`, then the `key` script), the read-through, and the output stage.
- **Cache get/set** goes through `IStateStoreCacheGateway` — the same gateway the
  [function response cache](#related-function-level-result-caching) uses — which dispatches
  `statestore` envelopes through `ITaskInvocationDispatcher`. Cache I/O therefore follows
  `Workflow:TaskInvocation:Modes.statestore` (Local in-process, or Remote through the Execution
  service), exactly like the [State Store task](./state-store-task.md). See
  [Task Invocation Routing](task-invocation-routing.md).
- **The source** runs through its OWN `ITaskExecutor` (resolved from `ITaskExecutorRegistry`) with
  `sourceMapping` as its mapping, so it runs and routes exactly as the same task would in
  `onExecutionTasks` — wherever its own type runs. It deliberately does not go through
  `ITaskExecutionEngine`: that compiles the error boundary for every task it runs, which would apply
  the boundary twice and add a second journal row.
- The task result participates in instance-data versioning (Patch bump) like any other task result.

## Config fields

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `key` | string \| ScriptCode | yes | The cache key. A **string** is used verbatim. A **ScriptCode object** computes it at runtime: `location: "dynamicExpresso"` is a Dynamic Expresso expression (e.g. `"customer:" + context.Headers.customerId + ":profile"`); any other location is a C# `ICacheKeyMapping` (Roslyn). NAT, B64 and REF encodings are all accepted. A non-blank script result overrides a key set earlier in the input stage. The former `keyExpression` field is **removed** — see the `cacheaside-key-expression-removed` deprecation. |
| `storeName` | string | no | Dapr state store component used as the cache. When omitted, `DAPR_STATE_STORE_NAME` of the executing runtime is used. |
| `ttlInSeconds` | int | no | TTL for the cached entry. When absent or `0`, the entry has **no expiry**. |
| `consistency` | string | no | `Eventual` (default) or `Strong` — passed through to the state store on read and write. |
| `sourceTask` | task ref | yes | Reference (`key`/`domain`/`flow`/`version`) to the task executed on a miss. Any task type except another CacheAside task (rejected, log 10177). `flow` defaults to the runtime tasks schema when omitted. |
| `sourceMapping` | mapping | no | The **source task's** mapping: `InputHandler` runs before the source call (it may configure the source task, e.g. `SetKey`), `OutputHandler` after it. Its output is what gets cached. |
| `bypassOnCacheError` | bool | no | `true` (default): cache read/write failures fall back to the source (log 10176 `CacheAsideBypassedCacheError`). `false`: cache errors surface as a task failure. |
| `forceRefresh` | bool | no | `true`: skip the cache read, always run the source and overwrite the entry. |

The `ICacheKeyMapping` contract (`src/BBT.Workflow.Domain/Scripting/Contracts/ICacheKeyMapping.cs`):

```csharp
public class CustomerProfileKey : ICacheKeyMapping
{
    public Task<string?> Handler(ScriptContext context)
        => Task.FromResult<string?>($"customer:{context.Headers["customerid"]}:profile");
}
```

Returning `null` or whitespace keeps the previously resolved key.

## Key naming convention

Cache keys share the `custom:` prefix with the [State Store task](./state-store-task.md), so a
`CacheAsideTask` and a `StateStoreTask` targeting the same logical key hit the same physical entry.
Example: `key: "customer:42:profile"` → store key `custom:customer:42:profile`.

**What is stored.** The cache holds the **shaped** value — the `sourceMapping` output, or the raw
source data when there is no `sourceMapping`. A State Store `set` that pre-warms a cache-aside entry
must therefore write the shaped value, not the raw source payload.

## Semantics

- **Cache hit** — the cached value is returned; the source is not executed.
- **Cache miss** — the source runs as a task, its shaped output is written to the cache and returned.
- **`forceRefresh: true`** — behaves as a miss regardless of cache content and refreshes the entry.
- **Task-level OutputHandler** — the `onExecutionTasks[].mapping` `OutputHandler` runs on hits AND
  misses. `context.Body.metadata` carries `CacheHit`, `Refreshed`, `Key`, `StoreName` and `ETag`.
- **Cache error with `bypassOnCacheError: true`** — log a warning, run the source, return its result
  (a failed write is ignored).
- **Cache error with `bypassOnCacheError: false`** — the task fails and flows into the error boundary.
- **Source failure** — propagated as this task's failure; nothing is cached.
- **Error boundary** — applies **once**, on the CacheAside task (the source has no boundary or journal
  row of its own).
- **Source routing** — the source's own type decides (HTTP may run Local, a Python source Remote, …);
  only the cache I/O follows `statestore`.

## Component requirement

When `storeName` is omitted the store is resolved from the executing runtime's
`DAPR_STATE_STORE_NAME` (`vnext-state` in the shipped environments). An explicit `storeName` must be
exposed to whichever sidecar performs the cache call: Orchestration's by default, Execution's if
`statestore` is routed Remote.

## Example task definition

A `GetInstanceData` source with no static key; the key is computed by a Dynamic Expresso script and
the source's key is set from the request by `sourceMapping`.

```json
{
  "type": "18",
  "config": {
    "key": { "location": "dynamicExpresso", "code": "\"customer:\" + context.Headers.customerid + \":profile\"" },
    "storeName": "customer-cache-store",
    "ttlInSeconds": 300,
    "consistency": "Eventual",
    "sourceTask": { "key": "get-customer-instance", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
    "sourceMapping": { "location": "./src/mappings/get-customer-source.csx", "code": "<base64>" },
    "bypassOnCacheError": true,
    "forceRefresh": false
  }
}
```

`get-customer-source.csx` (the source task's mapping, a normal `IMapping`):

```csharp
public class GetCustomerSource : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        ((GetInstanceDataTask)task).SetKey(context.Headers["customerid"]);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
        => Task.FromResult(new ScriptResponse { Data = /* shape the source response; this is what is cached */ });
}
```

A Roslyn key instead of Dynamic Expresso: set `"key": { "location": "./src/mappings/customer-key.csx", "code": "<base64>" }`
and implement `ICacheKeyMapping` as shown under [Config fields](#config-fields).

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
the function and writes the response back. The cache get/set goes through `StateStoreCacheGateway`,
which reads/writes via `ITaskInvocationDispatcher` on the `statestore` wire type — in-process on
Orchestration by default, or the Execution service if that type is reconfigured Remote (same
`custom:` prefix / TTL / consistency either way; see
[Task Invocation Routing](task-invocation-routing.md)). Only side-effect-free (read) functions should
opt in. A deterministic `sha256(string)` helper is available in `keyExpression` for bounded,
vary-by-correct keys; the config's own version is available as `context.Instance.Version`, so folding
it into the key makes a new config version produce a new key (no active deletion needed for config
changes).

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
- `src/BBT.Workflow.Application/Tasks/Executors/Cache/CacheAsideTaskExecutor.cs`
- `src/BBT.Workflow.Application/Tasks/Evaluators/CacheKeyEvaluator.cs`
- `src/BBT.Workflow.Domain/Scripting/Contracts/ICacheKeyMapping.cs`
- `src/BBT.Workflow.Application/Tasks/Evaluators/DynamicExpressoValueEvaluator.cs`
- `src/BBT.Workflow.Application/Functions/StateStoreCacheGateway.cs`
- `src/BBT.Workflow.Domain/Definitions/Functions/FunctionCache.cs`
- `docs/runtime/state-store-task.md`
- `docs/runtime/task-invocation-routing.md`
