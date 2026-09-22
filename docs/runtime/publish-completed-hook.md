# Publish-Completed Hook

The one call a domain's CD pipeline makes after it has finished publishing a package — and the
runtime work that hangs off it.

```
POST /api/v1/definitions/publish/completed
{ "packageName": "@burgan-tech/vnext-onboarding", "version": "1.2.2", "domain": "onboarding" }
```

---

## Why it exists

A component deployment publishes one component at a time: the package is unpacked and every
Workflow, Task, View, Function, Schema and Mapping is sent to `POST definitions/publish`
individually. Some runtime work belongs to the *deployment*, not to any one component — and doing it
per component would repeat it N times for an effect that is identical each time.

There was already a call at exactly this moment. Both callers — the init host's package job and the
`wf` CLI's `sync` / `update` / `reset` — ended their publish loop with one `GET
definitions/re-initialize`, and that endpoint had been reduced to a no-op:

```csharp
// With the lazy Redis-only cache strategy, there is no in-memory state to reinitialize.
// This method is retained for API compatibility but is now a no-op.
```

The shape was right and the content was gone. `publish/completed` takes its place, with the work put
back and a name that says what the call means.

> **`definitions/re-initialize` is removed.** Not deprecated — removed. It did nothing, so nothing
> that called it loses behaviour, but a pipeline still pointing at it gets a 404 and both old callers
> **swallow that silently** (a yellow spinner line, a `log.warn`; neither changes an exit code). See
> [Rollout](#rollout).

## What it does today

One hook: **`discovery-cache`**, which forces a full re-read of the domain discovery registry.

That hook is not an optimisation. The [discovery endpoint cache](discovery-endpoint-cache.md) holds
**no TTL** — entries are filled once and live until something invalidates them — so this call is its
only *automatic* invalidation. A pipeline that does not make it leaves the runtime resolving
cross-domain calls by whatever it learned at startup, until the pod restarts.

## Contract

**Call it once**, after the last `publish` of the package, and read `success` from the body.

```json
{
  "success": true,
  "hooks": [ { "name": "discovery-cache", "outcome": "Refreshed", "message": null } ]
}
```

| Property | Meaning |
|---|---|
| `success` | `false` when **any** hook failed. This is the field to gate on |
| `hooks[].name` | stable identifier; a pipeline may key on it |
| `hooks[].outcome` | `Failed`, or a named success the hook chose |
| `hooks[].message` | detail; present on a failure |

`discovery-cache` outcomes: `Refreshed` (done), `SkippedNotOwner` (another replica is reading the
registry right now and its result applies cluster-wide — a success), `Disabled` (there is nothing to
refresh here: `ServiceDiscovery:Enabled=false`, `Provider=dapr`, or `Cache:Enabled=false`), `Failed`
(the registry could not be read — **retry**, because nothing else will).

> **`Enabled=false` has to be part of that `Disabled` condition**, and a live run is what proved it.
> The cache *registration* checks only `Provider` and `Cache:Enabled`, so a runtime with discovery
> switched off still has a refresher; the hook called it, the bulk read went to the default registry
> address, nothing answered, and the endpoint returned `success: false` — on every deployment of
> every single-domain runtime, for a feature that deployment had turned off.

Request body fields are all optional. `packageName` and `version` are identification for the logs and
traces, so that *"which deployment refreshed this?"* has an answer during an incident. `domain`, when
supplied, is checked against the runtime's own domain — a pipeline pointed at the wrong runtime fails
loudly instead of cheerfully refreshing a stranger's caches.

### The status code is always 200

A failed hook is reported in the body, not as a non-2xx. By the time this call runs the components
are already published: a 500 would be read as a failed deployment, and collapsing the per-hook detail
into a status code would throw away the only information that says what did not happen.

### It is idempotent

Calling it twice for one deployment is harmless — a retried CD step, an operator repeating it, the
init host and a manual call. Nothing upstream deduplicates, so every hook must tolerate it. The
discovery refresh is guarded by its own distributed lock, so two overlapping calls produce one
registry read.

---

## Adding a hook

Implement `IPublishCompletedHook` and register it. Nothing in the controller or the app service
changes — that is the point of the seam.

```csharp
public sealed class MyPostDeploymentHook(IMyThing thing) : IPublishCompletedHook
{
    public string Name => "my-thing";
    public int Order => 200;

