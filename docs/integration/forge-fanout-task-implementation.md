# Forge Studio — FanOutTask (type 21) Implementation Spec

Audience: a Forge Studio engineer adding designer support for vNext task type `21`. You know Forge;
this page assumes you have never seen fan-out.

Runtime version: `0.0.80` (`common.props`). Everything below was verified against runtime source,
the developer guide (`docs/domain/fan-out-task.md`), the design spec
(`docs/superpowers/specs/2026-08-21-fanout-task-design.md`), `vnext-meta/`, and two real authored
tasks in `burganbank/vnext-contract`. Anything not verifiable in those sources is marked
**[UNVERIFIED]**.

Runtime source of truth, when this page and the engine disagree:

| Concern | File |
|---|---|
| Config parsing + every parse-time rule | `src/BBT.Workflow.Domain/Definitions/Tasks/FanOutTask.cs` |
| Mapping contract + DTOs | `src/BBT.Workflow.Domain/Scripting/Contracts/IFanOutMapping.cs` |
| Batch orchestration | `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutTaskExecutor.cs` |
| Join policy | `.../FanOut/FanOutJoinEvaluator.cs` |
| `itemsPath` + `ItemKey` derivation | `.../FanOut/FanOutItemsResolver.cs` |
| Public error codes | `.../FanOut/FanOutErrorCodes.cs` |
| Global bulkhead | `.../FanOut/FanOutOptions.cs`, `FanOutConcurrencyLimiter.cs` |
| Developer guide | `docs/domain/fan-out-task.md` |

---

## 1. What FanOutTask is

`FanOutTask` (type `21`, discriminator `fanout`) is **dynamic parallelism**: it resolves a
collection from instance data *at runtime*, runs one **referenced inner task** once per item in
parallel, then joins the per-item outcomes into **one** task result and **one** instance-data
write. It exists for the case the author cannot express statically — the item count comes from
data, not from the workflow definition (documents to sign, recipients to notify, accounts to
reconcile). Phase 1 is **inline mode only**: the whole batch runs synchronously inside the
transition that triggered it, bounded by its own batch timeout, with no persisted batch/item state
and no cross-request resumption.

For Forge this means: **a fan-out step is one task on one transition**, exactly like an `HttpTask`
in the model — but its runtime cost, failure surface and output shape are batch-shaped. The
designer's job is to make the batch semantics legible and to catch the config mistakes that the
runtime only discovers at parse or execution time.

### When Forge should steer the author away from it

Surface these as hints, not errors — all three are legal:

- **The downstream has a batch endpoint.** One request for N items beats N parallel calls plus N
  journal rows, N task-engine invocations, N DI scopes.
- **Item N depends on item N-1.** Items run concurrently and independently; there is no ordering
  guarantee between item *executions* (only the result list is re-sorted by index).
- **Progress must be visible before the batch finishes.** Fan-out is single-writer by design;
  nothing reaches instance data until every item has settled.
- **Static, design-time parallelism** (a fixed set of tasks at the same `order`) already works via
  the task coordinator and needs no fan-out.

---

## 2. Where the pieces live in a domain package

Two files, two different places. Getting this wrong is the single most likely Forge bug.

1. **The task component** — `Tasks/<key>.<version>.json`, with `attributes.type = "21"` and
   `attributes.config` carrying everything in §3.
2. **The mapping (`.csx`)** — attached at the **task binding inside the workflow**, not inside the
   task component:

```jsonc
{ "order": 2,
  "task":    { "key": "fan-out-online-document-launch", "domain": "contract",
               "version": "1.0.0", "flow": "sys-tasks" },
  "mapping": { "location": "./src/contract-approval-workflow/FanOutLaunchOnlineDocumentsMapping.csx",
               "code": "<base64 of the .csx>" } }
```

Verified in `vnext-contract/contract/Workflows/contract-approval-workflow.json`. Both shipped
type-21 task components contain **only** `type` + `config` — no `mapping` key. So: the config form
edits the task component; the mapping editor edits the workflow's task binding. A fan-out task
reused on two transitions can carry two different mappings, and Forge must not present the mapping
as a property of the task component.

---

## 3. Config form specification

Full shape, with every default in place:

```jsonc
{
  "attributes": {
    "type": "21",
    "config": {
      "mode": "inline",
      "itemsPath": "$.documents",
      "itemAlias": "document",
      "task": { "key": "process-single-document", "domain": "core",
                "flow": "sys-tasks", "version": "1.0.0" },
      "execution": { "maxDegreeOfParallelism": 4, "itemTimeoutSeconds": 30,
                     "batchTimeoutSeconds": 120 },
      "join": { "policy": "allSettled", "minSuccess": 8,
                "resultKey": "documentResults", "ordered": true },
      "errorBoundary": { "onError": [ /* standard ErrorBoundary rules */ ] }
    }
  }
}
```

