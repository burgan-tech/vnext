# Subflow Descent Trace — Implementation Plan

**Source analysis:** [`../specs/2026-08-31-builtin-function-subflow-trace-analysis.md`](../specs/2026-08-31-builtin-function-subflow-trace-analysis.md) §4
**Predecessors:** `2026-08-25-trace-span-tree-design.md` (write path), `2026-08-30-trace-episode-separation.md` (transport vs business)

This plan makes the **read** path's subflow descent visible. The write path was covered by the two
predecessors; the read path has never had a span of its own.

---

## Outcome (implemented 2026-08-31)

Phases A, B, C1 and C2 are done; D1 is done; **C3 and D2 are not** (see below). Build clean, no new
failing tests: Application 16/1774, Domain 27/2318, Infrastructure 16/316 — same failing names as
before, +12 new passing.

Three things went differently than the plan assumed. All three are improvements, but they change what
was actually delivered:

**FAZ B needed no code change — A2 delivered it, and the analysis overstated the bug.**
`ActivitySource.StartActivity` sets `Activity.Current`, so once each level opens a descent span, the
existing `Activity.Current?.SetTag(...)` at `InstanceQueryAppService` :92 and :703 already writes to
that level's own span. Moving the call was unnecessary.

Worse for the original claim: the tag in question is `RootInstanceId`, and `GetRootInstanceId()` reads
the instance's own `RootInstanceId` extra-property — which is the **same value at every level of one
chain**. So the levels were overwriting each other with identical data. The structural hazard analysis
§2.2 described was real; the concrete defect it named was not. Recorded here rather than quietly
dropped, because §2.2 will otherwise keep being cited as a fixed bug.

**C1 needed no DTO change.** The plan added `SubflowDepth` to `GetFunctionWithInstanceInput`. With an
`AsyncLocal` carrying depth in-process and a header carrying it across domains, that property would
have had no reader — dead weight on a contract type. Skipped.

**A real defect was found and fixed while writing D1: the explicit-parent overload severs baggage.**
`StartActivity(name, kind, Activity.Current?.Context)` sets `ParentSpanId` but leaves
`Activity.Parent` null, and baggage is inherited through the Activity *chain*, not the context. A
descent span created that way cuts baggage off for everything nested under it — including the
cross-domain read one level down, which forwards `X-Root-Instance-Id` by reading that baggage back
out in `CurrentUserForwardHeadersHelper`. Silent, and only visible on a remote hop.

Both new spans now use the implicit-parent overload, and `ADescent_InheritsTheCallersBaggage` pins it.
This is the same "explicit-parent-context defect" `docs/runtime/trace-span-tree.md` already notes for
`Discovery.Resolve` and `Instance.Query.Prepare` — **those two are still affected** and are worth a
separate look; they were out of scope here.

### Not done

- **C3 (descent depth cap)** — gated by design. Still needs an explicit decision: it changes
  behaviour, failing chains deeper than the cap where they previously worked.
- **D2 (acceptance against a running stack)** — needs the docker infra plus the four apps, and
  OpenObserve. The unit tests pin the logic; D2 is what proves the tree's actual shape. Until it runs,
  the ladder is verified in theory only.

---

## Resolved open questions

The analysis left three decisions to the plan. All three are settled here.

### Q1 — Is `Function.{name}` (analysis piece 1) worth it? **No. Cut.**

The ASP.NET server span already names the route
(`GET /api/v1/{domain}/workflows/{workflow}/instances/{instance}/functions/{function}`), so at level 1
a `Function.{name}` span would restate its parent — the exact argument that removed `transition/{key}`
in the 2026-08-25 refinement round. At level 2+ there is no server span, but that is what
`Subflow.Descend` (Q2) is for, and it can carry the function name as a tag for a fraction of the cost.

The two tags piece 1 was going to justify itself with are already elsewhere: cache hit/miss lives on
`Cache.Get`'s `cache.source`, and the 304 lives on the server span's `http.status_code`.

**Consequence:** the plan has three pieces, not four. Nothing is lost.

### Q2 — Which `ActivitySource`? **New: `BBT.Workflow.Instances.Read`.**

Not `BBT.Workflow.Pipeline`: that name means the write path, and folding read spans into it silently
breaks every "show me pipeline spans" query and every duration aggregation built on it. The
`BBT.Workflow.Instances.*` prefix is already established (`BBT.Workflow.Instances.Events`).

