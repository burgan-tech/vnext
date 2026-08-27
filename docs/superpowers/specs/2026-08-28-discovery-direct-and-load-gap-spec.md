# Spec: Discovery Without Cache, Discovery Span, and the Instance.Load Pre-Command Gap

Date: 2026-08-28 · Status: approved for planning · Owner: platform
Repo: vnext (`/Users/U0B006/Documents/repos/burgan-tech/vnext`), branch `feature/trace-span-tree`

## The ask

1. Cross-domain resolution goes through `IDomainDiscoveryResolver` and has **no span** — make it visible in the trace tree at the point we query it.
2. **Remove the discovery cache entirely.** Always go remote. Do not add any replacement cache.
3. `DomainDiscoveryInitializationHostedService`: only the bulk-cache step goes. **Registration stays** — every service must register itself. But 20 replicas all registering is pointless: guard it with a lock. Pods that do not get the lock must **not wait and not retry** — one pod registering healthily is enough.
4. Discovery HTTP timeout `30s → 5s` as a starting value (to be tuned down from environment results).
5. Investigate the `Instance.Load: 13ms` vs `Db.SELECT: 2.2 + 1.8 + 1.4ms` gap — loss, or miscalculation?

## Findings (verified in code and against live traces, 2026-08-28)

### The Instance.Load gap: neither loss nor miscalculation

Measured over 300 real `Instance.Load` spans in local Elastic, decomposing each parent against its children:

| | mean | p50 | p90 | max |
|---|---|---|---|---|
| **lead** (parent start → first `Db.SELECT`) | 0.60 ms | 0.63 | 2.40 | **87.99** |
| between children | 0.26 ms | | | |
| **trail** (last `Db.SELECT` end → parent end) | **0.03 ms** | | | |
| parent total | 2.30 ms | 4.25 | 30.52 | 201.73 |

- **The gap is ~entirely LEADING.** Trail ≈ 0.03 ms, which **refutes** the natural first hypothesis that entity materialization / jsonb→ExpandoObject deserialization is the hidden cost. It is not. (`LatestOnlyInstanceLoading: true` in orchestration means one data row, not the whole history.)
- `Db.SELECT` comes from `AddEntityFrameworkCoreInstrumentation` (renamed via `EnrichWithIDbCommand` to `Db.{VERB}`), which is **command-level**: EF's `CommandExecuting` → `CommandExecuted`. The parent is measured correctly; the children simply do not cover the whole parent. Nothing is lost and nothing is miscalculated — there is an **uncovered window**, and it sits before the first command.
- Three `Db.SELECT` children per load is expected: `WithDetailsAsync()` + `AsSplitQuery()` → root + `DataList` + `ChildCorrelations`.
- What lives in that window (`EfCoreInstanceRepository.FindByIdentifierAsync`): `await WithDetailsAsync()` → `GetDbContextAsync` (DbContext resolution, connection acquisition) and then EF query compilation inside `FirstOrDefaultAsync` before the command goes out.
- **Ruled out: a hidden `SET search_path` round trip.** Aether's Npgsql provider runs in `QualifiedNames` mode (`SchemaSwitchingMode`, `QualifiedNamesCommandInterceptor`: "No search_path manipulation is performed"), so schema selection costs no extra statement.
- **Not cold start.** The oldest samples have ~1 ms leads; the largest leads (88, 54, 48, 35 ms) are scattered across the whole window. The profile is consistent with **connection acquisition under pool pressure**, matching the known preprod DB-pool exhaustion incident — but this is a hypothesis the current instrumentation cannot confirm, which is exactly why the window needs a span.

### Discovery: the resolution is a black hole today

- `DomainDiscoveryResolver.GetEndpointAsync` has no span, **and neither does the Redis read inside it**: Aether's `DistributedCacheBase` emits no activity, and vnext's `Cache.Get` spans come only from `CacheActivityHelper`, used by `CacheSet<T>`. So the bulk-cache read, the ETag revalidation call and the fallback query are all either invisible or land as unattributed HttpClient spans.
- 613 lines, of which ~400 are cache machinery: `RefreshBulkCacheAsync`, `FetchAllPagesAsync` (pagination), `CheckDomainETagAsync`, `AddDomainToBulkCacheAsync`, `UpdateDomainInBulkCacheAsync`, the four cache records, `BulkCacheKey`/`BulkCacheLockKey`/`LockExpiryInSeconds`, and `DiscoveryCacheSeconds`.
- **32 call sites, each resolving once per operation** — removing the cache adds one HTTP round trip per cross-domain operation, not a multiplier.
- **The sharp edge is long-polling.** `InstanceQueryAppService.cs:540` resolves a cross-domain subflow while walking the active-correlation chain for the **state function** — the long-poll endpoint. Today: ~1 discovery call per domain per 5 minutes (`DiscoveryCacheSeconds: 300`). After removal: **one discovery call per poll**. Discovery is itself a vNext domain (`Domain: "discovery"`, flow `domain-registration`, `/discovery/functions/domain-lookup`), so each resolution is a real workflow function invocation, not a DNS-cheap lookup.
- Resilience already exists and is genuinely wired (`DiscoveryServiceCollectionExtensions:82-84`): timeout policy, `WaitAndRetryAsync(MaxRetryAttempts)`, `CircuitBreakerAsync(CircuitBreakerFailureThreshold, CircuitBreakerTimeoutSeconds)`. A discovery outage therefore fast-fails once the breaker opens rather than hanging every caller indefinitely.
- What the cache bought was latency; what it cost was **staleness** — up to 5 minutes of routing to a moved or dead endpoint, which the ETag machinery existed to paper over. Removing it makes discovery authoritative, which is its purpose.