    public async Task<Result<string>> ExecuteAsync(
        PublishCompletedInput input, CancellationToken cancellationToken)
    {
        await thing.DoItAsync(cancellationToken);
        return Result<string>.Ok("Done");
    }
}

// where the feature is wired, not in a central switch
services.AddScoped<IPublishCompletedHook, MyPostDeploymentHook>();
```

Rules a hook has to hold up:

- **Idempotent.** See above.
- **Independent.** Every registered hook runs even after an earlier one fails, so a hook must not
  assume its predecessors succeeded. Stopping at the first failure would let an unrelated
  registration's *order* decide whether the rest of a deployment's post-work happens at all.
- **Ordered by `Order`, ties broken by `Name`** — deterministic regardless of DI registration order.
- **Failure is data, not an exception.** Return `Fail`; a throw is caught and recorded as the same
  failure, it just loses the error code.
- **Name it as a contract.** A CD pipeline may key on `Name`; renaming it breaks that.

---

## Callers

| Caller | Where |
|---|---|
| init host package job | `init/VNext.Init.Host/package-api-server.js` → `publishCompleted()`, after `processPackage` |
| `wf` CLI | `vnext-workflow-cli`, `src/lib/api.js` → called by `sync`, `update`, `reset` |

Both call it **unconditionally**. In the init host it used to be opt-in (`reInitialize`, default
`false`) — a flag that switched a no-op on and off; it is gone, and passing it is ignored. The
outcome is recorded in the job result as `results.publishCompleted`, and a failure sets the job's own
`success` to `false`: a swallowed warning here is indistinguishable from a healthy deployment, which
is how a domain ends up serving stale cross-domain endpoints with nobody looking.

A **cancelled** package job skips the call, along with everything else; partial uploads are not
rolled back.

## Rollout

`re-initialize` is gone and its 404 is silent in both callers, so the order matters:

1. release the `wf` CLI version that calls `publish/completed` **before or with** the runtime,
2. record the minimum `wf` version in `vnext-meta/version-manifest.json`,
3. tell the domain teams.

Step 3 is load-bearing, not courtesy: a domain that keeps an old `wf` gets a yellow line nobody
reads, no discovery invalidation, and — because there is no TTL — no recovery until its pod restarts
or someone calls `utilities/discovery/refresh`. This is not a prediction; it was measured against a
runtime carrying this change, with an init host that still pointed at the old path:

```
⚠ Failed to trigger re-initialization (HTTP 404)
✓ Job 58836665-… completed — Package processing failed. No packages were loaded.
```

A 404 for the invalidation, a warning nobody acts on, and a job that still reports `completed`.

## Observability

`WorkflowLogs`, EventIds **90001–90005**:

| EventId | Level | Meaning |
|---|---|---|
| `90001` `PublishCompletedReceived` | Information | the call arrived, with package/version and hook count. **Its absence after a deployment is the failure mode** — an unmade call looks exactly like a healthy one |
| `90002` `PublishCompletedHookSucceeded` | Information | one hook's named success |
| `90003` `PublishCompletedHookFailed` | Warning | a hook returned a failure; the rest still ran |
| `90004` `PublishCompletedHookFaulted` | Error | a hook threw; the rest still ran |
| `90005` `PublishCompletedFinished` | Information | how many hooks ran and how many failed |

## Related

- [Discovery Endpoint Cache](discovery-endpoint-cache.md) — the cache this call invalidates, and why
  it no longer has a TTL.
- `init/VNext.Init.Host/README.md` — the package-publish job and its result shape.
