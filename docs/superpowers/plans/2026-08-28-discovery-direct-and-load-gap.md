# Discovery Without Cache + Trace Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make cross-domain resolution authoritative and visible — delete the discovery cache, always ask discovery, and give the resolution a span. Keep service registration but stop 20 replicas from all doing it. Then name the anonymous window inside `Instance.Load`.

**Architecture:** `DomainDiscoveryResolver` loses ~400 lines of bulk-cache/ETag machinery and reduces to a direct query wrapped in one `Discovery.Resolve/{domain}` span, which surfaces at all 32 call sites via ambient parenting. `DomainDiscoveryInitializationHostedService` keeps its registration step, now guarded by a non-blocking `TryAcquireLockAsync` whose lease is deliberately left to expire. The discovery HTTP timeout drops to 5s. Separately, `EfCoreInstanceRepository` gets one diagnostic span that splits `Instance.Load`'s leading gap.

**Tech Stack:** .NET 10, `System.Diagnostics.Activity`, Polly, xUnit + Shouldly + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-08-28-discovery-direct-and-load-gap-spec.md` — read it; it carries the measurements and the accepted risks that justify these choices.

## Global Constraints

- **Local commits only on branch `feature/trace-span-tree`. NEVER `git push`.** No branch/merge/rebase.
- **The working tree carries uncommitted files that are NOT yours** — `launchSettings.json`s, `appsettings.json` files, `ScriptConditionEvaluator.cs`, and files touched by a concurrent session (`TransitionDataMapper.cs`, `FunctionAppService.cs`). **Stage only the files your task modifies.** Never `git add -A` / `git commit -a`. Two tasks here legitimately edit `orchestration/.../appsettings.json`, which is user-dirty: after staging, diff it and confirm your hunk is only what you intended; report anything else instead of committing blind.
- **NO replacement cache.** Not a distributed cache, not an in-memory memo, not a per-request memo, not a `Lazy<T>`. Always ask discovery. This is the point of the change, not an oversight to fix.
- **No behavioral change beyond what each task states.** Instance loading results, pipeline semantics, hook/outbox spans are untouched.
- New tag constants go in `TelemetryConstants.TagNames` (`src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`), `vnext.<area>.<thing>` convention, each with an XML `<summary>` saying what it means AND when it is set.
- Public types/members get XML `<summary>` docs; comments explain WHY. Match the voice of the file you edit.
- Logging goes through `WorkflowLogs.cs` `[LoggerMessage]` extensions — never raw `logger.Log*`. Follow the existing EventId blocks.
- Regression gate: `dotnet build vnext.sln -v q --nologo` → 0 errors; `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` and `dotnet test test/BBT.Workflow.Infrastructure.Tests --nologo -v q` (plus `Domain.Tests` if you touch Domain) with **no NEW failing test name** versus a baseline you capture yourself BEFORE your change. Note: `/private/tmp/.../scratchpad/master-failures.txt` exists but is stale (58 names vs ~16 actual) — capture your own baseline and say so.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/BBT.Workflow.Application/Discovery/IDomainDiscoveryResolver.cs` | Drops `RefreshBulkCacheAsync` | 1 |
| `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs` | Cache machinery deleted; `Discovery.Resolve` span added | 1, 2 |
| `src/BBT.Workflow.Application/Discovery/ServiceDiscoveryOptions.cs` | `DiscoveryCacheSeconds` removed; `TimeoutSeconds` default | 1, 3 |
| `orchestration/.../Controllers/Utilities/UtilityController.cs` | Refresh endpoint becomes a no-op | 1 |
| `vnext-meta/deprecations.json` | Records the deprecated endpoint | 1 |
| `orchestration/.../appsettings.json` | Drops `DiscoveryCacheSeconds`; `TimeoutSeconds: 5` | 1, 3 |
| `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` | Discovery span tags | 2 |
| `orchestration/.../HostedServices/Discovery/DomainDiscoveryInitializationHostedService.cs` | Bulk step removed; registration lock-guarded | 4 |
| `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` | Registration skip/claim log messages | 4 |
| `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs` | `Instance.Query.Prepare` span | 5 |
| `docs/runtime/trace-span-tree.md` | Span rows for both new spans | 2, 5 |