### 3.1 Field table

| Field | Type | Required | Default | Allowed values | Notes for the form |
|---|---|---|---|---|---|
| `mode` | string | no | `"inline"` | `"inline"` **only** | Render as a select with one enabled option. `"durable"` is reserved for a later phase and **fails task parsing** (`ArgumentException`, "not supported yet"). Do not offer it, not even disabled-with-tooltip that lets it be saved. |
| `itemsPath` | string | conditional (XOR, §3.2) | none | `"$."`-rooted **dot-path subset** of JSONPath | Property navigation only — **no filters, wildcards, array indices or slices**. Parse rejects anything not starting with `"$."`. Offer autocomplete from the flow's master schema when available; the path must point at an array. |
| `itemAlias` | string | no | none | free text | **Reporting label only.** It is a structured field on `FanOutBatchStarted` and the `vnext.fanout.item.alias` span tag; when unset the runtime substitutes `"item"`. It plays **no role in input binding** — the default binding sets the branch body from the raw item value regardless. Label the field "Item label (logs/traces)" so nobody expects `Data.{alias}`. |
| `task` | object | **yes** | — | — | Missing or non-object ⇒ parse error. |
| `task.key` | string | **yes** | — | non-empty | Inner task reference. |
| `task.domain` | string | **yes** | — | non-empty | All four are required *individually*; the runtime errors per missing property. |
| `task.flow` | string | **yes** | — | non-empty | |
| `task.version` | string | **yes** | — | non-empty | Resolved **once per batch** and cloned per item — not re-resolved per item. |
| `execution.maxDegreeOfParallelism` | int | no | `4` | `>= 1` | Batch-local `SemaphoreSlim`. The low default is deliberate: an unbounded fan-out DDoSes whatever the inner task calls. Warn above ~16 (see §6.2). |
| `execution.itemTimeoutSeconds` | int | no | `30` | `>= 1` **and** `<= batchTimeoutSeconds` | Per-item deadline. |
| `execution.batchTimeoutSeconds` | int | no | `120` | `>= 1` | Whole-batch deadline. Items still running are cancelled and counted `FanOut:BatchTimeout`; `TimedOut` becomes true. |
| `join.policy` | string enum | no | `"allSettled"` | `all` \| `allSettled` \| `quorum` \| `firstSuccess` | Runtime parse is case-insensitive; Forge must still emit exactly these camelCase spellings. |
| `join.minSuccess` | int | **iff** `policy == "quorum"` | none | `>= 1` | Parse fails when `quorum` and `minSuccess` is absent or `< 1`. For other policies it is **silently ignored, with no runtime warning** — so Forge should own that warning. |
| `join.resultKey` | string | no | `"fanOutResults"` | non-empty | The instance-data key the **default** output packaging writes under. Ignored once the mapping overrides `OutputHandler`. |
| `join.ordered` | bool | no | `true` | `true` \| `false` | **No-op in inline mode** — results are always sorted by index. Present so a future durable mode is not a breaking schema change. Render it read-only/disabled with that explanation, or hide it behind an advanced toggle; do not imply `false` changes anything today. |
| `errorBoundary` | object | no | none | standard `ErrorBoundary` (`onError[]`) | **Per-item** boundary: applied independently to every item. A rule that ignores 4xx lets one item be skipped without stopping the batch. Reuse the existing error-boundary editor unchanged; only the label changes ("applies to each item"). |

Note `join.minSuccess: 8` appears alongside `policy: "allSettled"` in the guide's illustrative
snippet — that is legal and inert, not a pattern to copy. Do not seed new forms that way.

### 3.2 Inter-field rules

| Rule | Enforced by the runtime where | What Forge must do |
|---|---|---|
| `itemsPath` **XOR** `ItemSelector` override in the mapping | **Executor preflight (runtime)** — needs the mapping compiled, so no JSON-schema rule can express it | Block publish. Both set ⇒ error. Neither set ⇒ error ("the batch has no item source"). |
| `policy == "quorum"` ⇒ `minSuccess >= 1` | `FanOutTask.Configure` (parse) | Make `minSuccess` appear and become required the moment `quorum` is selected. |
| `minSuccess` set with a non-`quorum` policy | not enforced anywhere | Forge-only warning: "ignored for this policy". |
| `itemTimeoutSeconds <= batchTimeoutSeconds` | `FanOutTask.Configure` (parse) | Cross-field validation on both inputs; clamp suggestions, never silent auto-fix. |
| `mode` must be `"inline"` | `FanOutTask.Configure` (parse) | Single-option select. |
| Inner `task` type must not be `21` | **Executor preflight (runtime)** — needs the inner task resolved | Block publish (§4). |

