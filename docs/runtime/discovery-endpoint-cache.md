# Discovery Endpoint Cache

How a domain name becomes a callable endpoint without paying a registry round trip on every
cross-domain hop, and what invalidates that answer — a deployment finishing, an operator, or a
failure to reach the cached address. Not a clock.

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

Fill comes from three directions: lazily, when a lookup misses; in bulk at startup, from
`DiscoveryCacheRefreshHostedService`, which stops as soon as the cluster's cache is filled; and in
bulk again whenever a deployment or an operator forces a refresh.

**Why the decorator and not the provider.** The cacheable unit is `DomainRegistration` — the same
record the bulk endpoint yields, and the only one independent of the caller. `DiscoveryEndpoint`,
which the provider produces, carries a `Kind` chosen from the call site's `preferredKind`; caching it
per domain is the fourth defect above. Keeping the cache below the provider also keeps it below the
`Discovery.Resolve/{domain}` span, so a hit stays visible in traces.

---

## Invalidation is an event, not a clock

```
L2TtlSeconds            = 0     (no expiry)
RefreshIntervalSeconds  = 0     (no periodic refresh)
```

An entry is filled once and lives until something says otherwise. Three things say otherwise:

| Trigger | Who calls it | Effect |
|---|---|---|
| `POST definitions/publish/completed` | the domain's CD pipeline, once per deployment | forced full re-read of the registry |
| `POST utilities/discovery/refresh` | an operator | the same, by hand |
| a transport failure against a cached endpoint | the runtime itself | that one domain's entry is dropped |

Measured on a two-domain local run (core + the discovery registry, `Provider=http`), on
`Discovery.Resolve/partner`:

| | `vnext.discovery.resolution` | duration |
|---|---|---|
| cached hit | `cache` (`age_seconds` 49) | **1.1 ms** |
| live re-resolution after an eviction | `registry` | **39.9 ms** |

**Why the clock went away.** The cached datum is *where a domain answers*, and that changes when the
domain is deployed to a new address — a rare, planned event. A timer sized to that either fires when
nothing changed (the common case, 24 registry reads a day to learn nothing) or fires too late. What
it actually cost was the window itself: `3600 + fetch + 600` ≈ **70 minutes** during which calls to a
moved domain went to an address it no longer answered on, with `utilities/discovery/refresh` as the
only way to shorten it. Replacing the timer with the deployment that causes the change removes the
window instead of shrinking it.

**Why removing the dead-man TTL is not the old mistake.** The first cache was deleted
(`79da3b6f`) for unbounded-ish staleness that bought nothing; a TTL was the answer then because
nothing else could notice a bad entry. Something can now: a stale entry only does damage while
something tries to *use* it, and that attempt is observable. See
[Failure-driven eviction](#failure-driven-eviction). A TTL, by contrast, can only convert "wrong
address" into "wrong address for a while" — and it charges every correctly cached domain a live
lookup per expiry to do it.

**The residual staleness is `L1TtlSeconds` (60 s).** Every invalidation writes the shared layer;
each pod keeps its own in-process copy until it expires. That is the one number to look at: a
publish-completed call, a forced refresh and an eviction all reach the rest of the fleet within it.
It is 60 s rather than the 600 it carried while it had to stay under an hour-long window — with no
window, a longer L1 buys one saved cache read per domain per expiry and costs propagation delay.

**What this design still does not cover.** A **peer** domain moving while *this* domain has no
deployment of its own, and whose traffic goes through a path that cannot observe a transport failure
— the trigger-task executors resolve an endpoint and hand the `baseUrl` to the **Execution** host,
which does the call, so a failure there is not seen by the cache that produced it. Recovery on that
path is `POST utilities/discovery/refresh`. This is accepted, deliberately, and it is the reason the
`cache.age_seconds` tag matters more here than it did with a TTL.

**Resilience, as a side effect.** Entries that never expire mean cross-domain traffic survives a
discovery-registry outage of any length instead of degrading with it.

### Failure-driven eviction

`IDiscoveryEndpointFeedback.ReportUnreachableAsync` drops a domain's entry (this pod's L1 and the
shared L2) and lets the next resolution go live. It is called from **one place** —
`RemoteTransportRouter.SendCoreAsync`, which every `Remote*` client funnels through — rather than
from the ~35 sites that resolve an endpoint. `DiscoveryEndpoint` carries its `Domain` for exactly
this reason: a socket error names a host, and a host cannot be mapped back to a cache key.

| Reported | Not reported |
|---|---|
| `HttpRequestError.ConnectionError`, `NameResolutionError` | any HTTP status — an error *response* means a domain at the right address |
| inner `SocketException`: `ConnectionRefused`, `HostNotFound`, `HostUnreachable`, `NetworkUnreachable`, `TimedOut` | a TLS failure — usually a certificate at the right address |
| | a broken circuit — a verdict about past failures, each already reported |
| | a cancelled request, and a response-read timeout — something answered |