**Task order matters:** Task 1 deletes the cache; Task 2 spans what remains. Doing 2 before 1 would span code about to be deleted.

---

### Task 1: Delete the discovery cache

**Files:**
- Modify: `src/BBT.Workflow.Application/Discovery/IDomainDiscoveryResolver.cs` — delete `RefreshBulkCacheAsync` from the interface.
- Modify: `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs` — this is the bulk of the task.
- Modify: `src/BBT.Workflow.Application/Discovery/ServiceDiscoveryOptions.cs` — remove `DiscoveryCacheSeconds`.
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Utilities/UtilityController.cs`.
- Modify: `vnext-meta/deprecations.json`.
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json` — remove `DiscoveryCacheSeconds`.
- Test: `test/BBT.Workflow.Infrastructure.Tests/Discovery/DomainDiscoveryResolverTests.cs` (create if absent; if an existing test file covers this resolver, extend it and say which).

**Interfaces:**
- After this task `IDomainDiscoveryResolver` has exactly one member: `GetEndpointAsync(string domain, EndpointKind preferredKind, CancellationToken)`.
- `GetEndpointAsync` keeps its signature and its `Result<DiscoveryEndpoint>` contract, including `WorkflowErrors.DomainEndpointNotFound(domain)` on a 404 and the disabled-discovery failure when `options.Enabled` is false.

- [ ] **Step 1: Capture the regression baseline**

Run the two suites named in Global Constraints and save the failing-name lists BEFORE touching anything. This is your comparison set.

- [ ] **Step 2: Write the failing tests**

In `test/BBT.Workflow.Infrastructure.Tests/Discovery/DomainDiscoveryResolverTests.cs`, pin the post-change contract. Arrange against the real constructor (read it — after this task the `IDistributedCacheService` and `IDistributedLockService` dependencies should be gone from this class, which is itself part of the contract) with a stubbed `HttpMessageHandler`:

- **Every call queries discovery.** Two successive `GetEndpointAsync` calls for the same domain produce **two** HTTP requests. This is the test that would fail if anyone reintroduces a cache — name it so that intent is obvious (e.g. `Every_resolution_queries_discovery_no_caching`).
- **Disabled discovery fails without any HTTP call**: `options.Enabled = false` → failed `Result`, zero requests on the handler.
- **404 maps to `DomainEndpointNotFound`.**
- **A successful lookup returns the endpoint** with the right `Uri` and `AppId`, and honours `preferredKind` through `DetermineEndpointKind`.
- **No `If-None-Match` header is ever sent** — the ETag path is gone.

Read `QuerySingleDomainAsync` and `DetermineEndpointKind` before writing, and take the response JSON shape from `FunctionDataListResponse` / the single-domain response type actually used, not from imagination.

- [ ] **Step 3: Run the tests to verify they fail**

They should fail to compile or fail on behavior against the current cached implementation (the "two HTTP calls" test in particular is satisfied by nothing today). Paste the failure.

- [ ] **Step 4: Delete the cache machinery**

From `DomainDiscoveryResolver`, remove:
- `RefreshBulkCacheAsync`, `FetchAllPagesAsync`, `CheckDomainETagAsync`, `AddDomainToBulkCacheAsync`, `UpdateDomainInBulkCacheAsync`
- the constants `BulkCacheKey`, `BulkCacheLockKey`, `LockExpiryInSeconds`, `IfNoneMatchHeader`
- the records `BulkDomainCache`, `DomainEndpointItem`, and any `FunctionDataListResponse` / `PaginationLinks` types that become unused — **check each for other users before deleting**; if one is referenced elsewhere, leave it and say so in the report
- the `IDistributedCacheService` and `IDistributedLockService` constructor parameters, if nothing else in the class uses them
- the corresponding `ETagCheckResult` type if it is private to this class

`GetEndpointAsync` becomes: the disabled-discovery guard, then `QuerySingleDomainAsync`, returned directly. Keep the existing `WorkflowLogs` calls that still make sense (`QueryingSingleDomain`); remove log methods that only described cache events **only if nothing else calls them** — an orphaned `[LoggerMessage]` is harmless, a broken reference is not. List in your report which log methods you removed and which you left.

