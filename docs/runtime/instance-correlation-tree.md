# Instance Correlation Tree

A built-in system function that answers one question: **what did this instance spawn, and what did
those spawn?** It walks the instance's `InstanceCorrelation` rows **parent → child**, recursively, and
returns the result as a tree rooted at the instance you asked about.

```
GET /{domain}/workflows/{workflow}/instances/{instance}/functions/instance-correlation
```

It is an `IInstanceFunctionHandler` registration dispatched by the `{function}` route segment
(`InstanceCorrelationFunctionHandler`, key in `FunctionTypeConst`), like `state`, `data` and `tasks` —
so a custom function named `instance-correlation` is shadowed, the same rule every system function key
has. `{instance}` accepts the instance id or business key.

> **Renamed in 0.0.98 — hard break.** This function was previously `…/functions/hierarchy`. There is
> **no alias**: the old path falls through to custom-function resolution and answers `404`
> (`Cache:300001`). The name changed because the tree is built purely from `InstanceCorrelation` rows,
> so it now says what it actually reads. The MCP tool `get_instance_hierarchy` became
> `get_instance_correlation` in the same change. See `vnext-meta/deprecations.json`.

## Direction: strictly downward

The walk is `GetByParentAsync(id)` → build a node per correlation → recurse into that child. So a
caller always sees **itself as the root and its descendants beneath it — never its ancestors**. Asking
about a leaf returns that leaf with an empty `children` list, not the chain above it.

Both correlation kinds appear, told apart by `subFlowType`: `S` (SubFlow, blocking) and `P`
(SubProcess, fire-and-forget). **Completed correlations stay in the tree** — the repository read is
deliberately unfiltered, so this is the full history of what an instance spawned, not only what is
still running.

## Response

```jsonc
{
  "root": {
    "id": "29f116ad-…",                  // instance id
    "key": "order-4711",                 // business key
    "flow": "subflow-orchestration-parent",
    "domain": "core",
    "flowVersion": "1.0.0",
    "currentState": "parent-subflow-state",
    "ownState": "parent-subflow-state",
    "status": "B",
    "isCompleted": false,
    "href": "/api/v1/core/workflows/subflow-orchestration-parent/instances/29f116ad-…",
    "children": [
      {
        "id": "b673dbfe-…",
        "flow": "subflow-orchestration-child",
        "currentState": "grandchild-initial",   // ← deepest active descendant
        "ownState": "child-subflow-state",      // ← where THIS node actually is
        "status": "B",
        "subFlowType": "S",
        "isCompleted": false,
        "parentState": "parent-subflow-state",
        "correlationId": "4367cf1e-…",
        "createdAt": "2026-09-30T11:27:29.869552Z",
        "stateChangedAt": "2026-09-30T11:27:30.662794Z",
        "href": "/api/v1/core/workflows/subflow-orchestration-child/instances/b673dbfe-…",
        "children": [ /* … recursive, same shape … */ ]
      }
    ]
  }
}
```

| Field | Meaning |
| --- | --- |
| `id`, `key` | The instance this node represents. |
| `flow`, `domain`, `flowVersion` | Which definition it is an instance of. |
| `currentState` | **On a child**: the correlation's tracked state, which reports the **deepest active descendant**. **On the root**: its own state (nothing tracks the root), so root `currentState` always equals root `ownState`. See the caveat below. |
| `ownState` | Where **this node itself** is, read from the instance row. Use this to place a node. |
| `status` | Instance status: `B` Busy, `A` Active, `C` Completed, `P` Passive, `F` Faulted. |
| `subFlowType` | `S` SubFlow (blocking) / `P` SubProcess. **Absent on the root.** |
| `isCompleted`, `completedAt`, `terminalOutcome` | Describe the **link**, not the instance — see below. |
| `parentState` | The parent state this child was spawned from. Absent on the root. |
| `correlationId` | The correlation row's own id — the handle for addressing the **link** rather than the instance. Absent on the root. |
| `createdAt` | When the correlation was created, i.e. when this child was spawned. Absent on the root. |
| `stateChangedAt` | When the child's tracked state last moved ("sitting here since"). Absent on the root. |
| `href` | Link to this node's own **instance resource**, so a caller need not compose URLs. Note the state function's `correlations[].href`, over the same correlation row, points at the **data function** instead — same field name, different target. |
| `children[]` | Recursive, same shape. |

**Nulls are omitted, not emitted as `null`** (the runtime serializes with `WhenWritingNull`). That is
why the root carries no `subFlowType`, `parentState`, `correlationId`, `createdAt`, `stateChangedAt` or
`terminalOutcome` — the root is nobody's correlated child, so every link-scoped member is absent by
construction. Do not write a client that requires those keys.

## Four things clients get wrong