`WorkflowValidator` is **not** involved in any of this: fan-out config lives in the task component,
not in the workflow document.

---

## 4. Designer-side validation, and why each rule exists

Enforce these before publish. The "why" column matters — a designer that only says *invalid* forces
the author back into the runtime logs.

| # | Rule | Severity | Why |
|---|---|---|---|
| 1 | Exactly one item source: `itemsPath` **or** a mapping overriding `ItemSelector` | error | Both was rejected deliberately: a "path wins, script silently ignored" precedence would be misleading. Neither means the batch has no items to fan out over. Only the designer sees both artifacts at once — the runtime discovers this at execution. |
| 2 | Inner `task` type ≠ `21` (no nested fan-out, depth 1) | error | Not style: a nested batch **deadlocks** against the same process-wide bulkhead. Outer items hold every slot they acquired while their inner items queue for a slot only an outer item could free. Message should say exactly that. |
| 3 | `mode` ≠ `"durable"` | error | Rejected at parse time; a published definition would fail to load. |
| 4 | `quorum` ⇒ `minSuccess >= 1` | error | Parse-time failure otherwise. |
| 5 | `minSuccess` present on a non-`quorum` policy | warning | Silently ignored by the runtime — the author almost certainly meant `quorum`. |
| 6 | `itemTimeoutSeconds <= batchTimeoutSeconds`, both `>= 1`, `maxDegreeOfParallelism >= 1` | error | Parse-time failures. An item deadline longer than the batch deadline can never be reached. |
| 7 | `itemsPath` starts with `"$."` and uses property navigation only | error | Parse rejects a non-`"$."` prefix. Filters/wildcards/indices/slices are **not** supported by the resolver — a path containing `[`, `*`, `?` or `..` will not do what the author reads it as. |
| 8 | `itemsPath` resolves to an array in the flow's schema, when the schema is known | warning | A path resolving to a non-array value throws `InvalidOperationException` at runtime. A *missing* path is **not** an error — it resolves to an empty batch. Do not error on unknown paths. |
| 9 | Inner task reference resolves (`key`/`domain`/`flow`/`version` exist in the registry) | error | The reference is resolved once per batch; an unresolvable one fails the batch, not one item. |
| 10 | Inner task type is `HumanTask` or `TimerTask` | warning | Legal — the inner type is deliberately unrestricted — but wrong: neither completes inside a bounded `itemTimeoutSeconds`/`batchTimeoutSeconds` window, run N times inline. |
| 11 | `join.policy: "quorum"` or `"firstSuccess"` where the item source can be empty | warning | Both **fail** an empty batch (§6.1). Surface it at design time; the author usually assumes "no items, nothing to do, success". |
| 12 | Mapping declares `ItemInputHandler` when the inner task needs per-item config (HTTP/SOAP/Dapr) | warning | The default binding only sets the branch body; it cannot touch a URL, envelope or method (§5.2). |

Rules 1, 2 and 12 are Forge's real value-add: the runtime cannot see them at parse time because
they need the compiled mapping and the resolved inner task.

---

## 5. The mapping (`.csx`) authoring surface

`IFanOutMapping` — three members, one abstract:

```csharp
public interface IFanOutMapping
{
    // optional — default returns null ⇒ "use itemsPath"
    Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context);

    // REQUIRED — no default
    Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item);

    // optional — default returns null ⇒ "use the runtime's default output packaging"
    Task<ScriptResponse?> OutputHandler(ScriptContext context, FanOutResult result);
}
```

### 5.1 Omit-vs-override

| Member | Omit it and you get | Override it when |
|---|---|---|
| `ItemSelector` | the task's `itemsPath` | the collection is **computed**, not readable from a fixed path (union, dedupe, filter) |
| `ItemInputHandler` | *cannot be omitted* | always, if a mapping exists at all |
| `OutputHandler` | the **default packaging**, byte-for-byte identical to a task shipping no mapping | you want your own shape (summary, failed keys, a domain projection) |

Combinations are free. Forge's mapping scaffolder should therefore offer **four** templates, not
one: input-only, input + selector, input + output, all three.

Two runtime details the editor should say out loud:

- **The fallback signal is a `null` *response*, not a null `Data`.** A handler that runs and returns
  `new ScriptResponse { Data = null }` **replaces** the default with nothing.
- **A handler that throws does not fall back.** The batch fails with
  `FanOut task output handler failed: …` and no data.

