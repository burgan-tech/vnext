# Built-in function subflow descent — trace/span analysis

**Status:** analysis complete. §4's sketch has been turned into
[`../plans/2026-08-31-subflow-descent-trace.md`](../plans/2026-08-31-subflow-descent-trace.md), which
resolves the three open questions left here — piece 1 (`Function.{name}`) is **cut** as redundant with
the route-named server span, the source is a new `BBT.Workflow.Instances.Read`, and depth propagation
turns out **not** to touch the cross-domain contract (the remote path maps the DTO to a URL rather
than serializing it). Read the plan for the current shape; this page stays as the evidence behind it.

**Context:** continues [`2026-08-25-trace-span-tree-design.md`](2026-08-25-trace-span-tree-design.md)
(pipeline span tree) and [`../plans/2026-08-30-trace-episode-separation.md`](../plans/2026-08-30-trace-episode-separation.md)
(transport vs business trace separation). Those two covered the **write** path — a transition and
everything it triggers. The **read** path was never touched, and that is where this looks.

---

## 1. The shape of the descent

Six built-in surfaces walk down into an active subflow. Each does the same three things: resolve the
parent instance, notice `instance.Subflow != null`, and forward the whole request to the child.

| Surface | Descent helper (`InstanceQueryAppService`) | Gateway call |
|---|---|---|
| `state` | `GetSubFlowTransitionsAsync` :515 | `GetFunctionWithStateAsync` |
| `master` | `GetSubFlowMasterAsync` :2015 | `GetFunctionWithMasterAsync` |
| `extensions` | `GetSubFlowExtensionsAsync` :2094 | `GetFunctionWithExtensionsAsync` |
| `schema` | `GetSubFlowSchemaAsync` :2184 | `GetFunctionWithSchemaAsync` |
| `view` | `GetSubFlowViewWithOverrideAsync` :2343 | `GetFunctionWithViewAsync` |
| `authorize` | `AuthorizeAppService` :119 | `IAuthorizeGateway.GetAuthorizeResultForInstanceAsync` |
| (retry) | `InstanceRetryAppService` :103 | `GetFunctionWithStateAsync` |

`GetSubFlowExtensionsAsync` is additionally called from `:854`, inside the state/data build — so a
descent can nest inside another descent within one level.

Every one of them lands on `RoutedInstanceQueryGateway`, which forks on domain:

```
InstanceQueryAppService (parent)
   └─ RoutedInstanceQueryGateway
      ├─ same domain  → LocalInstanceQueryGateway
      │                    └─ ExecuteWithWorkflowAsync   ← new DI scope, SAME async context
      │                         └─ IInstanceQueryAppService  ← full re-entry, recurses
      └─ other domain → RemoteInstanceQueryGateway → HTTP
```

---

## 2. Findings

### 2.1 The two transports are not equally visible — and the common one is the blind one

**Cross-domain is traced.** Aether enables `AddHttpClientInstrumentation`
(`AetherTelemetryServiceCollectionExtensions.cs:160`), so the remote hop produces a client span, and
the receiving host's ASP.NET server span continues the trace. Level boundaries are obvious.

**Same-domain is not traced at all.** `LocalInstanceQueryGateway` calls `ExecuteWithWorkflowAsync`,
which creates a DI scope — **not** an `Activity`. There is no `[Trace]` aspect on
`InstanceQueryAppService` or on any gateway (verified). So a same-domain descent re-enters the app
service with `Activity.Current` unchanged and contributes **zero** spans.

The consequence is the wrong way round: the *cheap* hop (in-process) is invisible and the *expensive*
one (HTTP) is visible. A three-level same-domain chain — `chain-busy` is exactly this — reads as one
flat region under the HTTP server span, containing three levels' worth of `Cache.*` and `Db.*` spans
in one undifferentiated list.

### 2.2 Levels overwrite each other's tags on the shared span

`InstanceQueryAppService` stamps the ambient span directly:

```csharp
Activity.Current?.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());   // :92, :680
Activity.Current?.SetBaggage(...);
```

On a same-domain descent `Activity.Current` is the *same* Activity at every level, because no level
opens one. So each level rewrites the previous level's tags on one span, and the surviving values are
whichever level wrote last.

This is not a new bug class in this codebase — it is the one the pipeline work already fixed for
chained hops:

> *"EnrichTelemetry o hop'ları bu span'e tag'ler, yoksa her hop transaction'ın tag'lerini eziyordu."*
> — `Transition.{key}` group span, refinement 4 of the 2026-08-25 work

Identical defect, different surface, still open.

### 2.3 Cache and DB spans are unattributable

`StateFunctionCache`, `DataFunctionCache` and `InstanceSchemaFunctionCache` each emit
`Cache.Get/{key}` (and `Cache.GenerationGet`) — at **every** level, since every level runs its own
cache lookup. With no level boundary span there is nothing to attach them to but the shared root.

Given a trace with a `state-fn:v7:…` miss and eight `Db.SELECT` spans, you cannot answer "which
instance in the chain paid for this" without decoding cache keys by hand. That is precisely the
question the read path gets asked when a state function is slow.