Registered in the same four hosts as `BBT.Workflow.Authorization`. Orchestration is the only host that
serves these reads today, but a source missing from a host goes dark **in that host only** — the
silent failure this convention exists to prevent — and the cost of listing it is one line.

### Q3 — Does depth propagation break the cross-domain contract? **No.**

The analysis assumed `GetFunctionWithInstanceInput` travels as a serialized body. It does not:
`RemoteInstanceQueryAppService` maps it to a URL (`InstanceUrlTemplates.State(...)` + query params).
So adding a property is a **local** change with no wire impact, and cross-domain propagation is an
additive header alongside the existing `X-Parent-Instance-Id`. Absent header ⇒ depth 0.

`no-breaking-change-policy` therefore does not bite. Phase C is much cheaper than the analysis feared.

---

## Global constraints

- **No pushes.** Local commits only, on `feature/caller-role-provider`.
- **Solution path:** `/Users/U0B006/Documents/repos/burgan-tech/vnext/vnext.sln`.
- **Dirty worktree.** The tree carries unrelated in-flight work (the Aether local-feed wiring,
  `Directory.Build.props` on `1.0.40-local`, `nuget.config` with `aether-local`). Stage only the exact
  files each task names. Never sweep.
- **Branch HEAD does not build on its own** — `WorkflowApiBaseServiceCollectionExtensions.cs` at
  `445c3959` carries `using BBT.Workflow.DefinitionContext;` for a namespace that does not exist; the
  working tree fixes it. Do not "clean up" by reverting that file.
- **Logging:** `WorkflowLogs` `[LoggerMessage]` extensions only, never raw `logger.Log*`.
- **Behavior must not change** in Phases A and B. No new failure modes, no new short-circuits, no
  ordering changes. These phases add spans and move tag writes; nothing else.
- **`SourceName` const rule (learned the hard way, 2026-08-31):** any new `ActivitySource` holder
  exposes `public const string SourceName`, and every `ShouldListenTo` predicate matches against the
  const. Reading the static `ActivitySource` field from inside a predicate re-enters the type
  initializer that `AddActivityListener` is still running → NRE → the type is poisoned for the whole
  process. Symptom: tests pass individually and fail together. See `AuthorizationActivityHelper`.
- **Test baseline (per-project, this branch, 2026-08-31):** Application 16 failing / 1762,
  Domain 27 / 2318, Infrastructure 16 / 316 (the Infrastructure ones are Testcontainers/Postgres and
  need Docker). Judge by **no new failing test names**, never by count alone.

---

## Design summary

```
GET .../instances/{id}/functions/state          ← ASP.NET server span (level 0, already exists)
├─ Auth.ResolveRoles                            ← already exists (2026-08-31)
├─ Cache.Get/state-fn:v7:…                      ← already exists; today unattributable
├─ Db.SELECT × N                                ← already exists; today unattributable
└─ Subflow.Descend/chain-busy-middle            ← NEW  depth=1, transport=local
   ├─ Cache.Get/state-fn:v7:…                   ← now nests under ITS level
   ├─ Db.SELECT × N
   └─ Subflow.Descend/chain-busy-leaf           ← NEW  depth=2, transport=local
      ├─ Cache.Get/…
      └─ Db.SELECT × N
```

Cross-domain descent keeps its existing shape — the HTTP client span plus the remote server span
already draw the boundary. `Subflow.Descend` still wraps it, tagged `transport=remote`, so the two
transports read the same way in the tree and a mixed chain is legible.

**Span:** `Subflow.Descend/{targetFlow}`, source `BBT.Workflow.Instances.Read`, kind `Internal`,
`span.category=business`.

| Tag | Value |
|---|---|
| `vnext.subflow.depth` | 1-based descent level |
| `vnext.descent.transport` | `local` \| `remote` |
| `vnext.descent.function` | `state` \| `data` \| `schema` \| `master` \| `view` \| `extensions` \| `authorize` |
| `vnext.domain`, `vnext.flow.key`, `vnext.instance.id` | the **target** (child) identity |
| `vnext.parent.instance.id` | the caller's instance |

---

## FAZ A — the level boundary span

> Fixes analysis findings 2.1 (invisible in-process descent) and 2.3 (unattributable cache/DB spans).
> This is the phase that buys the most; A2 alone makes the ladder readable.

### ✅ Task A1: `InstanceReadActivityHelper`

- [ ] New `src/BBT.Workflow.Application/Instances/InstanceReadActivityHelper.cs`.
- [ ] `public const string SourceName = "BBT.Workflow.Instances.Read";` then
      `public static readonly ActivitySource ActivitySource = new(SourceName);` — const first, see the
      global constraint on why.