`ItemInputHandler` is abstract on purpose: it has no return channel that could mean "not
overridden", so a default would have to *silently* perform the flat body binding — and an author who
mistyped the member name would get a batch that compiles, runs, and fires N identical unbound
requests at the inner task's authored endpoint. Keep the signature exact in the scaffold; Forge
should treat a signature mismatch as an error, not a lint.

### 5.2 The two consequences that drive form design

**An HTTP-style inner task *requires* `ItemInputHandler`.** With no mapping the executor does
exactly one thing per item: `branch.SetBody(item.Value)`. That reaches inner tasks which read
`context.Body` (e.g. a `ScriptTask`). It does **not** shape fields that live on the cloned task
instance — an `HttpTask`'s URL or templated body, a `SoapTask`'s envelope, a `DaprServiceTask`'s
method. Those need a handler that mutates the clone:

```csharp
public class BindOnlyMapping : IFanOutMapping
{
    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        if (task is HttpTask http)
        {
            http.SetUrl($"{http.Url}/{item.ItemKey}");
            http.SetBody(new { documentId = item.ItemKey, payload = item.Value });
        }
        return Task.FromResult(new ScriptResponse { Data = new { itemKey = item.ItemKey } });
    }
}
```

**Overriding input costs you nothing on the output side.** That one-member mapping still writes the
default packaging under `join.resultKey`. There is one packaging implementation and both routes
reach it, so a mapping never has to reproduce the default shape in script to keep it. Forge should
*not* scaffold an `OutputHandler` that hand-rolls the default shape.

### 5.3 Default packaging (what `join.resultKey` produces)

Item results under `join.resultKey` as a list of

`{ index, itemKey, isSuccess, data, errorCode, errorMessage, durationMs }`

plus `{resultKey}Summary` = `{ total, succeeded, failed, timedOut }`.

Forge can therefore predict and display the fan-out step's output keys whenever `OutputHandler` is
not overridden — useful for downstream mapping autocomplete and for auto-transition condition
builders. When it *is* overridden, the output shape is whatever the script returns; do not guess.

### 5.4 DTOs and `ItemKey`

```csharp
public sealed record FanOutItem(int Index, dynamic? Value, string ItemKey);

public sealed record FanOutResult(int Total, int Succeeded, int Failed, bool TimedOut,
                                 IReadOnlyList<FanOutItemResult> Items);

public sealed record FanOutItemResult(int Index, string ItemKey, bool IsSuccess,
                                      dynamic? Data, string? ErrorCode, string? ErrorMessage,
                                      TimeSpan Duration);
```

There is **no `Attempts` field** — retry counts live in the item's `InstanceTask` journal row and
retry span events, not in the result.

**`ItemKey` derivation** (`FanOutItemsResolver.ExtractItemKey`): the item object's `id` string
property if present, else its `key` string property, else the zero-based index as a string.
Uniform across `itemsPath` items (`JsonElement`) and `ItemSelector` items (`JsonElement`,
`ExpandoObject`/`IDictionary<string,object?>`, or an arbitrary CLR object read by reflection — e.g.
an anonymous type a `.csx` selector returns). Worth surfacing in the designer: **if the items carry
an `id` or `key`, every log line, span and result row becomes addressable by it for free**;
otherwise operators are stuck reading indices.

### 5.5 Purity — the rule to state in the mapping editor

Every item runs on its own isolated branch `ScriptContext` (`CreateParallelBranch()`) and its own DI
scope, through the **full** task engine (own retry, own per-item error boundary, own `InstanceTask`
journal row keyed `{fanOutTaskKey}#{index}`) with `SuppressDataApply = true`. The branch is
**discarded**, never merged back. So `ItemInputHandler` **must be pure with respect to instance
data** — any write it attempts is either lost or races its siblings. `OutputHandler` (or the default
packaging in its place) is the single write point: **one fan-out execution ⇒ one `InstanceData`
patch**, regardless of batch size.

Show this as a persistent note in the item-handler editor, not a tooltip.

---

## 6. Runtime behaviour the designer must reflect

### 6.1 Join policy

| `join.policy` | Batch succeeds when | Empty batch (0 items) | Early stop |
|---|---|---|---|
| `all` | every item succeeded **and** the batch did not time out | **succeeds** (vacuously) | cancels the rest on the first failure |
| `allSettled` | **always** — partial failure is data, not an error; succeeds even if the batch timed out | **succeeds** | none |
| `quorum` | `succeeded >= minSuccess`, regardless of `timedOut` | **fails** | none |
| `firstSuccess` | `succeeded >= 1`, regardless of `timedOut` | **fails** | cancels the rest on the first success |