- **Evicting is not a conclusion that the entry was wrong.** A correctly cached endpoint for a domain
  that is merely *down* is evicted too, and the next resolution re-caches the same address. That is
  intended: telling "moved" from "down" apart from a socket error is not decidable here, and the cost
  is one registry read per cooldown window.
- **`UnreachableEvictionCooldownSeconds` (30 s, per domain) is the load guard**, not a tuning knob.
  Without it a peer's outage becomes ours: every failed call evicts, and every eviction sends the next
  caller to the registry. Set it to `0` to switch failure-driven eviction off entirely.
- The cooldown claim is a check-and-set loop, because a moved domain fails every in-flight call at
  once and a plain read-then-write would let all of them evict.

### Running it on a timer instead

Set `RefreshIntervalSeconds` to a positive value and the periodic behaviour returns — marker window,
staleness budget and all. It is a rollback lever, for a deployment whose CD pipeline cannot be relied
on to call publish-completed. Set `L2TtlSeconds` alongside it: the validator **rejects** an expiry
with no periodic refresh behind it, because nothing would renew entries before they died.

## Refresh protocol

Warm-up is simply tick #1; there is no separate startup path, so startup and steady state cannot
drift apart. A pod that starts while the Dapr sidecar is still coming up fails tick one quietly and
succeeds on tick two.

**In the default mode the loop then stops.** `RefreshIntervalSeconds = 0` means there is no window to
re-check, so once a tick returns `Refreshed` or `SkippedWindowFresh` — the cluster's cache is filled —
`DiscoveryCacheRefreshHostedService` logs `DiscoveryCacheWarmUpCompleted` (50043) and returns. What
the short tick buys is purely retry: a first attempt that failed because the sidecar or the registry
was not ready is retried within a minute. `Failed` and `SkippedNotOwner` are deliberately **not**
treated as filled — a replica that merely holds the lock may still fail, and exiting on it would
leave this pod resolving live for the rest of its life.

A pod that starts later does not need a warm-up at all: L2 is shared, so it reads what the first pod
published and its own tick #1 answers `SkippedWindowFresh` (observed: `DiscoveryCacheWarmUpCompleted`
with that outcome, on a pod whose predecessor had already claimed the marker).

**The marker outlives the entries it speaks for.** With no expiry it records "this cluster was filled
once" forever, so if the domain entries were lost while it survived — a flushed cache, an eviction
policy reclaiming keys — no pod would bulk-refill them. The degradation is graceful rather than
silent: every lookup misses, resolves live and re-caches itself, which is the pre-cache behaviour one
domain at a time. To force a bulk refill, call `utilities/discovery/refresh`, which ignores the
marker.

| Key | Kind | Lifetime | Job |
|---|---|---|---|
| `discovery:bulk:v1:lock` | distributed lock | `WarmupLockLeaseSeconds` (30 s) | serializes the racers inside one window |
| `discovery:bulk:v1:refreshed-at` | cache entry | `RefreshIntervalSeconds`; **no expiry when that is 0** | records that the cluster has been filled (or, in periodic mode, defines the window) |
| `discovery:domain:v1:{domain}` | cache entry | `L2TtlSeconds`; **no expiry when that is 0** | the payload |

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
written, and the next tick retries. The bound is `lease + tick interval` ≈ 90 s. A tick whose window
is already served costs one cache read and stops.

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
  immediately, so the server-side TTL adds nothing to what a forced refresh sees.
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
| `L1TtlSeconds` | `60` | **how long any invalidation takes to reach the other pods.** Must be `> 0`, and `< RefreshIntervalSeconds` in periodic mode |
| `TickIntervalSeconds` | `60` | how fast a *failed* warm-up is retried — **not** a refresh rate |
| `RefreshIntervalSeconds` | **`0`** | `0` = no periodic refresh; invalidation is event-driven. A positive value restores the window |
| `L2TtlSeconds` | **`0`** | `0` = entries never expire. A positive value restores the dead-man switch and **requires** a positive `RefreshIntervalSeconds` |
| `UnreachableEvictionCooldownSeconds` | `30` | per domain; `0` switches failure-driven eviction off |
| `WarmupLockLeaseSeconds` | `30` | must be `< RefreshIntervalSeconds` in periodic mode |
| `DomainListEndpointTemplate` | `/{0}/functions/domain-list` | `{0}` = the registry domain; configurable for a gateway path variation |
| `DomainListExpectedMax` | `500` | the registry function's own page size — reaching it warns that the list may be truncated |

### Why the code default is off

This reverses a shipped decision, and domain teams consuming the runtime as a package inherit code
defaults — default-on would silently reintroduce a staleness window for all of them on upgrade. It is
enabled per deployment through `appsettings.json`, which operators already own and can revert. The
same reasoning already governs `ServiceDiscovery:Enabled`, `Provider`'s fallback to `http`, and
`Dapr:RequireRegistryEntry`.

### The invariants are enforced at startup

`ServiceDiscoveryOptionsValidator` rejects, in **every** mode:

