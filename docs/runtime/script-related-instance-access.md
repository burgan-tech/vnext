# Script Related Instance Access

Mapping scripts can read the data of a *related* instance — the parent that started this instance as a
SubFlow/SubProcess, or one of this instance's own correlations — instead of duplicating that data across
the parent/child boundary. Exposed as `context.Related` (`IRelatedInstanceAccessor`,
`src/BBT.Workflow.Domain/Scripting/Related/`).

## API

`context.Related` is always available inside any mapping script (`IMapping`, `IConditionMapping`,
`ISubFlowMapping`, `IOutputHandler`, `IEventMapping`) — a no-op accessor is substituted when no reader is
wired, so existing scripts and tests are unaffected.

| Member | Returns | Notes |
|---|---|---|
| `HasParent` | `bool` | Synchronous, no read. `true` when this instance was started as a SubFlow/SubProcess. |
| `SubKeysAsync(ct)` | `Task<IReadOnlyList<string>>` | Distinct sub workflow keys, in correlation creation order. Loads the correlation list but reads no instance data. |
| `ParentAsync(ct)` | `Task<RelatedInstanceView?>` | `null` when this instance has no parent. |
| `SubAsync(subFlowKey, ct)` | `Task<RelatedInstanceView?>` | The most recently created correlation whose key matches, or `null`. |
| `SubsAsync(subFlowKey = null, ct)` | `Task<IReadOnlyList<RelatedInstanceView>>` | All correlations, oldest first. Omit the key to get every correlation. Batched — never N+1. |

`RelatedInstanceView` fields: `InstanceId`, `Key`, `Domain`, `Flow`, `FlowVersion`, `Status`,
`CurrentState`, `IsCompleted`, `CorrelationCompleted`, `TerminalOutcome`, `SubFlowType`, `Data`.

`IsCompleted` is the **target instance's** own status (`Status == "C"`). `CorrelationCompleted` is
whether the **relationship** is closed, and is always `null` for the parent direction (no correlation
is involved there). The two can disagree: during the subflow completion window the child instance is
already `Completed` while the parent's correlation record is still open. Treat them as answering
different questions — don't conflate them.

`Data` is an `ExpandoObject` (via `dynamic`) on **both** the same-domain and cross-domain paths. The
remote reader converts the deserialized payload before handing it back, so
`context.Related.ParentAsync().Data.someField` behaves the same regardless of where the related
instance actually lives.

> Calling `context.Related` after the owning `ScriptContext` has been disposed throws
> `ObjectDisposedException` — a disposed context must not silently answer "no parent".

## Examples

Read parent data from a subflow's input binding:

```csharp
var parent = await context.Related.ParentAsync();
var limit = GetPropertyValue<decimal>(parent?.Data, "creditLimit", 0m);
```

Gate a view on a child's completion:

```csharp
var kyc = await context.Related.SubAsync("kyc-flow");
return kyc?.IsCompleted == true;
```

Aggregate over repeated subprocesses:

```csharp
var uploads = await context.Related.SubsAsync("doc-upload");
return uploads.Count(u => u.CorrelationCompleted == true) >= 3;
```

Check whether a subflow key was ever started, without paying for a data read:

```csharp
var keys = await context.Related.SubKeysAsync();
if (!keys.Contains("kyc-flow"))
{
    return false;
}
```

## Key resolution

`SubAsync` / `SubsAsync` match against `InstanceCorrelation.SubFlowName` — the **sub workflow key**.
There is no separate alias field. When several correlations share a key (a loop, or a subprocess started
repeatedly from the same state), `SubAsync` returns the one with the newest `CreatedAt`; use `SubsAsync`
to see every match and choose explicitly.

Both active and completed correlations are visible — a finished subflow's output is not lost the moment
its correlation closes.

## Scope

Exactly one hop. The parent's parent, and a child's own children, are not reachable through this API —
by design. There is no ancestor chain and no `Root` accessor.

## Reads run inside the current transaction