The empty-batch asymmetry is intentional and must be shown, not smoothed over: `quorum` and
`firstSuccess` are threshold policies and zero can never clear a threshold of at least one.
`firstSuccess` is definitionally `quorum(minSuccess = 1)`; the two must never diverge on the same
input, empty batch included.

Two more facts for the policy picker's help text:

- A **failed join still carries its full result data** on the task's output — a caller branching on
  which items failed needs that data even when the task itself is marked failed.
- A failed join is an **ordinary failed task** inside its own transition: the workflow's normal
  Task → State → Global error-boundary chain runs. Fan-out adds no new boundary concept at that
  level.

**The recommended partial-failure pattern**, and the one Forge should offer as a one-click
affordance: `policy: "allSettled"` + an auto-transition condition on
`{resultKey}Summary.failed > 0` routing to a `partial-failure` state, `failed == 0` continuing the
happy path (evaluated by `RunAutomaticTransitionsStep`, order 80). The platform does not decide
this; it is a workflow-design choice every time.

### 6.2 Two-level concurrency ceiling

Two independent caps, acquired in this order:

1. **Batch-local** — `execution.maxDegreeOfParallelism` (default `4`), a `SemaphoreSlim` scoped to
   this one batch.
2. **Process-wide** — `Workflow:FanOut:MaxConcurrentItems` (default `64`, `FanOutOptions`), a single
   singleton semaphore shared by **every** fan-out batch in the process, across every instance and
   workflow.

Effective concurrency per item is `min(batch's remaining slots, remaining global slots)`. This is
what stops N concurrent instances from multiplying into `N × maxDegreeOfParallelism` simultaneous
downstream calls: 100 instances each at `maxDegreeOfParallelism: 5` is 500 potential calls; the
global bulkhead holds the process total at `MaxConcurrentItems` regardless.

Designer consequences:

- `maxDegreeOfParallelism` is **not** a throughput dial the author fully controls. Its help text
  must name the global ceiling and that the ceiling is **runtime configuration, not authorable** —
  Forge cannot read or set it. Warn when `maxDegreeOfParallelism` alone approaches the default 64.
- The bulkhead is **per-process, not distributed** — a distributed counter was deliberately left out
  of scope (a network round trip per item). So the real ceiling scales with pod count; do not
  present it as a domain-wide guarantee.
- `MaxConcurrentItems` is validated at startup (`[Range(1, int.MaxValue)]`, `ValidateOnStart()`) —
  a misconfigured `0` fails the boot instead of deadlocking silently. Not Forge's problem, but
  useful in an operator-facing tooltip.

### 6.3 Per-item error boundary

`config.errorBoundary` is applied **independently to every item** (retry / fallback). If retries are
exhausted the item enters the result set as failed; it does not stop the batch on its own — the join
policy decides. Reuse the existing error-boundary editor; change only the framing.

### 6.4 Error codes authors branch on

`FanOutErrorCodes` is a **public contract** — offer these as autocomplete values in output handlers,
auto-transition conditions and error-boundary rules:

| Code | Meaning |
|---|---|
| `FanOut:ItemTimeout` | the item exceeded its own `itemTimeoutSeconds`; **takes priority** over the causes below |
| `FanOut:BatchTimeout` | the item was cut short because the batch hit `batchTimeoutSeconds` |
| `FanOut:ItemCancelled` | cancelled by the join policy's early stop (`firstSuccess` already succeeded, or `all` already failed) |
| `FanOut:ItemNotStarted` | cancelled while still queueing for a concurrency slot, with no deadline or early stop to explain it |
| `FanOut:ItemFailed` | fallback: the inner task failed or threw with no more specific fan-out code |

The inner task's **own** error code, when it has one, **passes through unchanged** — so an
error-boundary rule editor must accept both fan-out codes and inner-task codes (`Task:503`,
`409`, …) in the same list.

### 6.5 Host boundary

The executor and its DI registration (`FanOutTaskExecutor`, `FanOutOptions`,
`FanOutConcurrencyLimiter`) are **Orchestration-local only** — the Execution host never registers
them, because fan-out has no remote invoker of its own. Remote inner task types still cross to
Execution through their own existing invokers. Relevant if Forge ever surfaces "where does this
run".

---

## 7. Canvas visualisation

**It is one transition, not N branches.** Do not draw N parallel edges, N nodes, or a gateway
split-and-join. The batch has no persisted per-item state and no per-item pipeline step; drawing it
as a subgraph would misrepresent both the model and the debugging surface.