**1. `isCompleted` describes the LINK, not the instance.** A child can be `status: "C"` (the instance
finished) while `isCompleted: false` (the parent's correlation is still open) — that is the SubFlow
completion window, and it is a normal, observable state. Real example from a live domain:

```jsonc
{ "flow": "start-video-call", "currentState": "completed", "status": "C", "isCompleted": false }
```

**2. `isCompleted` says *whether*, `terminalOutcome` says *how*.** The outcome is `completed`,
`faulted` or `canceled` (the enum is serialized camelCase). A UI that colours a node green on
`isCompleted` alone will paint a faulted child green. Both are absent while the link is still open.

**3. The root is not descended.** `GET …/functions/state` on the same instance descends into the
active subflow and reports the deepest leaf's state, but this tree's **root** reports the root's own
state. For an instance holding an open subflow the two surfaces therefore disagree about "the" state —
by design: the tree's job is to show each level separately.

**4. `currentState` is not the node's own state.** It carries the correlation's tracked state, which
bubbles up from the deepest active descendant — so a child that has an active subflow of its own
reports the *grandchild's* state. That is pre-existing behaviour and is deliberately unchanged;
**`ownState` was added alongside it** for callers that need to place each node where it actually is.
A tree or graph view should read `ownState`.

The same divergence also appears **transiently on a leaf child with no subflow of its own**: the child's
own row commits first and `SubflowStateService` bubbles the state onto the correlation afterwards
(relay + Inbox backup), so during that normally sub-second window `ownState` can be ahead of
`currentState`. That is propagation lag, not a bug.

## How the walk runs

The tree is expanded **one level at a time, batched by hop**, where a hop is a
`(domain, flow, version)` group:

```
A (root, local)
├── B  subflow, closed, same domain  ─┐ one hop, one pair of queries
├── D  subprocess, same domain       ─┘
└── C  subflow, active, OTHER domain ── separate hop -> POST .../internal/correlations/batch
                                         ^ sibling hops run concurrently
```

- **One correlation read and one projected instance read per hop**, not two per node
  (`IInstanceCorrelationRepository.GetByParentsAsync`). A level of five siblings in one flow costs
  two queries, not ten. The instance read is a **SQL projection** of the four columns the walk
  actually uses (`CorrelationWalkRow`) — no aggregate is materialised, so a tree walk never
  transfers whole instance rows, and there is no partially loaded aggregate for a later reader to
  misuse.
- **A cross-domain branch costs ONE call.** The far side re-enters the same resolver and recurses
  locally, so a six-level subtree in a partner domain is one remote call, not six. This is also
  what makes a cross-domain child's `key`, `ownState` and live `status` real — only the domain that
  owns an instance can read its row, so each hop reports *self-data* for the instances it was asked
  about and the parent merges it into the node it built from the correlation row.
- **Sibling hops run in parallel**, bounded twice: `FanoutParallelism` (default 8) per request, and
  a process-wide `MaxConcurrentHops` (default 32). Both matter — each branch holds its own unit of
  work and therefore its own pooled connection, and this endpoint has no single-flight, so N
  callers are N independent walks. The human-task fan-out established the bound the expensive way
  (`53300 sorry, too many clients already`); see `CorrelationHopLimiter`.
- **Depth is still serial, inherently.** A level cannot be grouped until its parent has answered.
  Only the width parallelises.

Configuration lives under `Workflow:InstanceCorrelation` and is validated at startup.

## Incomplete answers are explicit

Every node carries `resolved`, and `unresolvedReason` when it is false. **The node itself is always
real — only its descendants are in question.**

| `unresolvedReason` | Meaning |
| --- | --- |
| `depth-exceeded` | `MaxDescentDepth` (default 20) ran out. On a graph that cannot legitimately nest that deep, this is the first symptom of a cycle. |
| `hop-failed` | A hop could not be expanded — most often an unreachable partner domain. **Only that branch is truncated**; the rest of the tree arrives intact and the call still returns 200. |
| `instance-missing` | The row could not be read in the domain that should own it. READ COMMITTED permits it to vanish between the parent's correlation read and the child's own. |

A tree whose nodes are all `resolved: true` is complete. Do not treat an empty `children` on an
unresolved node as "no children" — that conflation is exactly what the old implementation forced on
callers for every cross-domain child.

## Known limits

- **No cycle guard.** `MaxDescentDepth` bounds the walk, but nothing tracks visited ids. Safe today
  because correlations are written only at spawn time (`HandleSubFlowStep`, `SubProcessTaskExecutor`),
  which cannot produce a cycle; a visited-set becomes worthwhile if post-hoc correlation
  registration lands (vnext-client-sdk-core#58 AB-20).
- **No cap on total nodes.** A pathologically wide tree is bounded only by depth and by the per-hop
  batch limit (`CorrelationBatchRequest.MaxInstanceIds` = 500).
- **The `{workflow}` route segment is not validated** against the instance's real flow. A mismatched
  key still answers `200` with the correct tree, only with an empty root `flowVersion`.

## Authorization

The handler carries no in-process gate, like the other read surfaces: `queryRoles` is answered by
`GET …/functions/authorize?queryRoles=true`, which the Internal Gateway consults. See
[The `authorize` Function](../domain/authorize-function.md).

## Implementation map

- Handler: `InstanceCorrelationFunctionHandler` (orchestration), key `FunctionTypeConst.InstanceCorrelation`.
- Read: `InstanceQueryAppService.GetInstanceCorrelationAsync` → `InstanceCorrelationResolver` (batched, per-hop).
- Hop routing: `IInstanceCorrelationGateway` → Routed / Local / Remote, on `IRuntimeInfoProvider.IsDomainMatch`.
- Internal endpoint: `POST /{domain}/workflows/{workflow}/internal/correlations/batch`.
- Bounds: `InstanceCorrelationOptions` (`Workflow:InstanceCorrelation`), `CorrelationHopLimiter`.
- Source rows: `IInstanceCorrelationRepository.GetByParentsAsync` (batched; active **and** completed).
- DTOs: `GetInstanceCorrelationInput` / `GetInstanceCorrelationOutput` / `InstanceCorrelationNode`.
- URL template: `InstanceUrlTemplates.InstanceCorrelationTemplate`.
- Telemetry read-kind: `InstanceReadKinds.InstanceCorrelation` (`instanceCorrelation`) — the span is
  `Instance.Read/instanceCorrelation`; it was `Instance.Read/hierarchy`, so saved dashboard queries
  filtering on the old value need updating.