- [ ] `StartDescend(string targetFlow, int depth, string transport, string function)` → names the span
      `Subflow.Descend/{targetFlow}`, stamps `span.category=business` and the five tags above.
- [ ] `SetTarget(Activity?, domain, flow, instanceId, parentInstanceId)` — separate from the start call
      because the target instance id is known at the call site while the flow key comes from the
      correlation.
- [ ] Tag name constants into `TelemetryConstants.TagNames`: `SubflowDepth`, `DescentTransport`,
      `DescentFunction`. `vnext.parent.instance.id` and `vnext.instance.id` already exist — reuse.
- [ ] Also add `TelemetryConstants.DescentTransports` with `Local` / `Remote`.

**Acceptance:** builds; no call sites yet.

### ✅ Task A2: wrap the five `InstanceQueryAppService` descents

- [ ] `GetSubFlowTransitionsAsync` (:515) — function `state`
- [ ] `GetSubFlowMasterAsync` (:2015) — `master`
- [ ] `GetSubFlowExtensionsAsync` (:2094) — `extensions`
- [ ] `GetSubFlowSchemaAsync` (:2184) — `schema`
- [ ] `GetSubFlowViewWithOverrideAsync` (:2343) — `view`

Each gets `using var activity = InstanceReadActivityHelper.StartDescend(...)` wrapping **only** the
gateway call and the result handling that belongs to it.

- [ ] Transport is decided the same way `RoutedInstanceQueryGateway` decides it:
      `runtimeInfoProvider.IsDomainMatch(subflow.SubFlowDomain)`. Do **not** duplicate the routing
      logic — inject `IRuntimeInfoProvider` (already available in this service) and read the same
      predicate, so a future routing change cannot make the tag lie.
- [ ] `GetSubFlowExtensionsAsync` is also called from `:854`, inside another descent. Verify the
      resulting nesting is correct rather than assuming it: the inner span must parent to the outer
      one, not to the server span.

**Acceptance:** a same-domain 2-level state read produces exactly 2 `Subflow.Descend` spans, depths
1 and 2, each containing its own level's `Cache.*`/`Db.*` children. Existing tests unchanged.

### ✅ Task A3: `AuthorizeAppService` descent

- [ ] Wrap `authorizeGateway.GetAuthorizeResultForInstanceAsync` (:119), function `authorize`.
- [ ] Note this path forwards to the subflow only after checking parent-owned transitions and
      overrides — the span goes around the forward, not around the whole method, or a locally-answered
      authorize would show a descent that never happened.

### ✅ Task A4: `InstanceRetryAppService` descent

- [ ] Wrap `GetFunctionWithStateAsync` (:103), function `state`, transport as A2.
- [ ] Lowest value of the four (retry is rare), but leaving one descent unspanned means "no
      `Subflow.Descend` in this trace" stops being a reliable signal.

### ✅ Task A5: register the source

- [ ] Add `"BBT.Workflow.Instances.Read"` to `Otel:Tracing:AdditionalSources` in the four hosts:
      orchestration, execution, Workers.Inbox, Workers.Outbox.
- [ ] Preserve each file's array style (orchestration is multi-line; the others are single-line) and
      re-validate the JSON parses.

---

## FAZ B — stop levels overwriting each other's tags

> Fixes analysis finding 2.2. Depends on A2 (needs a per-level span to write to).

### ✅ Task B1: move the ambient stamping onto the level's span

- [ ] `InstanceQueryAppService` :92 and :680 both do
      `Activity.Current?.SetTag(RootInstanceId, …)` + `SetBaggage(...)`. On an in-process descent
      `Activity.Current` is the **same** Activity at every level, so each level overwrites the last.
- [ ] Write the tag to the level's own `Subflow.Descend` span when one is in scope; keep writing to
      `Activity.Current` at level 0, where the server span is the correct target.
- [ ] **Baggage stays on `Activity.Current`.** Baggage is deliberately ambient and inherited — moving
      it would sever propagation to children and to any outbound call. Only the *tag* moves.
- [ ] Straight port of what `Transition.{key}` did for chained hops (2026-08-25 refinement 4); read
      that first rather than re-deriving it.

**Acceptance:** in a 3-level trace, the three `Subflow.Descend` spans carry three **different**
`vnext.instance.id` values, and the root instance id on the server span is the parent's, not the
leaf's.

---

## FAZ C — descent depth

> Fixes analysis finding 2.5's observability half. The robustness half (the cap) is C3 and is
> **gated** — it is a behavior change and needs its own approval.

