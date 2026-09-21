# Discovery Endpoint Cache

How a domain name becomes a callable endpoint without paying a registry round trip on every
cross-domain hop — and what bounds the staleness that buys.

> **Scope:** the default (`http`) provider only. Under `ServiceDiscovery:Provider=dapr` nothing on
> this page applies; see [Dapr Invocation Transport](dapr-invocation-transport.md).

---

## Why this exists, and why it was deleted once

Under `Provider=http`, every cross-domain call resolves the target through the discovery registry.
`IDomainDiscoveryResolver.GetEndpointAsync` is called from ~35 sites — the trigger task executors and
the whole `Remote*` family, `RemoteInstanceCommandAppService` alone in twelve places — and each
resolution was one HTTP GET, measured at ~80 ms in production. A cross-domain subflow start spent
that before doing any work.

A cache for exactly this was shipped and then removed in **`79da3b6f`**, with the rationale:

> *The bulk domain cache's staleness risk (routing to a moved or dead endpoint for up to 5 minutes)
> is not worth its latency saving.*

**That was the right call about that implementation**, and the reason matters more than the verdict:
it revalidated over HTTP on every cache hit (`CheckDomainETagAsync`), against an endpoint its own
comment admitted had no conditional-request support. A "hit" cost a cache read **plus** the full
80 ms. It carried the staleness of a cache and saved nothing. It had three further defects worth
knowing, because each is something this design has to avoid rather than rediscover:

| Old defect | Consequence | Avoided by |
|---|---|---|
| HTTP call on every hit | no latency saving at all | a hit performs no I/O beyond the cache read |
| Whole blob rewritten with a fresh TTL on any miss | TTL extended indefinitely under traffic; "5 minutes" was optimistic | per-domain entries, each stamped with its own fetch time |
| Followed `links.next` | those links carry the *remote's* gateway base path, so page 2 404'd and was swallowed — one page cached forever, silently | no pagination at all: the registry's `domain-list` function answers for the whole registry in one call |
| Cached `DiscoveryEndpoint` under a domain-only key | `Kind` depends on the caller's `preferredKind`, so a Dapr-preferring caller and a URL caller overwrote each other | caches `DomainRegistration`, which is caller-independent |

---

## Shape

```
GetEndpointAsync
  └─ HttpDomainDiscoveryProvider          (no cache; builds the endpoint from the registration)
       └─ IDiscoveryRegistryClient
            └─ CachingDiscoveryRegistryClient   ← registered only when http + Cache:Enabled
                 ├─ L1  DiscoveryL1Cache        private MemoryCache, per pod
                 ├─ L2  IDistributedCacheService shared across pods
                 └─ DiscoveryRegistryClient     the live registry call
```

Fill comes from two directions: lazily, when a lookup misses, and in bulk from
`DiscoveryCacheRefreshHostedService`, which reads every registration once per refresh window.

**Why the decorator and not the provider.** The cacheable unit is `DomainRegistration` — the same
record the bulk endpoint yields, and the only one independent of the caller. `DiscoveryEndpoint`,
which the provider produces, carries a `Kind` chosen from the call site's `preferredKind`; caching it
per domain is the fourth defect above. Keeping the cache below the provider also keeps it below the
`Discovery.Resolve/{domain}` span, so a hit stays visible in traces.

---

## Staleness budget

```
W_worst = RefreshIntervalSeconds + fetch + L1TtlSeconds
        = 3600 + ~1 + 600  ≈  70 min   (defaults)
```

**Why an hour is the right size here, when five minutes was judged too long before.** The window is
sized to how often the data actually changes, not to how quickly it could: a domain's registered
address moves when that domain is deployed to a new address — a rare, planned event, not a
continuous drift. What made five minutes indefensible in the removed implementation was that it
bought nothing (every hit still made the HTTP call) and that there was **no way to shorten it on
demand**. Both are now false: a hit makes no network call, and `POST utilities/discovery/refresh`
re-reads the registry synchronously, so the single event that invalidates an entry early has an
operator-triggered answer that does not wait for the window.

A window this long also turns the cache into a **resilience** feature rather than only a latency one:
with entries valid for two hours, cross-domain traffic survives a discovery-registry outage instead
of failing with it.

**The trade this accepts:** if a domain moves and nobody forces a refresh, cross-domain calls to it
fail — the old address is gone — for up to the window. That is a real outage, not degraded latency,
and it is why the refresh endpoint is documented under *Operations* below rather than treated as a
nicety. Anyone changing a domain's `baseUrl` should call it as part of that change.

