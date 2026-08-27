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
- **Discovery `TimeoutSeconds: 30 → 5`.** The policy chain in `DiscoveryServiceCollectionExtensions` is `AddPolicyHandler(GetTimeoutPolicy)` → `GetRetryPolicy` → `GetCircuitBreakerPolicy` (registered outermost-first), and `client.Timeout = TimeSpan.FromSeconds(clientOptions.TimeoutSeconds)` (line ~55) additionally bounds the whole `SendAsync`. Both mean the **5s client/policy timeout wraps the entire retry sequence, not one attempt**. The retry policy's own backoff is exponential — `sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds * Math.Pow(2, retryAttempt - 1))`, i.e. 1s, 2s, 4s = 7s of sleep alone before three attempts even run — so a discovery resolution is now hard-bounded at ~5s total: retry attempts 2 and 3 can never complete inside that budget and attempt 1 rarely does either. `HandleTransientHttpError().Or<TimeoutRejectedException>()` inside the retry policy is consequently dead code, since the timeout policy sits outside the retry and its `TimeoutRejectedException` never reaches the retry's handler. Retry count stays at `MaxRetryAttempts: 3` (not mentioned in the ask), but that value is now effectively decorative under a 5s outer bound — the retry count and the timeout are coupled and cannot be tuned independently; raising one without the other buys nothing. The user tunes further from environment results, but should treat `TimeoutSeconds` and `MaxRetryAttempts` as one setting going forward.
- **The `POST utilities/discovery/refresh` endpoint is kept as a no-op** returning 200 with an explanatory message, plus a `deprecations.json` entry. It is `[ApiExplorerSettings(IgnoreApi = true)]` (an undocumented ops endpoint, not a published API), but the no-breaking-change policy applies and a no-op costs three lines. Removal comes a few versions later.
- **`Instance.Query.Prepare` span around `await WithDetailsAsync()`** in the repository, to split the leading window into "context/connection acquisition" versus "everything else (EF query compilation)". This is deliberately diagnostic: if Prepare dominates the lead, the cost is context/connection acquisition and the pool hypothesis stands; if Prepare is near zero, the remaining lead is EF query compilation or a connection opened lazily at execution, and the follow-up is Npgsql pool metrics rather than more spans. The plan states both readings so the result is actionable either way.

## Accepted risks

- **Long-poll amplification is accepted deliberately.** One discovery call per state-function poll for cross-domain subflows, replacing one per 5 minutes. The `Discovery.Resolve` span makes the real rate measurable in production, so the decision can be revisited with data instead of speculation.
- **Discovery becomes a hard dependency of every cross-domain operation.** Bounded by the existing circuit breaker and the reduced timeout.
- **An open circuit breaker silently serves the parent's view instead of the subflow's.** `InstanceQueryAppService.cs:574` catches a discovery (or any) failure while resolving a cross-domain subflow's state and falls back to main-flow transitions (`GetMainFlowTransitions`), logging only. This was tolerable when discovery was cached (a rare failure mode); now that discovery is a hard per-poll dependency, a breaker opened by an outage means **every** cross-domain-subflow long-poll silently returns the parent's transitions instead of the subflow's, with no error surfaced to the caller, and the response may itself be fingerprint-cached by the state function. Recorded here as a known behavior, not a surprise, so it can be prioritized deliberately rather than discovered in production.
- **Parked finding: registration-guard lock failure is indistinguishable from lock contention.** Aether's `TryAcquireLockAsync` swallows backend exceptions and returns `null` on both "held by another replica" and "backend unreachable" (e.g. Redis down at rollout). Under the new registration guard (see "Decisions taken"), every replica that gets `null` concludes another replica already owns the lease, skips, and starts healthy — but if the `null` was actually caused by an unreachable lock backend, **no replica registers**, and the domain comes up unregistered with no error, a failure mode that did not exist before this guard was added. This was parked during execution on the belief that distinguishing the two cases requires an Aether API change (a richer return type than `null`). The final reviewer disagreed and proposed a vnext-only alternative, not implemented here because it changes the not-acquired branch the user specified directly and is theirs to approve: on the not-acquired branch, call `IDomainDiscoveryResolver.GetEndpointAsync(ownDomain)`; if it returns not-found, register anyway. Roughly ten lines, entirely in this repo. Known trade-offs of that alternative, stated honestly: it adds one discovery call to every replica's startup path, and it has its own race — a replica that starts before the lock holder finishes registering will also see not-found and register too, so the guard degrades to "best-effort dedupe" rather than holding perfectly under that alternative.

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