Also remove `DiscoveryCacheSeconds` from `ServiceDiscoveryOptions` and from the orchestration `appsettings.json`, and drop `RefreshBulkCacheAsync` from the interface.

- [ ] **Step 5: Turn the utility endpoint into a no-op**

In `UtilityController`, `RefreshDiscoveryCacheAsync` keeps its route and its 200, but no longer calls the resolver:

```csharp
    /// <summary>
    /// Deprecated no-op. Discovery is no longer cached — every resolution queries the registry
    /// directly — so there is nothing to refresh. The route is kept so existing runbooks and
    /// automation do not break; it will be removed a few versions after 0.0.85.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("utilities/discovery/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RefreshDiscoveryCacheAsync()
        => Ok(new { message = "Discovery is no longer cached; this endpoint is a deprecated no-op." });
```

Remove the now-unused `IDomainDiscoveryResolver` constructor dependency **if the controller has no other use for it** — check.

Add to `vnext-meta/deprecations.json` under `items`:

```json
{
  "id": "discovery-refresh-endpoint",
  "type": "endpoint",
  "component": "api",
  "path": "POST utilities/discovery/refresh",
  "deprecatedSince": "0.0.85",
  "removedAt": null,
  "replacement": null,
  "severity": "warning",
  "message": "Discovery is no longer cached; every resolution queries the registry directly. The endpoint is a no-op kept for compatibility."
}
```

Match the file's existing field order and formatting exactly; if `"type": "endpoint"` is not a value the file already uses, use the closest existing one and say which in your report.

- [ ] **Step 6: Run the tests to verify they pass**

New tests green; then the regression gate versus your Step 1 baseline.

- [ ] **Step 7: Commit**

```bash
git add src/BBT.Workflow.Application/Discovery/IDomainDiscoveryResolver.cs \
        src/BBT.Workflow.Application/Discovery/ServiceDiscoveryOptions.cs \
        src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs \
        orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Utilities/UtilityController.cs \
        orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json \
        vnext-meta/deprecations.json \
        test/BBT.Workflow.Infrastructure.Tests/Discovery/DomainDiscoveryResolverTests.cs
git commit -m "refactor(discovery): drop the endpoint cache, always ask the registry"
```

---

### Task 2: `Discovery.Resolve/{domain}` span

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`
- Modify: `docs/runtime/trace-span-tree.md`
- Test: extend `test/BBT.Workflow.Infrastructure.Tests/Discovery/DomainDiscoveryResolverTests.cs` from Task 1.

**Interfaces:** produces spans named `Discovery.Resolve/{domain}` carrying `vnext.discovery.domain`, `vnext.discovery.endpoint_kind`, and `span.category=business`.

**Why one span here covers everything:** the resolver is called from 32 sites (6 trigger task executors, the four `RemoteInstance*AppService`s, `RemoteAuthorizeAppService`, `RemoteRelatedInstanceReader`). A span inside `GetEndpointAsync` parents to whatever is ambient, so it appears under each caller with no per-call-site edits — and the discovery HTTP call becomes its child instead of an unattributed HttpClient span.

- [ ] **Step 1: Write the failing tests**

Add to the Task 1 test file, using an `ActivityListener` (the pattern is established in `test/BBT.Workflow.Application.Tests/EventBus/HookedDistributedEventBusSpanTests.cs` — read it for the listener setup and `ActivitySource` name matching):

- A successful resolution emits exactly one span named `Discovery.Resolve/{domain}` with the domain interpolated.
- Tags `vnext.discovery.domain` and `vnext.discovery.endpoint_kind` are set.
- A failed resolution (404) produces a span with `ActivityStatusCode.Error`.
- The span parents to the ambient activity: start one, resolve, assert `ParentId` equals the ambient's `Id`.

- [ ] **Step 2: Run the tests to verify they fail**

- [ ] **Step 3: Add the tag constants**

In `TelemetryConstants.TagNames`:

```csharp
/// <summary>Domain whose endpoint is being resolved from service discovery. Set on every Discovery.Resolve span.</summary>
public const string DiscoveryDomain = "vnext.discovery.domain";