The refresher **overwrites** L2 rather than waiting for entries to expire, so L1/L2 TTL skew across
pods does not add to `W`. It only means two pods can disagree for up to `L1TtlSeconds` — which is why
that value stays well under the window, and why it is validated to stay below the refresh interval:
at parity a pod could serve a stale endpoint for twice as long as the cluster takes to correct it. It is also the residual staleness after a *forced* refresh: the endpoint corrects
the shared layer immediately, but each pod's own copy clears on its own schedule, so full propagation
is `L1TtlSeconds`, not instant. A pod that never wins the refresh lock contributes nothing: L2 is
shared, and the lock only decides who *writes*.

**The cache fails open.** If the refresher stops entirely — registry down, lock store down, pod
wedged — entries age past `L2TtlSeconds` and every lookup falls back to a live registry call, i.e.
the pre-cache behaviour, within 2 hours. `L2TtlSeconds` is twice `RefreshIntervalSeconds` by design:
survive one missed window, expire after roughly two. It never fails into a stale answer. A distributed-cache read
failure is likewise treated as a miss, so resolutions stay correct (just slow); during such an outage
only L1 can serve stale, for at most `L1TtlSeconds`.

`POST utilities/discovery/refresh` re-reads the registry **synchronously** and republishes every
entry, bypassing the window. It is the operational answer to the objection that killed the first
attempt: when a domain's `baseUrl` moves, nobody waits the window out. Only each pod's in-process
layer remains — so the fleet-wide effect of one click is **not instant, it is `L1TtlSeconds`** (ten
minutes by default; the pod that served the request is corrected immediately). That is the number to
weigh when changing `L1TtlSeconds`, and the reason it is not simply set to the refresh interval.

### The age is stamped on the entry, not delegated to the store

`CachedDomainRegistration` carries `FetchedAtUtc`, and every read checks it against the injected
`TimeProvider`. This is load-bearing rather than defensive: `IDistributedCacheService` is bound to
the **Dapr** provider, which turns an absolute expiration into the state store's `ttlInSeconds`
metadata — and a component that does not implement TTL ignores it **silently**. Entries would then
never expire, reproducing precisely the unbounded staleness that got the first cache deleted, with
nothing in code review or the logs to reveal it. With the stamp, the store's TTL degrades to garbage
collection and correctness stops depending on which component is configured.

---

## Refresh protocol

Warm-up is simply tick #1; there is no separate startup path, so startup and steady state cannot
drift apart. A pod that starts while the Dapr sidecar is still coming up fails tick one quietly and
succeeds on tick two.

| Key | Kind | Lifetime | Job |
|---|---|---|---|
| `discovery:bulk:v1:lock` | distributed lock | `WarmupLockLeaseSeconds` (30 s) | serializes the racers inside one window |
| `discovery:bulk:v1:refreshed-at` | cache entry | `RefreshIntervalSeconds` (1 h) | defines the window |
| `discovery:domain:v1:{domain}` | cache entry | `L2TtlSeconds` (2 h) | the payload |

Each tick, on every pod (`IDiscoveryCacheRefresher.RefreshAsync`):

1. **Marker present ⇒ return.** The common case never touches the lock. A forced refresh skips this
   step and step 3 — but *not* the lock, so two operators clicking at once still produce one read.
2. `TryAcquireLockAsync`, single attempt, no wait, no retry. `null` ⇒ return.
3. **Re-check the marker under the lock**, closing the gap between 1 and 2.
4. Bulk read, bounded by a linked CTS at `lease − 5 s`.
5. Publish and claim the window — **only** if the read succeeded and the result is non-empty.
6. `finally` ⇒ release the lease. Always.

**Both guards are needed, and they do different jobs.** The marker defines the window and is what
prevents a stampede: `DomainDiscoveryInitializationHostedService` gets away with never releasing its
lease because for a once-per-rollout job the lease *is* the guard, but a periodic refresh must be
able to re-acquire — and a released lock with no marker lets every replica acquire in turn and each
do a full bulk read. The lock only serializes racers within a window.

A replica that dies holding the lease stalls nothing permanently: the lease expires, no marker was
written, and the next tick retries. The bound is `lease + tick interval` ≈ 90 s — **not** the refresh
window, which is why the tick stays at a minute while the window is an hour. A tick whose window is
already served costs one cache read and stops.