Draw it as a **single task node with an expansion marker** — the established BPMN vocabulary for
this is the multi-instance marker (three short parallel bars at the bottom of the activity), which
is exactly the right semantics: one activity, N runtime instances of it, unknown at design time.
Use a distinct accent from ordinary tasks so a fan-out step is scannable in a large flow.

At a glance on the node (no expansion, no hover):

1. **Inner task reference** — `key@version` of the referenced task, and its type badge, since the
   inner type is what the reader actually cares about ("this fans out an HTTP call").
2. **Item source** — the literal `itemsPath` when set, or the badge `ItemSelector (script)` when the
   mapping computes it. These are mutually exclusive, so one slot suffices.
3. **`maxDop`** — e.g. `×4`, with the global-ceiling caveat in the tooltip.
4. **Join policy** — the policy name, plus `minSuccess` when `quorum`. Consider a warning glyph on
   `quorum`/`firstSuccess` for the empty-batch rule.

On selection / in the inspector, add: `itemAlias`, both timeouts, `resultKey` **only when
`OutputHandler` is not overridden** (otherwise it is inert and showing it misleads), whether a
per-item error boundary exists, and which `IFanOutMapping` members the mapping actually overrides —
that last one is the highest-value line in the whole panel, because it determines input binding and
output shape.

Navigation affordances worth having:

- Click-through from the node to the **inner task component**, and from the node to the **mapping
  editor** for *this binding* (§2 — mapping is per binding, so the link must be binding-scoped).
- If the same fan-out task is bound on several transitions, show that count; the mappings may differ.

Do **not** render: `join.ordered` as a meaningful setting (no-op inline); a `mode` selector with more
than one option; any per-item progress indicator (nothing is observable mid-batch by design).

---

## 8. Observability Forge can deep-link to

| Kind | Name | Notes |
|---|---|---|
| Log | `FanOutBatchStarted` (Information) | task key, item count, `itemAlias` (or `"item"`), `maxDegreeOfParallelism`, join policy, instance id |
| Log | `FanOutItemFailed` (Warning) | one per failed item — item key, index, error code. Warning, not Error: a failed item is a recoverable outcome the join policy decides on |
| Log | `FanOutBatchCompleted` (Information) | total / succeeded / failed / duration |
| Log | `FanOutBatchTimedOut` (Warning) | how many items had settled before the deadline cut the rest |
| Log | `FanOutBulkheadSaturated` (Warning) | emitted **at most once per batch**, the first time an item waits on the *global* bulkhead rather than the batch's own cap |
| Metric | `workflow_fanout_batch_size` (histogram; `task_key`, `workflow`) | items per batch |
| Metric | `workflow_fanout_batch_duration_seconds` (histogram; same labels) | whole-batch wall clock, queueing included |
| Metric | `workflow_fanout_item_failures_total` (counter; same labels) | incremented **once per batch** by the failed count, not once per item |
| Span | `FanOut.Item` (`ActivitySource("BBT.Workflow.Tasks")`) | **only when verbose tracing is on** (`AetherTracingRuntime.IsVerbose`) |

Log EventIds are in the `101xx` block (`WorkflowLogs.cs` § Fan-Out Execution).

Item spans open **before** the item waits on either concurrency gate, tagged
`vnext.fanout.item.key`, `vnext.fanout.item.index`, `vnext.fanout.item.alias` immediately, plus
`vnext.fanout.item.queue_wait_ms` once slots are acquired — so a trace distinguishes "queued behind
the bulkhead" from "the item itself is slow". Display name becomes
`FanOut.Item[{index}] {itemKey}`.

Two gaps to design around:

- **Per-item spans exist only at Verbose detail.** Any Forge "open the trace for this batch" UI must
  say so, or an operator on default tracing will conclude the feature is broken. There is no
  per-item duration *metric* at all — an item is a full task execution, so its duration is captured
  by the engine's generic per-task metric under the **inner task's** key.
- **No live concurrency/saturation gauge.** Bulkhead pressure is visible only through the
  once-per-batch `FanOutBulkheadSaturated` log line. A Forge "is my fan-out being throttled?" panel
  must be log-driven, not metric-driven.

**Straggler detection** has no built-in metric. The intended read is
`max(item duration) / p50(item duration)` off the per-item spans of one batch's trace — a batch's
total time is dominated by its single slowest item. Today that means querying the trace backend by
the batch's `task.key` tag and the `FanOut.Item` span name.

---

## 9. Known gaps and dependencies

