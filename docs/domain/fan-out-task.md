# FanOut Task (Type 21)

## Purpose

`FanOutTask` resolves a collection from instance data at runtime and runs one **inner task**
once per item, in parallel, then joins the per-item outcomes into **one** task result and
**one** instance-data write. It exists for the case a workflow author cannot solve with static
parallelism: the number of items is not known at design time (an array of documents to sign, a
list of recipients to notify, a batch of accounts to reconcile).

This phase (Phase 1) supports **inline mode only** — the whole batch runs synchronously inside
the transition that triggers it, bounded by the batch's own timeout. There is no persisted
batch/item state and no cross-request resumption; `mode: "durable"` is reserved in the schema
for a later phase and is rejected at parse time (`FanOutTask.Configure`, `src/BBT.Workflow.Domain/Definitions/Tasks/FanOutTask.cs`).

Static, design-time parallelism (a fixed, known set of tasks at the same `order`) already works
via `TaskCoordinator.ExecuteTaskGroupInParallelAsync` and needs no `FanOutTask` — reach for
FanOut only when the item count comes from data, not from the workflow definition.

### When NOT to use it

- **The downstream integration has a batch endpoint.** If the service you are calling can take
  N items in one request, that single call is cheaper and more consistent than N parallel calls
  through the fan-out machinery (N journal rows, N task-engine invocations, N DI scopes). Reach
  for FanOut when the target genuinely has no batch API, or when the "items" are heterogeneous
  workflow-internal work (e.g. running a `SubProcess` per item) rather than one HTTP call.
