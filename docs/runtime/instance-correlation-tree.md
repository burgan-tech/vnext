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

**2. `isCompleted` says *whether*, `terminalOutcome` says *how*.** The outcome is `Completed`,
`Faulted` or `Canceled`. A UI that colours a node green on `isCompleted` alone will paint a faulted
child green. Both are absent while the link is still open.

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

## Known limits

- **N+1 reads.** One `GetByParentAsync` per node plus one instance read per child, recursively. A wide
  or deep tree costs proportionally many round trips.
- **No depth cap and no cycle guard.** Safe today because correlations are written only at spawn time
  (`HandleSubFlowStep`, `SubProcessTaskExecutor`), which cannot produce a cycle. **Both become
  mandatory if post-hoc correlation registration lands** (vnext-client-sdk-core#58 AB-20), since an
  explicitly registered link could close a loop.
- **Cross-domain children degrade.** A child in another domain is read from the local schema, so its
  instance row is not found and the node falls back to correlation-carried data (`key`, `ownState` and
  live `status` come back null/derived).
- **The `{workflow}` route segment is not validated** against the instance's real flow. A mismatched
  key still answers `200` with the correct tree, only with an empty root `flowVersion`.

## Authorization

The handler carries no in-process gate, like the other read surfaces: `queryRoles` is answered by
`GET …/functions/authorize?queryRoles=true`, which the Internal Gateway consults. See
[The `authorize` Function](../domain/authorize-function.md).

## Implementation map

- Handler: `InstanceCorrelationFunctionHandler` (orchestration), key `FunctionTypeConst.InstanceCorrelation`.
- Read: `InstanceQueryAppService.GetInstanceCorrelationAsync` → `BuildCorrelationTreeAsync` (recursive).
- Source rows: `IInstanceCorrelationRepository.GetByParentAsync` (active **and** completed).
- DTOs: `GetInstanceCorrelationInput` / `GetInstanceCorrelationOutput` / `InstanceCorrelationNode`.
- URL template: `InstanceUrlTemplates.InstanceCorrelationTemplate`.
- Telemetry read-kind: `InstanceReadKinds.InstanceCorrelation` (`instanceCorrelation`) — the span is
  `Instance.Read/instanceCorrelation`; it was `Instance.Read/hierarchy`, so saved dashboard queries
  filtering on the old value need updating.