### 2.4 The 304 path descends too, and it is the highest-frequency request in the system

A conditional GET that ends in `304 Not Modified` still walks the chain — the child's ETag material
is needed to decide. Long-polling clients issue this continuously. So the least visible work in the
runtime is also the most frequently executed.

### 2.5 There is no depth bound

Nothing caps the descent (verified: no depth/`MaxDepth`/`MaxResolution` guard in
`InstanceQueryAppService`). Compare script-side related-instance access, which is capped by
`Workflow:Scripting:RelatedAccess:MaxResolutionsPerContext` (default 10). A malformed or cyclic
correlation graph recurses until the stack or the request timeout ends it, and today the trace would
show one long silent span rather than a visible ladder.

This is an observability finding *and* a robustness finding; they share a fix surface (a depth
counter), which is why it is listed here rather than filed separately.

---

## 3. What is already right, and should not be re-litigated

- **`Cache.*` spans exist and are correct.** The problem is attribution, not absence.
- **Cross-domain descent needs nothing.** HttpClient + server spans already draw the boundary; adding
  a vNext span around the remote call would duplicate what the transport reports.
- **Naming conventions are settled.** `Operation/{subject}`, `span.category=business`, no `[` prefix
  (it would be stripped by Aether's `BusinessSpanFilterProcessor` in Business mode), source listed in
  every consuming host's `AdditionalSources`. Reuse, do not reinvent.

---

## 4. Proposed work (sketch — for discussion)

Four pieces, roughly in dependency order. Piece 2 is the one that buys the most.

**(1) `Function.{name}` — the read transaction.**
One span at the built-in function entry (`state` / `data` / `schema` / `master` / `view` /
`authorize`), giving the read path a named root the way `TransitionJob.Execute/{key}` gives the write
path one. Tags: function name, domain/flow/instance, `vnext.cache.hit`, `http.status=304` when the
conditional GET short-circuits.
*Open question:* the ASP.NET server span already names the route. This may be redundant for hop 1 —
the same argument that removed `transition/{key}`. Possibly only worth it for the cache/304 tags.

**(2) `Subflow.Descend/{targetFlow}` — the level boundary.**
One span per descent, wrapping the gateway call. Tags: `vnext.subflow.depth`, target
domain/flow/instance, and `vnext.descent.transport = local | remote`. This alone fixes 2.1 and 2.3:
every level's cache and DB spans nest under their own level, and the ladder is readable at a glance.
Cheap — one wrap per descent helper, seven call sites.

**(3) Move the tag stamping off the ambient span.**
Stamp the level's own span (from piece 2) instead of `Activity.Current`, so levels stop overwriting
each other. Straight port of what `Transition.{key}` did for chained hops.

**(4) Depth counter + cap.**
Thread depth through `GetFunctionWithInstanceInput`, tag it, and bound it with a configured maximum
mirroring `RelatedAccess:MaxResolutionsPerContext`. Fixes 2.5 and gives 2.2's tags a stable key.
*Open question:* this changes a DTO on the cross-domain contract; needs a compatibility pass.

### Cost / risk

- Pieces 2 and 3 are contained: `InstanceQueryAppService` descent helpers + `AuthorizeAppService`.
  No behavior change, no contract change.
- Piece 4 touches `GetFunctionWithInstanceInput`, which crosses the domain boundary → the
  no-breaking-change policy applies (parallel support, `deprecations.json`).
- Piece 1 is the least certain and should probably be decided last, after 2 shows what the tree
  actually looks like.
- New `ActivitySource`? Not needed — `BBT.Workflow.Pipeline` already covers Application-layer read
  operations, or reuse the same source these helpers' callers run under. Deciding this is part of the
  plan, not the analysis.

### Verification

The 2026-08-30 work established the method: OpenObserve (`vnext` stream, org `default`), acceptance
queries rather than eyeballing. Analogues here:

- a 3-level `chain-busy` state read produces exactly 3 `Subflow.Descend` spans with depths 1..3;
- every `Cache.Get/state-fn:*` has a `Subflow.Descend` ancestor when the read descended;
- `vnext.instance.id` on each level's span differs (i.e. 2.2 is fixed);
- 304 responses still show the full ladder.

> **Measurement trap, carried forward:** in OpenObserve `start_time`/`end_time` are **nanoseconds**
> while `duration` is **microseconds**. Mixing them silently yields nonsense.

---

## 5. Relationship to the morph-idm span (same session)

`Auth.ResolveRoles` (added 2026-08-31, `AuthorizationActivityHelper`) is the read path's first
business span. It is deliberately narrow — it covers role resolution only — but it establishes two
things this work would reuse: the `BBT.Workflow.Authorization` source is registered in all four
hosts that resolve roles, and the "emit on hit and miss, put the outcome in a tag" rule is now
applied twice (compile cache, role memo). A `Subflow.Descend` span should follow the same rule for
its own short-circuits.