### Registration is service-level, so one pod is genuinely enough

`DomainRegistrationService.RegisterDomainAsync` posts `{ domainName, baseUrl, healthUrl }` where `baseUrl` comes from configuration `vNextApi:BaseUrl` — **identical across every replica**. It is service registration, not pod registration. It performs it by starting a workflow instance
(`POST {registry}/{domain}/workflows/{registryFlow}/instances/start?sync=true`), so 20 replicas today mean 20 identical instance starts per rollout.

Current failure behavior: any failure in the hosted service logs Critical and rethrows from `BackgroundService.ExecuteAsync`, aborting host startup.

## Decisions taken

- **Delete the discovery cache outright**, including `RefreshBulkCacheAsync` from `IDomainDiscoveryResolver`. `GetEndpointAsync` reduces to the direct query path. No replacement cache of any kind.
- **`Discovery.Resolve/{domain}` span inside `GetEndpointAsync`.** One span there surfaces at all 32 call sites, because it parents to whatever is ambient — no per-call-site edits.
- **Registration guarded by `TryAcquireLockAsync`**, which returns `null` without waiting — exactly the required semantics. On failure to acquire: log and return, **do not abort startup**. On acquire: register; on registration failure **release the lock** so another replica can try, then rethrow (that pod aborts, k8s restarts it, the retry happens naturally).
- **The lock is a once-per-window guard, not a mutex: it is NOT released after a successful registration.** Releasing on success would let the next replica in the same rollout re-register, which defeats the purpose — replicas start seconds-to-minutes apart, not simultaneously. The lease is left to expire.
- **The lock key carries the registered content** (`discovery:register:{domain}:{hash of baseUrl+healthUrl}`). A redeploy that changes the registered URL gets a fresh key and registers immediately even inside the previous lease window; a redeploy that changes nothing correctly skips. Without this, a hotfix that moves the base URL inside the lease window would silently fail to register.
- **Discovery `TimeoutSeconds: 30 → 5`.** Retry count stays at 3 (not mentioned in the ask), so the worst case per resolution becomes ≈ 5×3 + 2×1s delay ≈ 17s, down from ≈ 92s. The user tunes further from environment results.
- **The `POST utilities/discovery/refresh` endpoint is kept as a no-op** returning 200 with an explanatory message, plus a `deprecations.json` entry. It is `[ApiExplorerSettings(IgnoreApi = true)]` (an undocumented ops endpoint, not a published API), but the no-breaking-change policy applies and a no-op costs three lines. Removal comes a few versions later.
- **`Instance.Query.Prepare` span around `await WithDetailsAsync()`** in the repository, to split the leading window into "context/connection acquisition" versus "everything else (EF query compilation)". This is deliberately diagnostic: if Prepare dominates the lead, the cost is context/connection acquisition and the pool hypothesis stands; if Prepare is near zero, the remaining lead is EF query compilation or a connection opened lazily at execution, and the follow-up is Npgsql pool metrics rather than more spans. The plan states both readings so the result is actionable either way.

## Accepted risks

- **Long-poll amplification is accepted deliberately.** One discovery call per state-function poll for cross-domain subflows, replacing one per 5 minutes. The `Discovery.Resolve` span makes the real rate measurable in production, so the decision can be revisited with data instead of speculation.
- **Discovery becomes a hard dependency of every cross-domain operation.** Bounded by the existing circuit breaker and the reduced timeout.

## Out of scope

- Any replacement cache, memoization, or per-request resolution memo.
- Npgsql pool metrics (a follow-up, gated on what `Instance.Query.Prepare` shows).
- Changing retry counts or circuit-breaker thresholds beyond the timeout value.
- The inbox/outbox and event-hook span work already shipped on this branch.

## Success criteria

1. `DomainDiscoveryResolver` has no cache read, write, refresh, ETag or bulk path left; `RefreshBulkCacheAsync` is gone from the interface; `DiscoveryCacheSeconds` is removed from options and appsettings.
2. A cross-domain operation shows `Discovery.Resolve/{domain}` in the trace, with the discovery HTTP call as its child.
3. With N replicas starting, exactly one performs registration; the others log a skip and start normally. A registration failure by the lock holder releases the lock and aborts only that pod.
4. `TimeoutSeconds` is 5 in the shipped appsettings.
5. `POST utilities/discovery/refresh` still answers 200 and is recorded in `deprecations.json` (`deprecatedSince: 0.0.85`).
6. `Instance.Query.Prepare` appears inside `Instance.Load`, before the first `Db.SELECT`, and the trace-span-tree doc records how to read it.
7. No behavioral change beyond the above: hook/outbox spans, pipeline semantics and instance loading results all unchanged. No new failing test name.