**Step 4 is not optional.** The distributed lock does not auto-renew (pinned by
`DistributedLockRegistrationTests`). A fetch slower than its lease would keep writing after another
replica had legitimately taken the window over; bounding it turns that into a failed window instead.

**Step 5's empty check guards a subtle failure.** If the registry ever applies caller-role filtering
to its instance list, an under-authenticated refresher receives `200` with zero items — and without
the check the cache would record "this cluster has no domains" with complete confidence.

### Failure policy

A refresh failure is logged and swallowed. This is the deliberate difference from
`DomainDiscoveryInitializationHostedService`, which rethrows to abort startup: a domain that failed
to *register* is genuinely broken, whereas an unwarmed cache is not — every lookup falls back to the
live registry, which is the behaviour with the cache switched off. Failing the pod for it would turn
an optimisation into an availability risk. **Do not merge the two services.**

---

## Bulk read

`GET {BaseUrl}/{Domain}/functions/domain-list`, on its **own** named `HttpClient`
(`ServiceDiscoveryBulk`).

The separate client is not tidiness: the registration client's Polly circuit breaker also guards the
per-domain lookup — the path a cache miss falls back to. A bulk refresh failing on every tick would
trip that breaker and take the fallback down with it, so the refresh would break the very thing it
degrades into.

`domain-list` is a **Domain-scope function on the registry** (`vnext-domain-discovery`,
`discovery/Functions/domain-list.json`) that answers for the whole registry in one request:

```json
{ "items": [ { "domainName": "credit", "baseUrl": "http://…:5000", "appId": "vnext-credit-app", "healthUrl": "http://…:5000/health" } ] }
```

- **The registry owns the projection.** It filters to active registrations, orders them, takes the
  domain name from the instance key and drops anything without a `baseUrl`. None of that is
  re-derived here — this client only maps the four fields and normalises blanks to `null`.
- **There is no pagination.** The function is not a page of a list, it *is* the list, so a full
  response is never a reason to ask for another one.
- **It is cached on the registry side too**, for 24 h under the static key
  `discovery:domains:active`, with write-through eviction wired into the `domain` workflow's
  `start-domain` and `update` transitions. A refresh window therefore usually costs the registry a
  cache read, not an instance query — and a domain that registers or moves evicts that entry
  immediately, so the server-side TTL does not add to the staleness budget below.
- **All-or-nothing.** A failed read returns an error rather than a partial list; a partial list is
  indistinguishable from a registry that genuinely lost domains, and publishing one would evict good
  entries for nothing. An **empty** `items` is a valid 200 and reaches `DiscoveryCacheRefresher`
  as an empty success, which that service then treats as a failed window.

> ⚠️ **The registry must carry the `domain-list` function.** There is no fallback read: a discovery
> deployment whose package predates it answers 404, every window fails with
> `DomainListEndpointMissing` (EventId 50039), and the cache simply stays cold — lookups fall back to
> the live per-domain path, which is the behaviour with the cache switched off. Check this first when
> a cluster shows no cache hits at all after an upgrade.

> ⚠️ **The function serves a single page**, sized by its own ceiling (500 today), and its response
> carries no truncation signal. Reaching `DomainListExpectedMax` items is logged at **Warning**
> (`DomainListCeilingReached`, EventId 50038) and the list is published anyway: the domains it holds
> are still served from cache, and the ones beyond the ceiling merely pay a live lookup — where
> refusing to publish would make *every* domain pay it. Raising the ceiling is a change on the
> registry side.

---

## Read path rules

- **Key**: `discovery:domain:v1:{domain-lowercase}`, identical for L1 and L2. The `v1` segment is a
  shape generation — bump it whenever `CachedDomainRegistration` changes, so an old entry becomes a
  miss rather than a deserialization failure on a hot path.
- **Casing is normalised on both write and read.** The registry echoes whatever spelling a domain
  registered with; callers use whatever their component authored. If the two ever produced different
  keys, every lookup for that domain would miss and go live *forever* while the cache still showed
  entries and logged no errors — silently useless, which is worse than absent because nobody looks.
- **Failures are never cached**, not even as a negative entry. A domain registering a moment from now
  must work on its next call, not after a TTL.
- **Concurrent misses for one domain share a single lookup** (single-flight). A cold pod taking a
  post-rollout burst would otherwise pay full registry latency once per concurrent caller.
- **Every cache operation is swallowed on failure.** A cache that cannot be read is a miss; failing
  the caller for it would turn a degraded cache into a degraded runtime.
- Reads are gated on `ServiceDiscovery:Enabled` too, so turning discovery off does not leave a
  populated cache answering on its behalf.

