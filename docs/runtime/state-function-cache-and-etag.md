# Instance Function Cache and Fingerprint ETag (state, data, master, schema)

## Purpose

The state function (`GET .../functions/state`, long-polling) is the hottest read path in the
runtime: clients poll it continuously with `If-None-Match` until the instance reaches a terminal
status. Rebuilding the full response on every poll (aggregate load, authorization gate, role
filtering, canonical-JSON hashing) is wasteful because the answer only changes when the
instance's state or status changes.

The runtime therefore derives the ETag from a **state fingerprint** instead of the response
body, and backs it with a distributed response cache. The fingerprint is loaded by a
single-row projection query (the *light DB query*), so the dominant poll cycle is answered
without touching the aggregate, the cache, or the response builder.

## Request flow

```mermaid
flowchart TB
    A["GET /functions/state<br/>(long-poll, If-None-Match)"] --> B["Light DB query<br/>(single-row fingerprint projection)"]
    B --> C{Active subflow?}
    C -- yes --> S["Live evaluation<br/>(cache bypass, live ETag)"]
    C -- no --> D["Compute ETag<br/>(fingerprint + caller hash)"]
    D --> E{If-None-Match matches?}
    E -- yes --> R304["304 Not Modified<br/>(no cache access, no build)"]
    E -- no --> F["Redis cache GET<br/>(entry.Etag == etag?)"]
    F -- match --> R200C["200 from cache<br/>(no aggregate load)"]
    F -- "miss / stale" --> G["Full build<br/>(aggregate + auth + role filtering)"]
    G --> H["Cache SET (TTL 60s)"]
    H --> R200["200 + ETag"]
```

Edge cases not shown: when the fingerprint query finds no instance, the full path runs and
produces the proper error (404). When `StateFunctionCache:Enabled` is `false`, the left column
is skipped entirely, but the ETag is still computed with the same formula inside the full
build — toggling the flag never changes ETag semantics.

## The three structures

### Light DB query (fingerprint)

`IInstanceRepository.GetStateFingerprintAsync(identifier)` projects one row — no includes, no
aggregate materialization:

```
SELECT Id, Key, EffectiveState, Status, EffectiveStatus, FlowVersion,
       EXISTS(active SubFlow correlation)      AS HasActiveSubFlow,
       COUNT(correlations)                     AS CorrelationCount,
       COUNT(correlations WHERE IsCompleted)   AS CompletedCorrelationCount,
       MAX(correlations.CompletedAt)           AS LastCorrelationCompletedAt,
       MAX(correlations.SubFlowStateChangedAt) AS LastSubFlowStateChangedAt,
       HasActiveIncident
```

- Identifier resolution mirrors `FindByIdentifierAsReadOnlyAsync`: id first, then the most
  recent row by key (`ORDER BY CreatedAt DESC`).
- `HasActiveSubFlow` translates to an `EXISTS` subquery served by the partial index
  `IX_InstancesCorrelations_ActiveBlockingSubFlow` (`ParentInstanceId WHERE IsCompleted = false
  AND SubFlowType = 'S'`) — a single B-tree probe, not a correlation load.
- `EffectiveState` (not `CurrentState`) is used because subflow state changes are propagated
  upward into the parent's `EffectiveState` column.
- `EffectiveStatus` is its status counterpart: the deepest active SubFlow's status, else the row's
  own. Maintained by the busy walk on the way down and by the sub-item's rest-point notification on
  the way up. An accept that reserves a chain flips only the LEAF's row, so without this member an
  ancestor's fingerprint would be bit-identical across exactly the transition a cached body must not
  survive. **It is also served now**, as `metadata.effectiveStatus` — but responses read it
  through `Instance.GetEffectiveStatus`, which clamps a terminal propagated status on a still-running
  level back to that level's own status (see below). The column itself, and therefore this
  fingerprint, keeps the raw propagated value.
- The four **correlation aggregates** exist because the response body carries the full
  `correlations` list (active *and* completed). They run over the unfiltered correlation set and
  are served by `IX_InstancesCorrelations_ByParent` (`ParentInstanceId`, no filter) — the two
  partial indexes cannot serve them, since both exclude exactly the completed rows involved.
  Each mutation of the set moves at least one aggregate: a sub item starting moves
  `CorrelationCount`, terminating (or a revert) moves `CompletedCorrelationCount`, a
  revert-then-recomplete that restores both counts moves `LastCorrelationCompletedAt`, and a sub
  item advancing its own state moves `LastSubFlowStateChangedAt`.