/// <summary>Endpoint kind requested from service discovery (Url or AppId). Set on every Discovery.Resolve span.</summary>
public const string DiscoveryEndpointKind = "vnext.discovery.endpoint_kind";
```

- [ ] **Step 4: Add the span**

Wrap the body of `GetEndpointAsync`. Reuse an existing `ActivitySource` rather than inventing one **only if a suitable Infrastructure-side source already exists and is registered in every host's `Telemetry:Tracing:AdditionalSources`** — check `orchestration`, `execution`, and both workers' appsettings. If none fits, declare a static source on the class and add its name to every host's `AdditionalSources` in the same commit, exactly as the `BBT.Workflow.Instances.Events` source was handled on this branch. **State which route you took and why in your report** — a span on an unregistered source is silently invisible, which is the failure mode this step exists to avoid.

Start the span with the **implicit-parent overload** (name, or name+kind): passing an explicit `Activity.Current?.Context` leaves `Activity.Parent == null` and severs the baggage chain. That exact bug was found and fixed on this branch — do not reintroduce it.

Set `ActivityStatusCode.Error` with the failure message on the failure paths (disabled discovery, 404, and the transport/error branch), so a failed resolution reads as an error span rather than a silent success.

- [ ] **Step 5: Run the tests to verify they pass**, then the regression gate.

- [ ] **Step 6: Document**

Add a `Discovery.Resolve/{domain}` row to the span table in `docs/runtime/trace-span-tree.md`: the source, the two tags, that it appears under whichever caller triggered a cross-domain hop, and — worth stating for the next reader — that discovery is queried on **every** resolution because the endpoint cache was deliberately removed, so this span's rate is the true cross-domain resolution rate.

- [ ] **Step 7: Commit**

```bash
git commit -m "feat(telemetry): a span for cross-domain endpoint resolution"
```

---

### Task 3: Discovery timeout 30s → 5s

**Files:**
- Modify: `src/BBT.Workflow.Application/Discovery/ServiceDiscoveryOptions.cs` (the `TimeoutSeconds` default)
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`
- Check and modify if present: any other `appsettings.json` carrying a `ServiceDiscovery` block (search first — only orchestration is known to have one).

**Why:** with the cache gone, this timeout now sits in front of **every** cross-domain operation instead of a rare cache miss. Retry count stays at 3, so the worst case per resolution drops from ≈92s to ≈17s. 5s is a deliberate starting value the user will tune down from environment results — do not "improve" it.

- [ ] **Step 1:** Set `TimeoutSeconds` to `5` in the options default and in every appsettings `ServiceDiscovery` block that sets it. Leave `MaxRetryAttempts`, `RetryDelayMilliseconds`, `CircuitBreakerFailureThreshold` and `CircuitBreakerTimeoutSeconds` untouched.
- [ ] **Step 2:** Confirm the value actually reaches Polly: `DiscoveryServiceCollectionExtensions` uses `clientOptions.TimeoutSeconds` for both `client.Timeout` and `GetTimeoutPolicy(options)`. Quote both lines in your report — no code change expected, this is a verification step.
- [ ] **Step 3:** Build → 0 errors. No new tests required; if an existing test asserts the old default, update it and say so.
- [ ] **Step 4:** Commit (`chore(discovery): 5s timeout now that every hop queries the registry`).

---

### Task 4: Registration keeps running, but only on one replica

**Files:**
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/HostedServices/Discovery/DomainDiscoveryInitializationHostedService.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- Test: `test/BBT.Workflow.Application.Tests/` or `Infrastructure.Tests` — wherever a hosted service can be exercised; if neither project can host it, put the guard logic behind a small injectable seam and test that, and explain the choice in your report.

**The requirement, precisely:** registration stays (every service must register itself). It must not run on all 20 replicas. Guard with a lock. **A pod that does not get the lock must not wait and must not retry** — one pod registering healthily is enough.

- [ ] **Step 1: Write the failing tests**