A related read executes inside the current transition's database transaction, so on the same-domain path
it observes that transition's own uncommitted writes. This is intentional: within one transition, a
script should see the engine's own in-flight state rather than a stale snapshot from before the
transition started.

## Security: reads are unfiltered

Related-instance reads use the engine's own system identity, not the calling user's. They deliberately
skip:

- the query-role check (`QueryAccessDenied` is never raised),
- `x-roles` field-level filtering,
- extensions,
- the data-function response cache.

This keeps script decisions deterministic across users and lets the API work in scheduled, automatic,
event, and background-job contexts, where no caller identity exists at all.

> **`x-roles` does not follow a copy.** If an output mapping writes a related instance's restricted
> field into *this* instance's data, that field becomes readable by any client entitled to read this
> instance — the source field's `x-roles` protection does not travel with the value. Only copy fields
> you intend to expose, and say so at the point you copy them.

Every cross-domain read is logged (`RelatedInstanceCrossDomainRead`, event id 20432), emitted by
`RoutedRelatedInstanceReader` — the only component that knows a dispatch went remote. It identifies the
**target** instance being read, not the instance whose script triggered the read: the reader only ever
sees the target's `RelatedInstanceRef`, never the caller. A batch read (`SubsAsync`) logs once per
distinct remote `(domain, flow)` group, with `Count` equal to that group's size.

For the deployment-facing security posture of the two internal HTTP endpoints backing cross-domain
reads — including the fact that they carry **no in-app authorization at all** — see
[API and Service Contracts § Internal-Only Endpoints](../contracts/api-and-service-contracts.md#internal-only-endpoints).
That is the authoritative statement; it is not repeated in full here.

## Errors

| Situation | Behavior |
|---|---|
| No parent, no matching correlation, or the target instance is gone | `null` (or an empty list), Debug log |
| Read failure — cross-domain HTTP error, DB error, discovery failure | throws `RelatedInstanceAccessException`; the error boundary handles it |
| More than `MaxResolutionsPerContext` distinct related instances resolved in one script | throws `RelatedInstanceAccessException` |

Absence is data; failure is a fault. A silent `null` on a read failure would be indistinguishable from
"there is no parent" and could quietly produce a wrong business decision.

## Performance

Nothing is pre-fetched. Resolved instances are memoized for the lifetime of the owning `ScriptContext`,
so repeated reads across `onExecute` / `onExit` / `onEntry` and view conditions within one transition
cost a single read. `SubsAsync` batches — it never issues one call per correlation. Parallel task
branches created via `CreateParallelBranch` share the coordinator's memo and, for reads targeting the
same instance, its correlation cache.

Same-domain reads never leave the process: `RoutedRelatedInstanceReader` dispatches locally when
`IRuntimeInfoProvider.IsDomainMatch` holds, and only crosses the network for a foreign domain.

## Configuration

```json
{
  "Workflow": {
    "Scripting": {
      "RelatedAccess": {
        "MaxResolutionsPerContext": 10
      }
    }
  }
}
```

`MaxResolutionsPerContext` defaults to 10 distinct related instances resolved per `ScriptContext`. Memo
hits do not count against it.

## Internal endpoints

Cross-domain reads go over two internal-only routes on `InstanceController`, excluded from the public
Swagger group (`[ApiExplorerSettings(IgnoreApi = true)]`):

| Method | Route | Response |
|---|---|---|
| GET | `api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/internal/related-data` | `200` with the instance snapshot; `204` when the instance does not exist (deliberately not `404` — a `404` would be indistinguishable from a misrouted request). |
| POST | `api/v{version}/{domain}/workflows/{workflow}/internal/related-data/batch` | `200` with an array of snapshots (possibly `[]`; ids that don't resolve are omitted, not errored); `400` when more than 100 instance ids are requested in one call. |

These are internal-to-internal calls: no caller identity, no query-role check, no `x-roles` filtering,
no extensions. See
[API and Service Contracts § Internal-Only Endpoints](../contracts/api-and-service-contracts.md#internal-only-endpoints)
for how their exposure is (and is not) controlled in each environment.
