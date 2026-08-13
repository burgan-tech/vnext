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
SELECT Id, Key, EffectiveState, Status, FlowVersion,
       EXISTS(active SubFlow correlation)      AS HasActiveSubFlow,
       COUNT(correlations)                     AS CorrelationCount,
       COUNT(correlations WHERE IsCompleted)   AS CompletedCorrelationCount,
       MAX(correlations.CompletedAt)           AS LastCorrelationCompletedAt,
       MAX(correlations.SubFlowStateChangedAt) AS LastSubFlowStateChangedAt
```

- Identifier resolution mirrors `FindByIdentifierAsReadOnlyAsync`: id first, then the most
  recent row by key (`ORDER BY CreatedAt DESC`).
- `HasActiveSubFlow` translates to an `EXISTS` subquery served by the partial index
  `IX_InstancesCorrelations_ActiveBlockingSubFlow` (`ParentInstanceId WHERE IsCompleted = false
  AND SubFlowType = 'S'`) — a single B-tree probe, not a correlation load.
- `EffectiveState` (not `CurrentState`) is used because subflow state changes are propagated
  upward into the parent's `EffectiveState` column.
- The four **correlation aggregates** exist because the response body carries the full
  `correlations` list (active *and* completed). They run over the unfiltered correlation set and
  are served by `IX_InstancesCorrelations_ByParent` (`ParentInstanceId`, no filter) — the two
  partial indexes cannot serve them, since both exclude exactly the completed rows involved.
  Each mutation of the set moves at least one aggregate: a sub item starting moves
  `CorrelationCount`, terminating (or a revert) moves `CompletedCorrelationCount`, a
  revert-then-recomplete that restores both counts moves `LastCorrelationCompletedAt`, and a sub
  item advancing its own state moves `LastSubFlowStateChangedAt`.
- **Scheduled-job rows are deliberately not projected** (team decision, issue #864). The body's
  `scheduledTransitions` list is therefore *not* covered by cache validation — see the known-gap
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
         | lastCorrelationCompletedAt | lastSubFlowStateChangedAt)
```

- **Deterministic across pods**: any instance of the service computes the same ETag for the
  same fingerprint, so 304 works with an empty cache (after TTL expiry, Redis flush, or
  failover).
- **`responseShapeVersion` guards runtime-side body changes** (`StateFunctionCache.ResponseShapeVersion`,
  currently `v6`). The material is derived from instance facts and caller scope only — it says nothing
  about what the body *contains*. So when a runtime release changes the body for an unchanged instance
  (v2 started listing the workflow-level `updateData` and `exit` transitions; v3 added the workflow's
  `functions` discovery links; v4 replaced that inline list with a `hasFunctions` flag plus a link to
  the `catalog` function; v5 began narrowing `availableTransitions` by per-state `availableIn` role
  grants, which can *remove* an entry a caller previously saw; v6 added the `scheduledTransitions`
  list), every previously issued ETag must be
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
  `h(... | displayedState | displayedStatus)`. The parent row alone cannot see
  subflow-internal Busy/Active flips (`PropagateEffectiveStateToParent` updates only
  `EffectiveState`, never `Status`, and is asynchronous via the Inbox worker).
- **Correlation members are in the hash** because the body exposes the full `correlations` list:
  a sub item starting, terminating or advancing its state changes the body without touching the
  instance's own state or status, and a long-polling client would otherwise keep getting 304 and
  never observe it.
- **Scheduled-job changes are deliberately NOT in the hash** (team decision, issue #864). The
  body's `scheduledTransitions` list is built from the active scheduled-transition job rows, but
  the job set has no fingerprint member, so a job-set change with no state/status delta does not
  invalidate the ETag. **Known gaps, accepted**: a same-state re-arm (`updateData`/`$self` — the
  reserved path never commits an observable Busy flip), an inline A→B→A chain (one transaction,
  the intermediate state never commits), and a fired job rejected under a lock conflict (row
  deactivated, instance untouched) can each leave a parked client on a `304` with a stale
  `executeAtUtc` until the next fingerprint-visible change. The accepted mitigation is the
  transient Busy flip on the non-reserved paths plus natural state changes; conditional-GET usage
  is currently low and the team wants to observe the gap frequency before revisiting.
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

Instances with an open SubFlow correlation bypass both the 304 fast path and the cache — the
response is composed from a live call to the subflow's own state function
(`IInstanceQueryGateway`, in-process for same-domain, HTTP/Dapr for cross-domain). That call
benefits from the *subflow side's* own fingerprint cache: the subflow service answers it from
its Redis entry or fingerprint fast path like any other state request. Nothing is ever written
to the parent's cache while the correlation is open.

**The subflow call always returns a body — a 304 from the subflow is impossible by contract.**
The parent needs the subflow response to compose its own; it never sends `If-None-Match`
downstream. The in-process gateway maps only the typed `input.IfNoneMatch` (never set for this
call), and the remote path (`RemoteInstanceQueryAppService.GetFunctionWithStateAsync`)
explicitly strips `If-None-Match` from the forwarded caller headers — the caller's ETag belongs
to a different resource (the parent), and a false 304 would leave the composer with no body.

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