Pin these four behaviors:
1. **Lock acquired → registration runs.**
2. **Lock NOT acquired (`TryAcquireLockAsync` returns `null`) → registration is NOT called, and the service completes without throwing.** Startup must not abort for a replica that skipped.
3. **Lock acquired and registration throws → the lock is RELEASED, and the exception propagates** (that pod aborts; k8s restarts it; another replica can then acquire).
4. **Lock acquired and registration succeeds → the lock is NOT released.** This is the counter-intuitive one and the heart of the design: the lease is left to expire so the next replica in the same rollout skips. Releasing on success would let replica 2 re-register seconds later, which defeats the guard entirely.

- [ ] **Step 2: Run the tests to verify they fail.**

- [ ] **Step 3: Implement**

Remove the `RefreshBulkCacheAsync` step (it no longer exists after Task 1). Keep registration, guarded:

```csharp
        await using var scope = scopeFactory.CreateAsyncScope();
        var registrationService = scope.ServiceProvider.GetRequiredService<IDomainRegistrationService>();
        var lockService = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();

        // Registration is service-level, not pod-level: the registered baseUrl comes from
        // vNextApi:BaseUrl and is identical on every replica, and registering starts a workflow
        // instance in the registry domain. Twenty replicas therefore mean nineteen redundant
        // instance starts per rollout. One healthy registration is enough.
        //
        // The lease is a once-per-window guard, NOT a mutex: it is deliberately left to expire
        // after a successful registration. Replicas start seconds-to-minutes apart, so releasing
        // on success would simply let the next one register again.
        var handle = await lockService.TryAcquireLockAsync(lockKey, RegistrationLeaseSeconds, stoppingToken);
        if (handle is null)
        {
            // No wait, no retry: another replica owns this rollout's registration.
            logger.DomainRegistrationSkippedNotLockOwner(domainName);
            return;
        }

        try
        {
            await registrationService.RegisterDomainAsync(stoppingToken);
        }
        catch
        {
            // Hand the window back so another replica can try immediately; this pod then aborts
            // startup as before and is restarted.
            await handle.ReleaseAsync(CancellationToken.None);
            throw;
        }
```

**The lock key must carry the registered content**, not just the domain:

```
discovery:register:{domainName}:{short hash of baseUrl + healthUrl}
```

Without the content in the key, a hotfix redeploy that changes `vNextApi:BaseUrl` inside the lease window would find the lease held and silently skip registering the new URL. With it, a changed URL is a different key and registers immediately, while an unchanged redeploy correctly skips.

`RegistrationLeaseSeconds` should comfortably exceed a rolling restart (suggest `300`) and stay well below the redeploy cadence. Make it a named constant with a comment explaining both bounds.

Resolving `domainName`, `baseUrl` and `healthUrl` here duplicates what `DomainRegistrationService` computes internally. **Do not copy its logic** — expose what you need from that service (e.g. a small `GetRegistrationIdentity()` returning the three values, used by both) so the key can never drift from what is actually registered. Say in your report how you exposed it.

Keep the outer try/catch: a genuine registration failure still logs Critical and rethrows.