| Item | Status |
|---|---|
| **`@burgan-tech/vnext-schema` has no type 21** | **BLOCKING for publish-time validation.** Verified in the copy installed in `vnext-contract` (`@burgan-tech/vnext-schema` **0.0.49**): `task-definition.schema.json` `attributes.type` enum is `['1'…'17']`, and its per-type `allOf` branches cover 1–7, 10–17. No `21`, and no `config` branch for it. A separate task is adding it; **Forge's schema-driven form and publish gate depend on that release**. Until then, Forge must carry the §3 rules itself and must not reject type 21 merely because the bundled schema does not know it. The exact contents of the latest *published* schema (`0.0.52`, per `version-manifest.json` for runtime `0.0.79`) are **[UNVERIFIED]** — only the locally installed 0.0.49 was inspected. |
| `vnext-meta/features.json` → `engine.fanOutTask` | **Present and thorough.** `since: 0.0.80`, `status: stable`, `schemaPath: "task.type=21"`, with a long description covering both concurrency levels, all four join policies, the empty-batch rule, the nested-fan-out rejection, the single-write invariant and all five error codes. Forge can feature-gate on this. Note it carries **no `relatedConfig`** array, so `Workflow:FanOut:MaxConcurrentItems` is discoverable only inside the prose description. |
| `vnext-meta/component-registry.json` → `tasks[0]` | **Present but thin.** `{ key: "fanout", since: "0.0.80", stable: true, domains: [], configSchema: "<one prose line>" }`. The `configSchema` value is a **human sentence, not a machine-readable schema** — Forge cannot build a form from it. Also: `fanout` is the **only** entry in `tasks`, and `functions` and `extensions` are **empty arrays**, so the registry is not yet a usable catalog for anything else. |
| `vnext-meta/performance-profiles.json` | **Empty** (`{"profiles": []}`). No published limits for batch size, `maxDegreeOfParallelism`, or the global bulkhead. Forge's warning thresholds (§4 rule 10, §6.2) have **no metadata backing** and must be hardcoded or made configurable until profiles land. Recommended gap to file: a fan-out profile carrying `maxDegreeOfParallelism` guidance and the `MaxConcurrentItems` default. |
| `vnext-meta/known-issues.json` | Non-empty overall, **no fan-out entries**. |
| `vnext-meta/deprecations.json` | **No fan-out entries** (correct — nothing is deprecated). |
| `vnext-meta/version-manifest.json` | **Has no `0.0.80` row** — newest is `0.0.79 → schemaVersion 0.0.52`, while `common.props` is already `0.0.80`. Since both `features.json` and the registry say `since: 0.0.80`, a Forge version gate reading the manifest cannot resolve the runtime version fan-out shipped in. Gap to file with the runtime team. |
| `mode: "durable"` | Future phase. Reserved in the schema so adding it later is not breaking; **rejected at parse time today**. No persisted batch/item state, no cross-request resumption in Phase 1. |
| `join.ordered` | Accepted, **no-op** in inline mode; forward-compatibility only. |
| No distributed concurrency cap | Deliberate, out of scope (latency per item). Real ceiling scales with pod count. |
| `FanOutItemResult.Attempts` | Does not exist. Retry visibility is journal rows + retry span events only. |

---

## 10. Worked example (from `vnext-contract`, annotated)

Real, shipped, and the best thing to build the form against.

### 10.1 `itemsPath` + input-binding-only mapping

`vnext-contract/contract/Tasks/fan-out-online-document-launch.1.0.0.json`:

```jsonc
{
  "key": "fan-out-online-document-launch",
  "domain": "contract",
  "version": "1.0.0",
  "flow": "sys-tasks",
  "flowVersion": "1.0.0",
  "tags": ["subprocess", "online-document", "fan-out", "parallel"],
  "attributes": {
    "type": "21",
    "config": {
      "_comment": "…domain teams use a _comment key for intent; harmless, and Forge should preserve it verbatim on round-trip…",
      "mode": "inline",
      "itemsPath": "$.documents.online",        // item source = path. Mapping must NOT override ItemSelector.
      "itemAlias": "document",                   // logs/spans only
      "task": {                                  // inner task = SubProcessTask (type 14), UNCHANGED
        "key": "launch-online-document-subprocesses",
        "domain": "contract", "flow": "sys-tasks", "version": "1.0.0"
      },
      "execution": {
        "maxDegreeOfParallelism": 4,             // low on purpose: each item creates an instance AND calls an API
        "itemTimeoutSeconds": 30,
        "batchTimeoutSeconds": 120
      },
      "join": {
        "policy": "allSettled",                  // a failed launch is recorded, not fatal
        "resultKey": "onlineLaunchResults",       // INERT here — the mapping overrides OutputHandler
        "ordered": true                           // no-op inline
      }
    }
  }
}
```

Form-building notes:

- Everything in `execution` and `join.ordered` here equals the defaults — Forge should still write
  them explicitly (this is the shipped house style) but must not treat their presence as intent.