- **You need the item's own state to affect a later item.** Items run concurrently and
  independently; there is no ordering guarantee between item *executions* (only the final result
  list is re-sorted by index — see [`join.ordered`](#config-schema)). A pipeline where item 2
  depends on item 1's output does not fit fan-out.
- **You need per-item writes to instance data as they complete.** Fan-out is single-writer by
  design (see [§ Single-write invariant](#single-write-invariant-and-why-item-handlers-must-be-pure)).
  If you need a running counter or streaming progress visible before the batch finishes, this is
  not the right primitive.

## Config Schema

```jsonc
{
  "attributes": {
    "type": "21",
    "config": {
      "mode": "inline",
      "itemsPath": "$.documents",
      "itemAlias": "document",
      "task": {
        "key": "process-single-document",
        "domain": "core",
        "flow": "sys-tasks",
        "version": "1.0.0"
      },
      "execution": {
        "maxDegreeOfParallelism": 4,
        "itemTimeoutSeconds": 30,
        "batchTimeoutSeconds": 120
      },
      "join": {
        "policy": "allSettled",
        "minSuccess": 8,
        "resultKey": "documentResults",
        "ordered": true
      },
      "errorBoundary": {
        "onError": [
          { "action": "retry", "errorCodes": ["Task:503", "Task:429"], "priority": 1,
            "retryPolicy": { "maxRetries": 3, "initialDelay": "PT1S", "backoffType": "exponential", "useJitter": true } },
          { "action": "log", "errorCodes": ["*"], "priority": 999, "logOnly": true }
        ]
      }
    }
  }
}
```

| Field | Default | Meaning |
|---|---|---|
| `mode` | `"inline"` | Only `"inline"` is accepted in this phase. Any other value fails task parsing with `ArgumentException` ("not supported yet"). Present now so a future `"durable"` mode is not a breaking schema change. |
| `itemsPath` | none | A `"$."`-rooted **dot-path subset** of JSONPath into instance data (property navigation only — no filters, wildcards, indices or slices; see `FanOutItemsResolver`). Must start with `"$."` or parsing fails. Mutually exclusive with the mapping's `ItemSelector` — configuring both, or neither, is a runtime execution error (checked by the executor, not the JSON-schema validator; see [§ Validation](#validation)). A missing path (or any intermediate segment absent) resolves to an empty batch, not an error; a path that resolves to a non-array value throws. |
| `itemAlias` | none | Accepted and stored on the task (round-trips through `Clone`/`Reset`), documented as "used in default input binding and log readability" — **but the current executor does not read it anywhere.** The default binding (below) sets the branch context's raw `Body` regardless of this value, and no log statement includes it. Treat it as reserved/aspirational until the executor is updated; do not rely on it to shape the default binding or logs today. |
| `task` | required | Reference to the inner task: `key`, `domain`, `flow`, `version` — all four required. Resolved once per batch via the component cache/task factory and cloned per item (not re-resolved per item). If the referenced task's type is `21` (FanOut itself), the executor rejects the batch before running anything — see [§ Nested fan-out](#author-beware). |
| `execution.maxDegreeOfParallelism` | `4` | Batch-local concurrency cap (`SemaphoreSlim`). Must be `>= 1`. Deliberately low by default: an unbounded fan-out DDoSes whatever the inner task calls. |
| `execution.itemTimeoutSeconds` | `30` | Per-item deadline. Must be `>= 1` and `<= batchTimeoutSeconds`. |
| `execution.batchTimeoutSeconds` | `120` | Whole-batch deadline; items still running when it fires are cancelled and counted as `FanOut:BatchTimeout` failures, and `FanOutResult.TimedOut` becomes `true`. Must be `>= 1`. |
| `join.policy` | `"allSettled"` | One of `all` / `allSettled` / `quorum` / `firstSuccess` — see [§ Join Policy](#join-policy). |
| `join.minSuccess` | none | Required (`>= 1`) when `policy` is `quorum`; parsing fails otherwise. Ignored (with no warning today) for other policies. |
| `join.resultKey` | `"fanOutResults"` | Instance-data key the **default** output packaging writes item results under, when the task ships no mapping (or the mapping's `OutputHandler` is not what you want to key off). |
| `join.ordered` | `true` | Accepted for forward compatibility with a future durable mode that may stream results in completion order. **In inline mode it is a no-op** — item results are always returned sorted by `Index`, and the executor deliberately never reads this flag to decide otherwise. |
| `errorBoundary` | none | A normal `ErrorBoundary` (`onError` rules: `action`, `errorCodes`/`errorTypes`, `priority`, `retryPolicy`, `logOnly`) applied **independently to every item** through the same engine machinery a state or transition error boundary uses. A retry-exhausted item becomes one `Failed` entry in the result set; it does not, by itself, stop the batch — the join policy decides that. |

## Join Policy

| `join.policy` | Succeeds when | Empty batch (0 items) |
|---|---|---|
| `all` | Every item succeeded **and** the batch did not time out. The executor cancels the remaining items via early-stop the moment the first item fails. | **Succeeds** (vacuously — no failures possible with no items). |
| `allSettled` | Always. Partial failure is data, not an error — the flow branches on the result summary. Succeeds even if the batch timed out. | **Succeeds.** |
| `quorum` | `succeeded >= minSuccess`, regardless of `timedOut`. | **Fails.** `succeeded` is 0, which can never clear a threshold `>= 1`. |
| `firstSuccess` | At least one item succeeded (`succeeded >= 1`); the executor cancels the rest via early-stop on the first success. Judges purely on success count, regardless of `timedOut`. | **Fails**, for the same reason as `quorum` — `firstSuccess` is definitionally `quorum(minSuccess=1)`, and the two must never diverge on the same input. |

Notes:

- The FanOut task's own success/failure becomes an ordinary task outcome inside its own
  transition — a failed join runs the workflow's normal Task → State → Global error boundary
  chain, same as any other failed task. Fan-out introduces no new error-boundary concept at that
  level.
- A failed join (`all` / `quorum` / `firstSuccess` not met) still carries its full result data on
  the task's output — `TaskInvocationResult.Failure` is deliberately *not* used, because a caller
  branching on which items failed needs that data in instance data even when the task itself is
  marked failed.
- `allSettled` is the expected common policy specifically because it lets you inspect
  `{resultKey}Summary` from an auto-transition condition afterward — see
  [§ Branching on partial failure](#error-codes-and-branching-on-partial-failure).

## `IFanOutMapping` — the mapping contract

Location: `src/BBT.Workflow.Domain/Scripting/Contracts/IFanOutMapping.cs`. Authored the same way
as any other mapping — a `.csx` script compiled by the runtime and referenced from the task's
`mapping` field.

```csharp
public interface IFanOutMapping
{
    // Only implement this when NOT using itemsPath. Default returns null ("use itemsPath").
    Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context)
        => Task.FromResult<IEnumerable<dynamic>?>(null);

    // Called once per item, on that item's own isolated branch context.
    // Mutates the CLONED inner task directly — this is how you shape a per-item
    // HTTP URL/body, a SOAP envelope, etc. The returned ScriptResponse is audit
    // data only (visible in the item's InstanceTask journal row); it is NOT merged
    // into instance data.
    Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item);

    // Called EXACTLY ONCE per batch, after every item has settled. This is the
    // batch's single write point: the returned ScriptResponse.Data becomes the
    // FanOutTask's own output and is merged into instance data as one patch.
    Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result);
}
```

Example — fan out over `$.documents`, call an HTTP inner task per document (mutating its URL and
body per item), and branch-friendly output:

```csharp
public class ProcessDocumentsMapping : IFanOutMapping
{
    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        // task is a clone of the referenced inner task (e.g. an HttpTask) — mutate it directly.
        if (task is HttpTask http)
        {
            http.Url = $"{http.Url}/{item.ItemKey}";
            http.Body = new { documentId = item.ItemKey, payload = item.Value };
        }

        return Task.FromResult(new ScriptResponse { Data = new { itemKey = item.ItemKey } });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result)
    {
        var failedKeys = result.Items.Where(i => !i.IsSuccess).Select(i => i.ItemKey).ToList();

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                documentResults = result.Items,
                documentsSummary = new
                {
                    total = result.Total,
                    succeeded = result.Succeeded,
                    failed = result.Failed,
                    timedOut = result.TimedOut,
                    failedKeys
                }
            }
        });
    }
}
```

> If you have seen an older fan-out mapping example with the signature
> `ItemInputHandler(ScriptContext context, FanOutItem item)` (no `task` parameter), it predates
> the implemented contract. The shipped interface always passes the cloned `WorkflowTask` as the
> first parameter — that is how input binding actually mutates the inner task.

Supporting types (`src/BBT.Workflow.Domain/Scripting/Contracts/IFanOutMapping.cs`):

```csharp
public sealed record FanOutItem(int Index, dynamic? Value, string ItemKey);

public sealed record FanOutResult(
    int Total, int Succeeded, int Failed, bool TimedOut,
    IReadOnlyList<FanOutItemResult> Items);

public sealed record FanOutItemResult(
    int Index, string ItemKey, bool IsSuccess,
    dynamic? Data, string? ErrorCode, string? ErrorMessage,
    TimeSpan Duration);
```

Note there is no `Attempts` field on `FanOutItemResult` — the engine's retry count is not surfaced
through the result; attempt visibility lives in the item's `InstanceTask` journal row and retry
span events instead.

**`ItemKey` derivation** (`FanOutItemsResolver.ExtractItemKey`): an item object's `id` string
property if present, else its `key` string property, else the item's zero-based index as a
string. This applies uniformly whether the item came from `itemsPath` (a `JsonElement`) or from
`ItemSelector` (a `JsonElement`, an `ExpandoObject`/`IDictionary<string,object?>`, or an arbitrary
CLR object read via reflection — e.g. an anonymous type a `.csx` selector returns directly).

## The zero-script path

You can skip the mapping entirely when:

- `itemsPath` selects the collection (no `ItemSelector` needed), **and**
- the inner task can consume the raw item value from the branch context's body (a `ScriptTask`
  reading `context.Body`, for instance), **and**
- the default output shape is acceptable.

With no mapping, the executor:

- **Input**: sets the per-item branch context's body directly — `branch.SetBody(item.Value)` — and nothing else. It does **not** wrap the value under `Data.{itemAlias}` or any other alias-qualified path, regardless of whether `itemAlias` is configured (see the `itemAlias` row above).
- **Output**: writes item results under `join.resultKey` as a list of
  `{ index, itemKey, isSuccess, data, errorCode, errorMessage, durationMs }`, plus a
  `{resultKey}Summary` object `{ total, succeeded, failed, timedOut }`.

**The real limitation**: `SetBody` only reaches inner tasks that read the branch body directly.
Task types whose *own config* needs to change per item — an `HttpTask`'s URL or templated body,
a `SoapTask`'s envelope, a `DaprServiceTask`'s method — are not shaped by `SetBody` at all,
because those fields live on the cloned task instance, not on the script context body. Any inner
task needing that kind of per-item config mutation **requires** an `ItemInputHandler` that
mutates the cloned `WorkflowTask` directly, as in the HTTP example above.

## Single-write invariant and why item handlers must be pure

Every item runs on its **own** isolated branch context (`ScriptContext.CreateParallelBranch()`)
and its **own** DI scope (`IServiceScopeFactory.CreateAsyncScope()`, mirroring the isolation
`TaskCoordinator.ExecuteTaskGroupInParallelAsync` already uses for static parallel groups — a
private EF `DbContext` per item, since the change tracker is not thread-safe). The item runs
through the **full** task engine — its own retry loop, its own per-item error boundary, its own
`InstanceTask` journal row keyed `{fanOutTaskKey}#{index}` — with one flag flipped:
`TaskEngineExecutionOptions.SuppressDataApply = true`. That flag is what stops the item's own
output from ever reaching instance data.

The item's branch context is **discarded**, never merged back with
`ScriptContext.MergeParallelBranch()`. Merging would collide: N items reusing the same inner
task's key inside the shared `TaskResponse` dictionary would trip `MergeDictionary`'s
duplicate-key guard (`InvalidOperationException`). Fan-out deliberately does not use that
mechanism — it builds its own aggregate (`FanOutResult`) instead.

This is why `ItemInputHandler` **must be pure with respect to instance data**: it runs N times
concurrently, each on a context that is thrown away, so any write it attempted would either be
lost or race against its siblings. `OutputHandler` is the only call in the whole batch whose
returned `ScriptResponse.Data` becomes the FanOut task's real output — merged into instance data
exactly once, through the same standard task-output path every other task uses. **One fan-out
task execution ⇒ one `InstanceData` patch**, no matter the batch size — the per-item journal rows
give you the audit trail without multiplying the write.

## Error codes and branching on partial failure

`FanOutErrorCodes` (`src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutErrorCodes.cs`) is
a **public contract** — workflow authors branch on these strings in output handlers,
auto-transition conditions and error-boundary rules:

| Code | Meaning |
|---|---|
| `FanOut:ItemTimeout` | The item exceeded its own `itemTimeoutSeconds`. Takes priority over the other causes below — a slow item that also happened to be caught by a sibling's early stop is still reported as its own timeout, not as "cancelled". |
| `FanOut:BatchTimeout` | The item was cut short because the batch as a whole hit `batchTimeoutSeconds`. |
| `FanOut:ItemCancelled` | The item was cancelled by the join policy's early stop — `firstSuccess` already succeeded, or `all` already failed, and this item was still running. |
| `FanOut:ItemNotStarted` | The item was cancelled while still queueing for a concurrency slot, with no deadline or early stop to explain it. |
| `FanOut:ItemFailed` | Fallback: the item's inner task failed (or threw) with no more specific fan-out-level code — the inner task's own error code, when it has one, passes through unchanged instead. |

**The recommended partial-failure pattern**: use `join.policy: "allSettled"` so the FanOut task
itself always succeeds, write (or let the default output write)
`{resultKey}Summary.{total,succeeded,failed,timedOut}` into instance data, then let the
transition's `RunAutomaticTransitionsStep` (order 90) evaluate an auto-transition condition
against that summary — e.g. `failed > 0` routes to a `partial-failure` state, `failed == 0`
continues the happy path. The platform does not decide this for you; it is a workflow-design
choice every time.

## Concurrency and the two-level bulkhead

Two independent caps apply, acquired in this order:

1. **Batch-local**: `execution.maxDegreeOfParallelism` (default `4`) — a plain `SemaphoreSlim`
   scoped to this one batch.
2. **Process-wide**: `Workflow:FanOut:MaxConcurrentItems` (default `64`, `FanOutOptions`) — one
   singleton `SemaphoreSlim` (`FanOutConcurrencyLimiter`) shared by **every** fan-out batch
   running in the process, across every instance and workflow.

Effective concurrency for any one item is `min(batch's remaining maxDop slots, remaining global
slots)`. This is what stops **N concurrently-running instances** from multiplying into
`N × maxDegreeOfParallelism` simultaneous downstream calls: with 100 instances each running a
`maxDegreeOfParallelism: 5` fan-out at the same moment, that is 500 potential concurrent calls to
whatever the inner task hits — the global bulkhead caps the process-wide total at
`MaxConcurrentItems` regardless.

`FanOutOptions` is validated **at startup** (`ValidateDataAnnotations().ValidateOnStart()` in
`TaskServiceCollectionExtensions.AddTaskExecutors`) with `[Range(1, int.MaxValue)]` on
`MaxConcurrentItems` — a misconfigured `0` would deadlock every fan-out batch in the process on
its first item, so it fails the boot instead of hanging silently later.

There is **no distributed/domain-level cap** — the bulkhead is per-process. A dedicated
distributed counter was considered and deliberately left out of scope: it would put a network
round trip's latency on every single item.

## Observability

- **Logs** (`WorkflowLogs.cs`, EventId block 101xx): `FanOutBatchStarted` (Information — task key,
  item count, `maxDegreeOfParallelism`, join policy, instance id), `FanOutItemFailed` (Warning,
  one per failed item — item key, index, error code; a failed item is a recoverable outcome the
  join policy decides on, so it is not `Error`), `FanOutBatchCompleted` (Information — total /
  succeeded / failed / duration), `FanOutBatchTimedOut` (Warning — how many items had settled
  before the deadline cut the rest short), `FanOutBulkheadSaturated` (Warning, emitted **at most
  once per batch** — the first time an item has to wait for the global bulkhead rather than the
  batch's own `maxDegreeOfParallelism`).
- **Metrics** (Prometheus, `WorkflowMetrics.cs` / `PrometheusWorkflowMetrics.RecordFanOutBatch`),
  batch-level only:
  - `workflow_fanout_batch_size` (histogram, labels `task_key`, `workflow`) — items per batch.
  - `workflow_fanout_batch_duration_seconds` (histogram, same labels) — whole-batch wall clock,
    queueing included.
  - `workflow_fanout_item_failures_total` (counter, same labels) — incremented once per batch by
    the batch's failed count, not once per item.

  There is **no per-item duration metric**: an item is a full task execution through the engine,
  so its duration is already captured by the engine's own generic per-task duration metric under
  the inner task's own key. There is also **no live concurrency/saturation gauge** — bulkhead
  pressure is visible only through the one-time-per-batch `FanOutBulkheadSaturated` log line, not
  through a metric you can graph continuously.
- **Spans** (`ActivitySource("BBT.Workflow.Tasks")`, only emitted when verbose tracing is on —
  `AetherTracingRuntime.IsVerbose`): each item gets its own `FanOut.Item` child span, opened
  **before** it waits for either concurrency gate, tagged `vnext.fanout.item.key` and
  `vnext.fanout.item.index` immediately, and `vnext.fanout.item.queue_wait_ms` once its slots are
  acquired — so the trace distinguishes "queued behind the bulkhead" from "the item itself is
  slow". The span's display name is rewritten to `FanOut.Item[{index}] {itemKey}` after the
  inner engine call renames `Activity.Current` in place, so N sibling items do not all end up
  showing the same generic task name in the trace.
- **Straggler detection**: there is no built-in metric that computes it, but the pattern the
  design intends is `max(item duration) / p50(item duration)` read off the per-item spans under
  one batch's trace — a fan-out batch's total time is dominated by its single slowest item, so
  that ratio is the number to look at when a batch runs long. Today that means querying the trace
  backend by the batch's `task.key` tag and the `FanOut.Item` span name, not reading a
  ready-made PromQL series.

## Validation

Split across two layers:

- **`FanOutTask.Configure` (fail-fast, definition time)**: `mode` restricted to `"inline"`;
  `itemsPath` must start with `"$."` when present; `task` reference (`key`/`domain`/`flow`/
  `version`) required; `maxDegreeOfParallelism >= 1`; timeouts positive and
  `itemTimeoutSeconds <= batchTimeoutSeconds`; `join.policy` one of the four valid values;
  `quorum` requires `minSuccess >= 1`.
- **Executor preflight (runtime, cross-component)**: the `itemsPath` XOR `ItemSelector` check
  (needs the mapping compiled first, so it cannot be a pure JSON-schema rule) and the nested
  fan-out rejection (needs the inner task's resolved type). `WorkflowValidator` is not involved —
  FanOut's config lives inside the task component, not the workflow document, so there is nothing
  for the workflow-level validator to check beyond referencing the task correctly.

## Author-beware

- **Nested fan-out is rejected.** If the referenced inner `task` itself resolves to another
  `FanOutTask` (type `21`), the executor fails the batch before running any item. This is not a
  style preference: a nested batch would deadlock against the *same* global bulkhead — the outer
  batch's items would hold every slot they acquired while their own inner items queue for a slot
  that only an outer item releasing could free.
- **Human and Timer inner tasks are legal but almost certainly wrong.** The inner task type is
  deliberately unrestricted — no validator rejects a `HumanTask` or `TimerTask` as the referenced
  `task`. Running either inline, N times in parallel, blocks the fan-out's per-item execution on
  something that is not designed to complete inside a bounded `itemTimeoutSeconds`/
  `batchTimeoutSeconds` window. Nothing stops you from configuring it; nothing about it will work
  the way you expect.
- **`join.ordered: false` is accepted but is a no-op in inline mode.** Results are always
  returned sorted by item index. The field exists for forward schema compatibility with a future
  durable mode that may stream results in completion order.
- **`mode: "durable"` is reserved and rejected.** Only `"inline"` parses today; the field exists
  in the schema now specifically so introducing durable mode later is not a breaking schema
  change.
- **`itemAlias` currently does nothing at runtime.** It is parsed, stored, cloned and reset like
  any other config field, but the executor's default input binding and its log statements do not
  read it. Do not rely on it to change either.

## Key implementation files

| Concern | File |
|---|---|
| Config parsing + validation | `src/BBT.Workflow.Domain/Definitions/Tasks/FanOutTask.cs` |
| Mapping contract | `src/BBT.Workflow.Domain/Scripting/Contracts/IFanOutMapping.cs` |
| Executor (batch orchestration) | `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutTaskExecutor.cs` |
| Join policy evaluation | `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutJoinEvaluator.cs` |
| Batch/item cancellation and classification | `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutBatchCancellation.cs` |
| `itemsPath` resolution + `ItemKey` derivation | `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutItemsResolver.cs` |
| Global bulkhead options + limiter | `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutOptions.cs`, `FanOutConcurrencyLimiter.cs` |
| Public error codes | `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutErrorCodes.cs` |
| DI registration (Orchestration-only) | `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/TaskServiceCollectionExtensions.cs` (`AddTaskExecutors`) |
| Logging | `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` § Fan-Out Execution |
| Metrics | `src/BBT.Workflow.Infrastructure/Monitoring/WorkflowMetrics.cs`, `PrometheusWorkflowMetrics.cs` § Fan-Out Metrics |
| Design spec | `docs/superpowers/specs/2026-08-21-fanout-task-design.md` |