- an expiry with no periodic refresh (`L2Ttl > 0` while `RefreshInterval = 0`) — entries would die on
  their own schedule with nothing renewing them, so every domain's next caller pays a live lookup,
  forever, while the cache still reports hits in between. Event-driven invalidation cannot cover for
  a TTL, because it renews nothing on a timer;
- an empty `DomainListEndpointTemplate`.

And in **periodic mode only** (`RefreshInterval > 0`), the original window relationships:
`L1Ttl >= RefreshInterval`, `TickInterval > RefreshInterval`, `WarmupLockLease >= RefreshInterval`,
and — when an expiry is also set — `RefreshInterval >= L2Ttl` and `L2Ttl < RefreshInterval + lease`.
They are scoped to that mode because applied to a zero they would reject the shipped defaults.

Every one of these degrades the cache **silently** when broken — it either stops serving or widens
its window, with no exception and no error log. Startup is the only place they are visible, which is
why they are a contract rather than a comment.

---

## Observability

On `Discovery.Resolve/{domain}`:

| Tag | Meaning |
|---|---|
| `vnext.discovery.resolution` | `cache` on a hit; `registry` / `convention` otherwise |
| `vnext.discovery.cache.age_seconds` | age of the entry that answered |

The age tag is the one that matters, and it matters **more** without a TTL: nothing else can reveal a
three-week-old entry, and it is the only way to answer *"was this routed by a stale entry?"* during an
incident — precisely the question the removal commit was written to avoid ever having to answer.

Logs (`WorkflowLogs`, EventIds 50001–50006 reused from the removed implementation so historical
queries keep working, plus 50036–50045): refresh started / refreshed / failed, domain-list fetch,
cache miss, skipped-fresh, skipped-not-owner, list-ceiling reached, domain-list endpoint missing,
cache operation failed, refresher-not-registered, and:

| EventId | Level | Meaning |
|---|---|---|
| `50043` `DiscoveryCacheWarmUpCompleted` | Information | the warm-up loop reached a filled cache and stopped ticking. Fires once per pod; its absence means the loop is still retrying or died |
| `50044` `DiscoveryEndpointEvicted` | **Warning** | a cached endpoint was dropped after a transport failure. **Look for this first** when cross-domain calls start failing; with no TTL it is the automatic recovery path, and its absence during a misrouting incident is itself the finding |
| `50045` `DiscoveryEndpointEvictionThrottled` | Debug | a failure inside the cooldown. A steady stream of this with no `50044` means the domain is *down*, not moved |

The deployment hook logs under 90001–90005 (`PublishCompleted*`); see
[Publish-completed hook](publish-completed-hook.md).

`QueryingSingleDomain` (50006) stays at `Information`: it fires once per cache miss, so a steady
stream of it now means the cache is not working.

---

## Operations

**Normally you do not need this.** A domain's own deployment calls
`definitions/publish/completed`, and an address that stopped answering is evicted on the first failed
call. Reach for the endpoint below when neither applies:

- a domain was registered with the **wrong** `baseUrl` — it may be answering, so nothing fails and
  nothing evicts;
- the failing traffic goes through a trigger task, whose call happens in the Execution host and
  therefore cannot report the failure;
- a domain moved and the callers are runtimes that are not being deployed.

```bash
curl -X POST localhost:4201/api/v1/utilities/discovery/refresh
```

Reads the registry and republishes every entry synchronously, then answers with what it did:

| `outcome` | Meaning |
|---|---|
| `Refreshed` | done; pods' in-process copies clear within `L1TtlSeconds` (60 s) |
| `SkippedNotOwner` | another replica is refreshing right now — its result applies cluster-wide |
| `Failed` | the registry could not be read; existing entries were left untouched — and with no TTL they stay until something else invalidates them, so this outcome needs a retry |
| `disabled` | there is nothing to refresh — `ServiceDiscovery:Enabled=false`, `Provider=dapr`, or `Cache:Enabled=false` |

**Roll back to a timer:** set `RefreshIntervalSeconds` to `3600` and `L2TtlSeconds` to `7200`. The
periodic window returns exactly as it was, and the event-driven triggers keep working on top of it.

**Roll back completely:** set `ServiceDiscovery:Cache:Enabled=false` and restart. The plain registry
client is then registered and the refresher never starts, so not even an entry written before the
flip remains readable — the pre-cache behaviour is restored literally, not approximately.

---

## Related

- [Remote App Service Architecture](remote-app-service-architecture.md) — the `Remote*` clients and
  the `ServiceDiscovery:Provider` switch this sits under.
- [Dapr Invocation Transport](dapr-invocation-transport.md) — the provider this cache deliberately
  does not touch.
- [Publish-completed hook](publish-completed-hook.md) — the deployment-time trigger that invalidates
  this cache, and the contract a domain's CD pipeline has to honour.
- [Trace Lanes](trace-lanes.md) / [Trace Span Tree](trace-span-tree.md) — where
  `Discovery.Resolve/{domain}` sits.