- **`HasActiveIncident`** is the denormalized instance column maintained by the aggregate
  (`AddIncident` / `ResolveOpenIncidents`) and, since `v9`, the only incident data the body needs, so projecting it costs nothing — no join to the
  `InstanceIncidents` table. It is in the fingerprint because the body carries an `incident` block
  and an error-boundary transition can open (Abort + transition) or close (`FinalizeTransitionStep`)
  an incident without moving state or status.
- **Scheduled-job rows are deliberately not projected** (team decision, issue #864). The body's
  `kind: "scheduled"` transition entries are therefore *not* covered by cache validation — see the known-gap
  note under the ETag section below.

> **Invariant — the two build paths must agree.** The fast path fingerprints via this projection;
> the full-build path uses `InstanceStateFingerprint.FromInstance(instance, allCorrelations)`.
> The aggregate's own `ChildCorrelations` is loaded with an active-only filtered include, so it
> must never feed the aggregates — hence `allCorrelations` is a required argument rather than
> read off the instance. If the two disagree on one member, the full-path ETag never matches the
> one the fast path validates and every poll rebuilds the response. Guarded by
> `InstanceStateFingerprintQueryTests.ProjectionAndFromInstance_ProduceIdenticalFingerprints`.

### ETag

Computed by `IStateFunctionCache.ComputeEtag` — a deterministic SHA-256 hash (32 hex chars):

```
etag = h(responseShapeVersion | instanceId | effectiveState | status | flowVersion | callerHash
         | correlationCount | completedCorrelationCount
         | lastCorrelationCompletedAt | lastSubFlowStateChangedAt | hasActiveIncident)
```

- **Deterministic across pods**: any instance of the service computes the same ETag for the
  same fingerprint, so 304 works with an empty cache (after TTL expiry, Redis flush, or
  failover).
- **`responseShapeVersion` guards runtime-side body changes** (`StateFunctionCache.ResponseShapeVersion`,
  currently `v9`). The material is derived from instance facts and caller scope only — it says nothing
  about what the body *contains*. So when a runtime release changes the body for an unchanged instance
  (v2 started listing the workflow-level `updateData` and `exit` transitions; v3 added the workflow's
  `functions` discovery links; v4 replaced that inline list with a `hasFunctions` flag plus a link to
  the `catalog` function; v5 began narrowing `availableTransitions` by per-state `availableIn` role
  grants, which can *remove* an entry a caller previously saw; v6 started listing scheduled
  transitions inside `transitions` as `kind: "scheduled"` entries with `executeAtUtc`; v7 gave the
  scheduled entries the uniform `href`/`view`/`schema` link objects with their capability flags
  hardcoded false — a temporary concession so domain clients that assume every `transitions[]` item
  carries the three links do not break; v8 added the always-present `incident` block with an embedded
  `active` summary; v9 replaced that block's content with links —
  `{ hasActiveIncident, active: { href }, history: { href } }`; v10 added the top-level `timeout`
  block, `{ key, target, executeAtUtc }`, omitted entirely when no workflow deadline is armed or once
  the polled instance's own status is terminal; v11 changed the **presence condition** of the
  `interaction` block from "the state declares one" to "an acknowledge is actually pending"
  (`Instance.IsAwaitingLongPollAck`) — a shape change with no new field, and exactly the kind a
  parked client would otherwise never see; v12 started carrying `annotations` on the
  `kind: "scheduled"` entries and on the `timeout` block), every previously issued ETag must be
  invalidated: otherwise a client
  long-polling an instance parked in a human state would keep receiving 304 and never observe the new
  shape. The same constant is a segment of the cache key, so bumping it also discards bodies written by
  the previous build. **Bump it in the same commit as any change to what the state body carries** —
  `StateFunctionCacheTests.BuildKey_ContainsResponseShapeVersionDomainWorkflowAndInstance` asserts the
  literal so the bump cannot be forgotten silently.
- **`functions.hasFunctions` is deliberately *not* in the ETag material.** It is a property of the flow
  version, which `InstanceStateFingerprint.FlowVersion` already covers, so it cannot change while an
  instance is parked. Adding it to the material would buy nothing and cost a hash input. What needed
  invalidating was each shape change, once — that is exactly what the `v3`, `v4` and `v5` bumps did.
- **`callerHash` is inside the hash**: the response is authorization- and localization-scoped,
  so a caller switching role, actor, or culture must never receive a false 304.
- **Subflow variant**: when an active subflow exists, the response content comes from a live
  subflow call, so the displayed state and status are folded into the hash:
  `h(... | displayedState | displayedStatus)`. The parent row's own `Status` cannot see
  subflow-internal Busy/Active flips (`PropagateEffectiveStateToParent` never touches `Status`) —
  that is what `EffectiveStatus` covers.
- **Correlation members are in the hash** because the body exposes the full `correlations` list:
  a sub item starting, terminating or advancing its state changes the body without touching the
  instance's own state or status, and a long-polling client would otherwise keep getting 304 and
  never observe it.
- **Scheduled-job changes are deliberately NOT in the hash** (team decision, issue #864). The
  body's `kind: "scheduled"` transition entries are built from the active scheduled-transition job rows, but
  the job set has no fingerprint member, so a job-set change with no state/status delta does not
  invalidate the ETag. **Known gaps, accepted**: a same-state re-arm (`updateData`/`$self` — the
  reserved path never commits an observable Busy flip), an inline A→B→A chain (one transaction,
  the intermediate state never commits), and a fired job rejected under a lock conflict (row
  deactivated, instance untouched) can each leave a parked client on a `304` with a stale
  `executeAtUtc` until the next fingerprint-visible change. The accepted mitigation is the
  transient Busy flip on the non-reserved paths plus natural state changes; conditional-GET usage
  is currently low and the team wants to observe the gap frequency before revisiting.
- **The `timeout` block needs no fingerprint member, and adds no gap.** Unlike the scheduled
  entries, its `executeAtUtc` is resolved once when the scheduler is armed and can never move
  afterwards — there is no re-arm path — and whether the block appears at all is governed by the
  instance's own `Status`, which is already hashed. So the two ways the block can change are both
  covered: a status move invalidates, and nothing else can change it. The `v10` bump was needed for
  the shape, once, not for the value.
- **The `interaction` block needs no fingerprint member either.** The token is armed inside the
  pipeline's own unit of work, and the pause it accompanies is a Busy rest — so the status the
  fingerprint already hashes moves with it, in the same commit. The `v11` bump was for the presence
  rule, once; there is no value here that can drift behind a 304 without a status move.
- **`annotations` need no fingerprint member.** On every `transitions[]` entry and on the `timeout`
  block they come from the definition (or, for the timeout, from the override stamped at start),
  so like `hasFunctions` they are a property of the flow version, which `FlowVersion` already
  hashes. The `v12` bump was for the shape — scheduled entries and the timeout block started
  carrying them — not for a value that could drift.
- **`hasActiveIncident` is in the hash** because the body's `incident` block flips with it and the
  flag can move without a state/status change (Boundary Abort with a transition raises one,
  `FinalizeTransitionStep` resolves it). The flag is the block's *only* varying member: since `v9`
  the block carries links, not the incident, so `active` is either present or absent and both states
  are decided by the flag that is already hashed.
- **The old resolve-A-then-raise-B gap is gone, not merely rarer.** While the block embedded a
  summary, resolving incident A and raising incident B inside one parked state left the flag `true`
  on both sides, so a client validating with `If-None-Match` kept its `304` and went on showing A.
  Content no longer rides in the body, so there is nothing left to go stale: the client re-fetches
  the incident through `active.href` and gets B. Do not reintroduce an embedded summary here without
  bringing back this hole.
- **A transient `hasActiveIncident = true` window is expected on a boundary transition.** The task
  step commits the incident before it saves — that is what stops the fault path recording a
  duplicate — so a `rollback`/`notify` outcome opens the row, routes to its transition, and only
  then closes it at `FinalizeTransitionStep`. A client polling mid-flight can legitimately observe
  the flag set on a `Busy` instance; the final committed state is unchanged.
- The ETag intentionally does **not** track instance-data-only changes: the state function
  signals state/status transitions, not data versions. `X-Entity-ETag` served from cache may
  lag data-only updates until the next state/status change (accepted by design — the data
  function is the authority for data freshness).

### Distributed cache

`IStateFunctionCache` over Aether `IDistributedCacheService` (Redis):

```
key   = state-fn:{responseShapeVersion}:{domain}:{workflow}:{instance}:{callerHash}
value = { Etag, EntityEtag, Output }          # full role-scoped response body
TTL   = StateFunctionCache:TtlSeconds         # default 60s = client long-poll timeout
callerHash = h(role | roles | actor identity | culture | extensions | version)
```

- The cache serves only callers **without** a current ETag (first poll, evicted client state).
  Validation on a hit is a single ETag equality check — state, status, version, and caller
  scope are all inside the hash, so a matching entry is guaranteed fresh.
- Actor identity participates because `$InstanceStarter`/`$PreviousUser` pseudo-roles are
  matched against `ICurrentUser`; culture participates because state alias labels are
  localized.
- Cache failures never fail a request: read errors degrade to a miss, write errors are
  swallowed (`StateFunctionCacheError`, EventId 20404).
- The authorization gate (`IsInstanceQueryAllowedAsync`) is skipped on a validated hit by
  design: the key pins the caller, the fingerprint pins the instance facts the gate depends on.

## Subflow behavior

Instances with an open SubFlow correlation bypass the plain 304 fast path — the response is composed
from a live call to the subflow's own state function (`IInstanceQueryGateway`, in-process for
same-domain, HTTP/Dapr for cross-domain). That call benefits from the *subflow side's* own
fingerprint cache: the subflow service answers it from its Redis entry or fingerprint fast path like
any other state request.

The composed body is then written to the parent's cache as an **active-subflow snapshot**, under
`ActiveSubflowTtlMilliseconds` (default 500 ms) rather than the normal TTL, and carrying the parent's
own fingerprint ETag as `ParentEtag`. A later poll serves it only while BOTH hold: the entry has not
expired, and the parent's freshly computed fingerprint ETag still equals that `ParentEtag`. So on
this path a stale parent projection can hold a body for at most one snapshot TTL — unlike the
no-subflow path, where a matching ETag answers 304 with no TTL at all, and where `EffectiveStatus`
therefore cannot disagree with `Status` (the aggregate and the status CAS keep them equal whenever no
SubFlow owns the row). Concurrent misses are coalesced by a per-key build lease.

**The subflow call always returns a body — a 304 from the subflow is impossible by contract.**
The parent needs the subflow response to compose its own; it never sends `If-None-Match`
downstream. The in-process gateway maps only the typed `input.IfNoneMatch` (never set for this
call), and the remote path (`RemoteInstanceQueryAppService.GetFunctionWithStateAsync`)
explicitly strips `If-None-Match` from the forwarded caller headers — the caller's ETag belongs
to a different resource (the parent), and a false 304 would leave the composer with no body.

### `effectiveStatus` as a served value

`metadata.effectiveStatus` on the instance GET, the list view and `GetInstanceTask` comes from
`Instance.GetEffectiveStatus`, **not** from the raw column:

```
effectiveStatus = Status.IsTerminal || EffectiveStatus.IsTerminal ? Status : EffectiveStatus
```

**The propagated projection is served only while neither side is terminal.** A terminal status on
either side means the projection can no longer be trusted, and in both directions the state function
already answers with the instance's own status. `InstanceStatus.IsTerminal` is the single definition
both read.

- **Terminal projection, running level** — the SubFlow completion window. The child reached a
  terminal status and published it upward at its rest point, so the column holds `C`/`F` while the
  parent's correlation is still open and the parent is resuming. `BuildInstanceStateOutputAsync`'s
  `subFlowIsTerminal` guard drops the subflow view and answers `Busy`; the clamp matches it.
- **Terminal own status, non-terminal projection** — a cancelled or faulted level whose child was
  still Active. The write side cannot fix this one: the cascade completes the level while the child's
  correlation is still open (so `Instance.ResyncEffectiveStatus` no-ops), and cleanup closes the
  correlation afterwards with nothing left to restamp the column. The state function's descent finds
  no active correlation and reports the own status. Without this arm a cancelled parent served
  `effectiveStatus: "A"` against the state function's `"C"` — measured on 15 pre-existing rows in a
  local core database. The stale `EffectiveState` those same rows carry is the identical gap one
  column over, and is unchanged by this work.

It is deliberately **not** written as "active subflow ? projection : own status": the list query does
not include child correlations (`EfCoreInstanceRepository.IncludeListData` loads `DataList` only), so
that predicate is false for every list item and every parent inside a subflow would report its own
`Busy`. A columns-only rule answers identically on all three surfaces.

Two consequences worth knowing:

- **Filters and sorts see the raw column, not the clamp** — they run in SQL. A
  `filter={"effectiveStatus":{"eq":"Completed"}}` can therefore return a parent that is in the
  completion window and whose served `effectiveStatus` reads `Busy`.
- **`WorkflowLogs.EffectiveStatusDrift` (EventId 20445) is now a regression sentinel**, not a
  measurement. It fires on the full-build path whenever the stored projection disagrees with the live
  descent for an instance with an active SubFlow. Before it was served, drift only shortened a cached body's life; now it means a served field is wrong.

## Data function

The data function (`GET .../instances/{instance}/data` and `functions/data`) uses the same
mechanism with a data-centric material — the design principle is that the data function
signals **data change points**, not state or extension flux:

```
etag = h(instanceId | latestDataEtag | flowVersion | callerHash)
key  = data-fn:{domain}:{workflow}:{instance}:{callerHash}
callerHash = h(roles | actor identity | culture | version)   # extensions deliberately EXCLUDED
```

- **Change signal is `InstanceData.ETag`** of the IsLatest row — a fresh ULID on every
  latest-line data write. It is read index-only via `UX_InstancesData_Instance_IsLatest`
  (ETag is in the INCLUDE list) by `IInstanceRepository.GetDataFingerprintAsync`.
- **`flowVersion` is in the material** because a flow migration can change `x-roles` field
  filtering and extension definitions without a data write.
- **Extensions are outside the key and the ETag.** The ETag signals the data change point
  only; requests differing only in the requested extension list share one key and one ETag.
- **Latest-data requests only.** Pinned-version requests (`?version=X`) bypass the fast path
  and the cache: a write into an *older* version line (`AddDataWithVersion`) creates a new row
  without touching the IsLatest row's ETag, so the latest-based material cannot see it. On the
  full path, pinned requests hash the **resolved** row's ETag instead — correct, because a
  write into that line produces a new row with a new ULID.
- **No subflow bypass**: the data body is always the parent's own `FindData` result; an active
  subflow only contributes extension output — which is never cached (below).
- A 304 answered from the fingerprint skips the aggregate load, the authorization gate, field
  filtering **and the extension run** — including always-on Global extensions, which are the
  most expensive part of a data read.

**The cache stores pure instance data — extension output is never cached:**

- An entry holds only the caller-scoped, field-filtered `Data` payload.
- A validated entry does **not** short-circuit the request: it feeds the **data portion** of
  the build (skipping the x-roles field filtering step — which may evaluate dynamic role
  scripts) while the extension pipeline **always runs fresh** against the raw instance data.
  This holds for every 200: whether or not the caller requested extensions, data comes from
  the cache when valid and extension output is never stale.
- Every latest-data 200 with a resolved data row warms the cache (data-only entry), including
  responses that carried extension output.
- Responses with no resolved data row are never cached.
- The heavy wins remain on the 304 fast path (no aggregate, no auth, no filtering, no
  extension run — including always-on Global extensions); the body cache additionally removes
  the field-filtering cost from every 200.

Accepted staleness (by design — "the critical thing is the data change point"): the ETag does
not track extension output, so an ETag-holding client receives 304 until the next data write
or flow migration even if extension output changed in the meantime; clients that need fresh
extension output call without `If-None-Match` and always get a freshly computed response.
Similarly, state-dependent `queryRoles` outcomes are only re-evaluated when data or flow
version changes (transitions usually write data, which re-triggers everything).

## Master and schema functions

Both return a resolved schema document (`GetSchemaOutput`) — the flow-level master schema for
`master`, the transition's schema for `schema` — and share one cache service
(`IInstanceSchemaFunctionCache`) and, by user decision, the data-centric change signal:

```
master etag = h(instanceId | latestDataEtag | flowVersion | callerHash)
schema etag = h(instanceId | latestDataEtag | effectiveState | flowVersion | callerHash | transitionKey)
master key  = master-fn:{domain}:{workflow}:{instance}:{callerHash}
schema key  = schema-fn:{domain}:{workflow}:{instance}:{callerHash}:{transitionKey}
callerHash  = h(roles | actor identity | culture | version)   # no extensions dimension
```

- `effectiveState` is only in the **schema** material: transition resolution
  (`ResolveTransition(transitionKey, currentState)`) is state-dependent, and
  `EffectiveState == CurrentState` whenever no active subflow exists.
- **Active subflow → full bypass** (both functions forward to the subflow's own function via
  the gateway). The subflow's body-embedded `ETag` is nulled on the forwarded response — it
  belongs to a different resource. Subflow calls never carry `If-None-Match` (the remote
  master/schema calls strip it from forwarded headers, like the state path).
- Only **successful** outcomes are cached (missing transition key, unresolvable transition,
  or missing schema reference are never written).
- A validated hit short-circuits fully (no aggregate load); the queryRoles gate is skipped on
  hits with the same justification as state/data — and note the master function's gate,
  previously disabled by a commented-out block, is now enabled (consistent with schema).
- Accepted staleness: republishing a schema component under the same version does not move the
  ETag (same class as a flow redeploy); data writes over-invalidate master/schema (harmless
  rebuilds, never staleness).
- Observability: shared EventIds 20420-20425 with a `{Function}` parameter
  (`InstanceSchemaFunctionCache*`), component types `master-fn` / `schema-fn`.

## Configuration

```json
"StateFunctionCache": {
  "Enabled": true,
  "TtlSeconds": 60
},
"InstanceFunctionCache": {
  "Enabled": true,
  "DefaultTtlSeconds": 60
}
```

Orchestration host `appsettings.json`. `Enabled=false` is the kill switch (full evaluation on
every request); TTL bounds only the residual staleness of parts not covered by the fingerprint
— fingerprint-covered changes are detected on every request via the projection query.

**Workflow-author TTL** (data/view/schema family — not the state function): the flow
definition may declare

```json
"functionCache": { "ttlSeconds": 120 }
```

(`Definitions.FunctionCacheDefinition`, bound to `Workflow.FunctionCache`). When present and
positive it overrides `InstanceFunctionCache:DefaultTtlSeconds` for that workflow's built-in
function cache entries; the single value covers all built-in functions of the workflow.

## Observability

| EventId | Name | When |
| --- | --- | --- |
| 20400 | `StateFunctionCacheHit` | Cached response served (ETag equality validated) |
| 20401 | `StateFunctionCacheMiss` | No entry for the caller scope |
| 20402 | `StateFunctionCacheInvalidated` | Entry ETag no longer matches the current fingerprint ETag |
| 20403 | `StateFunctionCacheBypassedForSubFlow` | Active subflow — live evaluation |
| 20404 | `StateFunctionCacheError` | Cache operation failed; degraded to miss |
| 20405 | `StateFunctionEtagNotModified` | 304 answered from the fingerprint alone |
| 20410-20414 | `DataFunctionCache*` / `DataFunctionEtagNotModified` | Data-function counterparts of the above |
| 20420-20425 | `InstanceSchemaFunctionCache*` | Master/schema counterparts (shared quintet + subflow bypass, `{Function}` = master/schema) |

Cache operations are traced under the `BBT.Workflow.Cache` activity source
(component types `state-fn`, `data-fn`, `master-fn`, `schema-fn`).

## Key implementation files

| Concern | File |
| --- | --- |
| Flow orchestration | `src/BBT.Workflow.Application/Instances/InstanceQueryAppService.cs` (`GetInstanceStateAsync`, `TryServeStateFromFingerprintAsync`) |
| ETag + cache | `src/BBT.Workflow.Application/Instances/Caching/StateFunctionCache.cs` |
| Fingerprint record | `src/BBT.Workflow.Domain/Instances/InstanceStateFingerprint.cs` |
| Projection query | `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs` (`GetStateFingerprintAsync`) |
| Options | `src/BBT.Workflow.Application/Instances/Caching/StateFunctionCacheOptions.cs` |