- `join.resultKey` is set **and unused**, because the mapping overrides `OutputHandler`. This is
  exactly the case where the inspector should grey `resultKey` out and say why — the author left a
  misleading value behind.
- Authors document intent in a `_comment` key inside `config`. Preserve unknown keys on round-trip;
  do not strip them.

Its mapping, `Workflows/src/contract-approval-workflow/FanOutLaunchOnlineDocumentsMapping.csx`,
overrides `ItemInputHandler` + `OutputHandler` (not `ItemSelector`, because `itemsPath` is set):

```csharp
public class FanOutLaunchOnlineDocumentsMapping : ScriptBase, IFanOutMapping
{
    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        var triggerTask = task as SubProcessTask
            ?? throw new InvalidOperationException("FanOut inner task must be a SubProcessTask");

        // Per-item identity is derived from the item's index — a duplicate dispatch targets the
        // SAME child, the runtime answers 409, and the inner task's acceptedStatusCodes absorbs it.
        triggerTask.SetKey($"{context.Instance.Id}-online-{item.Index}");
        triggerTask.SetBody(new { /* projected from item.Value + context.Instance.Data */ });
        triggerTask.SetHeaders(new Dictionary<string, string?> { /* clientid, accept-language, … */ });

        return Task.FromResult(new ScriptResponse { /* audit data only — never merged */ });
    }

    public Task<ScriptResponse?> OutputHandler(ScriptContext context, FanOutResult result)
    {
        // Reads documents.online from INSTANCE DATA, not from the result set: item branches were
        // discarded, so this handler still sees the pre-batch array and rewrites it wholesale.
        // One writer, one write — the race has no window.
        foreach (var item in result.Items) { if (!item.IsSuccess) continue; /* … */ }
        return Task.FromResult<ScriptResponse?>(new ScriptResponse { /* single patch */ });
    }
}
```

The two lines worth lifting into Forge's mapping-editor help: the item handler mutates **only the
cloned task**, and the output handler reads **instance data**, not the branch contexts (they are
gone).

### 10.2 `ItemSelector` + per-item error boundary

`vnext-contract/contract/Tasks/fan-out-finalize-subprocesses.1.0.0.json` — the mirror case:

```jsonc
{
  "attributes": {
    "type": "21",
    "config": {
      "mode": "inline",
      // NO itemsPath — the list is COMPUTED (documents.online, then documents.offline, then ids
      // present only in tracking.subprocess.instanceIds, deduped, order preserved), so
      // FanOutFinalizeSubprocessesMapping.ItemSelector produces it. This is the XOR's other leg.
      "itemAlias": "subprocess",
      "task": { "key": "notify-subprocesses-finalize", "domain": "contract",
                "flow": "sys-tasks", "version": "1.0.0" },   // DirectTriggerTask, type 12
      "execution": { "maxDegreeOfParallelism": 4, "itemTimeoutSeconds": 30, "batchTimeoutSeconds": 120 },
      "join": { "policy": "allSettled", "resultKey": "finalizeResults", "ordered": true },
      "errorBoundary": {
        "onError": [
          { "action": 3,                                       // Ignore, per item
            "errorCodes": ["400", "404", "409", "Task:400", "Task:404", "Task:409"],
            "priority": 1 }
        ]
      }
    }
  }
}
```

Notes that matter for the form:

- **This task must not offer an `itemsPath` field as "empty/optional"** once Forge sees the mapping
  overrides `ItemSelector` — filling it in would break rule 1. The two legs of the XOR should be a
  single radio: *path* or *script*.
- `errorBoundary.action` is the **numeric** enum in real definitions (`3` = Ignore). Forge must map
  labels to the same numeric encoding the existing error-boundary editor already uses.
- Deliberately **no `"*"` rule**: an unexpected error still surfaces and the output handler flags it
  `unexpected = true`. Worth a scaffolder hint — a catch-all `"*"` + Ignore silently hides real
  failures behind `allSettled`.
- Its selector returns items carrying `id`, so `ItemKey` becomes the subprocess instance id and
  every log line, span and result row is addressable by it (§5.4). Forge's selector scaffold should
  recommend emitting `id` or `key` for exactly this reason.

Its `ItemSelector` shape:

```csharp
public Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context)
{
    var targets = new List<dynamic>();
    // union + dedupe over instance data; anonymous objects carrying `id` are fine —
    // ExtractItemKey reads `id` (then `key`, then the index) by reflection.
    targets.Add(new { id = idStr, flow = /* … */, isDecided = /* … */ });
    return Task.FromResult<IEnumerable<dynamic>?>(targets);
}
```