---

## Configuration

`ServiceDiscovery:Cache`

| Key | Default | Notes |
|---|---|---|
| `Enabled` | **`false`** in code, `true` in the orchestration host's `appsettings.json` | the single rollback switch |
| `L1Enabled` | `true` | |
| `L1TtlSeconds` | `600` | must be `< RefreshIntervalSeconds`; **this is how long a forced refresh takes to reach the other pods** |
| `TickIntervalSeconds` | `60` | how fast a *failed* window is retried — **not** the refresh rate |
| `RefreshIntervalSeconds` | `3600` | 1 hour; enforced cluster-wide by the marker, not per pod |
| `L2TtlSeconds` | `7200` | the dead-man's switch; 2× the refresh interval |
| `WarmupLockLeaseSeconds` | `30` | must be `< RefreshIntervalSeconds` |
| `DomainListEndpointTemplate` | `/{0}/functions/domain-list` | `{0}` = the registry domain; configurable for a gateway path variation |
| `DomainListExpectedMax` | `500` | the registry function's own page size — reaching it warns that the list may be truncated |

### Why the code default is off

This reverses a shipped decision, and domain teams consuming the runtime as a package inherit code
defaults — default-on would silently reintroduce a staleness window for all of them on upgrade. It is
enabled per deployment through `appsettings.json`, which operators already own and can revert. The
same reasoning already governs `ServiceDiscovery:Enabled`, `Provider`'s fallback to `http`, and
`Dapr:RequireRegistryEntry`.

### The invariants are enforced at startup

`ServiceDiscoveryOptionsValidator` rejects `L1Ttl >= RefreshInterval`, `RefreshInterval >= L2Ttl`,
`WarmupLockLease >= RefreshInterval`, `L2Ttl < RefreshInterval + lease`, and an empty
`DomainListEndpointTemplate`. Every one of those degrades the cache **silently** when broken — it either stops
serving or widens its window, with no exception and no error log. Startup is the only place they are
visible, which is why they are a contract rather than a comment.

---

## Observability

On `Discovery.Resolve/{domain}`:

| Tag | Meaning |
|---|---|
| `vnext.discovery.resolution` | `cache` on a hit; `registry` / `convention` otherwise |
| `vnext.discovery.cache.age_seconds` | age of the entry that answered |

The age tag is the one that matters. Without it there is no way to answer *"was this routed by a
stale entry?"* during an incident — which is precisely the question the removal commit was written to
avoid ever having to answer, and the one that decides whether this feature survives its first
incident.

Logs (`WorkflowLogs`, EventIds 50001–50005 reused from the removed implementation so historical
queries keep working, plus 50036–50041): refresh started / refreshed / failed, domain-list fetch,
cache miss, skipped-fresh, skipped-not-owner, list-ceiling reached, domain-list endpoint missing,
cache operation failed.

`QueryingSingleDomain` (50006) stays at `Information`: it fires once per cache miss, so a steady
stream of it now means the cache is not working.

---

## Operations

**Force a refresh — do this whenever a domain's `baseUrl` changes.** With an hour-long window this is
not optional housekeeping: until it runs, every other domain keeps calling the address the moved
domain no longer answers on. Make it a step in whatever procedure changes a domain's address.

```bash
curl -X POST localhost:4201/api/v1/utilities/discovery/refresh
```

Reads the registry and republishes every entry synchronously, then answers with what it did:

| `outcome` | Meaning |
|---|---|
| `Refreshed` | done; pods' in-process copies clear within `L1TtlSeconds` |
| `SkippedNotOwner` | another replica is refreshing right now — its result applies cluster-wide |
| `Failed` | the registry could not be read; existing entries were left untouched |
| `disabled` | the cache is off; every resolution already queries the registry |

**Roll back completely:** set `ServiceDiscovery:Cache:Enabled=false` and restart. The plain registry
client is then registered and the refresher never starts, so not even an entry written before the
flip remains readable — the pre-cache behaviour is restored literally, not approximately.

---

## Related

- [Remote App Service Architecture](remote-app-service-architecture.md) — the `Remote*` clients and
  the `ServiceDiscovery:Provider` switch this sits under.
- [Dapr Invocation Transport](dapr-invocation-transport.md) — the provider this cache deliberately
  does not touch.
- [Trace Lanes](trace-lanes.md) / [Trace Span Tree](trace-span-tree.md) — where
  `Discovery.Resolve/{domain}` sits.