### ✅ Task C1: carry depth

- [ ] Add `public int SubflowDepth { get; set; }` to `GetFunctionWithInstanceInput`. Local only — the
      remote path maps this DTO to a URL, it does not serialize it (see Q3).
- [ ] Same-domain: increment when building the child input in each descent helper.
- [ ] Cross-domain: add header `X-Subflow-Depth` next to the existing `X-Parent-Instance-Id` in
      `TelemetryConstants.HeaderNames`; `RemoteInstanceQueryAppService` sends it, the receiving
      controller reads it into the input. **Absent ⇒ 0**, so an older peer degrades to today's
      behavior instead of failing.
- [ ] Tag it on the `Subflow.Descend` span (already in A1's signature — this task makes the value
      real instead of a local counter that resets at every domain boundary).

**Acceptance:** a mixed local→remote→local chain reports depths 1, 2, 3 — not 1, 1, 2.

### ✅ Task C2: document

- [ ] Add the `Subflow.Descend` row to `docs/runtime/trace-span-tree.md`'s span reference table, and
      the ladder to its target-tree diagram.
- [ ] Note the local/remote asymmetry that motivated it, so the next reader does not re-discover it.

### ⏸ Task C3 (GATED — needs explicit approval): cap the descent

- [ ] **Do not start without asking.** This changes behavior: a chain deeper than the cap starts
      failing where it previously worked.
- [ ] Mirror `Workflow:Scripting:RelatedAccess:MaxResolutionsPerContext` (default 10):
      `Workflow:Instances:MaxSubflowDescentDepth`.
- [ ] On breach: a `Result` failure with a new `WorkflowErrors` code, plus the span marked Error —
      not an exception, and not a silent truncation of the chain.
- [ ] The value of doing this at all is that today a cyclic correlation graph recurses until the stack
      or the request timeout ends it, and C1's depth tag is what makes such a chain visible in the
      first place. Whether that risk is real enough to pay for a behavior change is the user's call.

---

## FAZ D — verification

Method is the one established on 2026-08-30: **OpenObserve** (stream `vnext`, org `default`),
explicit acceptance queries. Jaeger is gone — do not look for 16686.

> **Measurement trap, carried forward:** `start_time` / `end_time` are **nanoseconds**, `duration` is
> **microseconds**. Mixing them silently produces nonsense.

### ✅ Task D1: unit tests

- [ ] `Subflow.Descend` emitted once per descent, with the right depth and transport.
- [ ] Nesting: the `:854` extensions-inside-state case parents correctly.
- [ ] A locally-answered `authorize` (parent-owned transition) emits **no** descent span.
- [ ] Listener via the `SourceName` const, never the `ActivitySource` field (global constraint).

### ⏸ Task D2: acceptance against a running stack

| Check | Target |
|---|---|
| 3-level same-domain `chain-busy` state read | exactly 3 `Subflow.Descend` spans, `vnext.subflow.depth` = 1,2,3 |
| Every `Cache.Get/state-fn:*` in a descended read | has a `Subflow.Descend` ancestor |
| The three descent spans' `vnext.instance.id` | three distinct values (FAZ B) |
| 304 response on a parked chain | still shows the full ladder |
| Cross-domain descent | `Subflow.Descend` with `transport=remote` wrapping the HTTP client span |
| Duration containment | each `Subflow.Descend` fully contains its children (the 2026-08-30 check-8 shape) |
| No regression | business transition traces contain no `Subflow.Descend` (write path untouched) |

---

## Sequencing and risk

**Order:** A1 → A2 → A5 (registration must land with the source, same commit) → A3 → A4 → B1 → C1 → C2.
C3 only on approval. D1 alongside each task; D2 once at the end.

**Stop after A2+A5 and look at a real trace before continuing.** A2 is where the tree's actual shape
becomes visible, and B1 and C1 are both easier to get right — and easier to judge — once it is.

| Risk | Mitigation |
|---|---|
| Parenting regression (the recurring one in this area) | D2's containment check; the 2026-08-25 work flagged parenting as the top risk and it has been right twice |
| Span volume on a hot path — the state function is the most frequent request in the system | One span per descent, and only when a descent happens. A read with no subflow adds nothing. Measure in D2 before assuming it is fine. |
| Transport tag drifting from actual routing | A2 reads the same `IsDomainMatch` predicate the router uses, rather than re-deriving it |
| C1's header ignored by an older peer | Absent ⇒ 0; depth degrades, nothing fails |