Add to `WorkflowLogs.cs`: `DomainRegistrationSkippedNotLockOwner` (Information — an operator reading one pod's logs should see why it did not register) and `DomainRegistrationClaimed` (Information, logged by the owner). Use a fresh EventId in the block neighbouring the other discovery/startup messages.

- [ ] **Step 4: Run the tests to verify they pass**, then the regression gate.

- [ ] **Step 5: Commit**

```bash
git commit -m "fix(discovery): register once per rollout, not once per replica"
```

---

### Task 5: Name the anonymous window inside `Instance.Load`

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs`
- Modify: `docs/runtime/trace-span-tree.md`

**Why, with the measurement:** across 300 live `Instance.Load` spans, the gap between the parent and its `Db.SELECT` children is **almost entirely leading** — mean lead 0.60ms, between-children 0.26ms, trail **0.03ms**. So it is not materialization and not a miscalculation: `Db.SELECT` is command-level instrumentation (`CommandExecuting`→`CommandExecuted`) and simply does not cover the window before the first command. That window is p50 0.63ms but p90 2.40ms and max 88ms, and the big values are scattered over time rather than clustered at startup — consistent with connection acquisition under pool pressure, which the current spans cannot confirm.

- [ ] **Step 1: Add the span**

In `EfCoreInstanceRepository`, wrap the `await WithDetailsAsync()` call inside `FindByIdentifierAsync` (and the sibling identifier finders that use the same shape — find them; if wrapping all of them would duplicate the line more than three times, extract a tiny private helper rather than repeating it):

```csharp
using var prepare = PipelineStepActivityHelper.StartOperationActivity("Instance.Query.Prepare");
var query = (await WithDetailsAsync()).AsSplitQuery();
```

Comment WHY it exists: it splits `Instance.Load`'s leading gap into DbContext/connection acquisition versus everything else, because the EF command spans start only when the command is issued.

Note `PipelineStepActivityHelper` lives in the Application layer — if Infrastructure cannot reference it, use whichever activity helper Infrastructure already uses for spans of this kind and say which in your report. Do not create a new `ActivitySource` for this without also registering its name in every host's `AdditionalSources`.

- [ ] **Step 2: Verify the span appears where expected**

A unit test asserting the span exists is acceptable and cheap; a live check is better if the environment is already up. Either way, confirm the span sits **inside** `Instance.Load` and **before** the first `Db.SELECT`.

- [ ] **Step 3: Document how to read it**

In `docs/runtime/trace-span-tree.md`, add the row and — importantly — the interpretation, because this span exists to answer a question rather than to decorate the tree:

- **Prepare dominates the lead** → the cost is DbContext/connection acquisition; the connection-pool hypothesis stands and the follow-up is Npgsql pool metrics plus pool sizing.
- **Prepare is near zero and a lead remains** → the cost is EF query compilation, or a connection opened lazily at execution time; the follow-up is a compiled-query or pool-warmup investigation, not more spans.

Also record the measured baseline (lead p50 0.63ms / p90 2.40ms / max 88ms, trail 0.03ms) so a future reader can tell whether things moved.

- [ ] **Step 4: Build → 0 errors, regression gate, commit**

```bash
git commit -m "feat(telemetry): name the pre-command window inside Instance.Load"
```

---

## Self-Review

**Spec coverage:** ask 1 (span at the query point) → Task 2. Ask 2 (remove cache, always remote, no replacement) → Task 1, reinforced by a Global Constraint and by the "two HTTP calls" test that fails if anyone reintroduces caching. Ask 3 (registration stays, lock-guarded, no wait/retry) → Task 4, with all four behaviors pinned including the non-obvious no-release-on-success. Ask 4 (30s→5s) → Task 3. Ask 5 (the Instance.Load gap) → answered in the spec's Findings with measurements, and made permanently observable by Task 5.

**Ordering dependency:** Task 2 must follow Task 1, or it would add a span to code Task 1 deletes. Task 3 touches `ServiceDiscoveryOptions.cs` which Task 1 also edits — sequential execution avoids a conflict, but a reviewer should confirm Task 3 did not resurrect `DiscoveryCacheSeconds`.

**Known soft spots, stated rather than hidden:**
(a) Task 2's `ActivitySource` choice is left to the implementer with an explicit warning about unregistered sources being silently invisible — this is a real decision the plan cannot make without reading four appsettings files, and the step demands the choice be reported.
(b) Task 4's lock-key construction depends on exposing the registration identity from `DomainRegistrationService`; the plan forbids copying that logic and requires the mechanism be reported, because a key that drifts from the registered content silently breaks the guard.
(c) Task 5 may find `PipelineStepActivityHelper` unreachable from Infrastructure; the step names the fallback rather than assuming.
(d) Task 1's dead-type cleanup (`FunctionDataListResponse`, `PaginationLinks`, log methods) is explicitly conditional on checking for other users — the plan does not assert they are unused.

**Accepted risk carried from the spec:** long-poll amplification — the state function resolves cross-domain subflows on every poll, replacing ~1 discovery call per 5 minutes with one per poll. Accepted deliberately; Task 2's span makes the real rate measurable so the decision can be revisited with production data.
