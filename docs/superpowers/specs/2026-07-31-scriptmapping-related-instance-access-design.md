# ScriptMapping Related Instance Data Access — Design

**Date:** 2026-07-31
**Status:** Approved (design)
**Scope:** Runtime (`BBT.Workflow.Domain`, `BBT.Workflow.Application`, `BBT.Workflow.Infrastructure`, `orchestration` host)

## 1. Problem

Mapping scripts (input binding, output binding, view conditions, subflow mappings) frequently need data
that lives on a *related* instance rather than the current one:

- A subflow deciding based on its parent's data (e.g. credit limit, customer segment).
- A parent rendering a view or taking a decision based on a child subflow/subprocess result
  (e.g. "is KYC complete?").

Today the only way to do this is to duplicate the data into the current instance's own data — via
output mappings that copy fields across the parent/child boundary. That duplication is redundant
storage, goes stale, and grows the instance data payload for every consumer.

There is no way for a script to read another instance's data at all: `ScriptBase` only receives
`IScriptServices` (DaprClient, ILogger, IConfiguration), and `ScriptContext` exposes only the current
`Instance`.

## 2. Goals

- A first-class, script-facing API to read the data of a related instance — one hop **up** (parent) or
  one hop **down** (own correlations).
- No remote call when the related instance lives in the same domain (gateway strategy).
- No end-user authorization coupling: the call is made by the engine inside its own correlation
  boundary, not on behalf of a caller, so it must be deterministic across users and must work in
  scheduled / automatic / event / job contexts where no caller identity exists.
- Zero cost when unused: nothing is pre-fetched.

## 3. Non-goals

- **`$instance[main].data` / `$instance[SubKey].data` token syntax is out of scope.** The deliverable
  is the C# surface (`context.Related.*`). Token resolution inside view/schema templates is a separate
  work item.
- No traversal beyond one hop: no ancestor chain, no grandchildren, no `Root` accessor
  (`root.instance.id` exists in `ExtraProperties` but is intentionally not surfaced).
- No write access. Related instances are read-only through this API.
- No new correlation alias/`SubKey` column. Keys resolve against the existing
  `InstanceCorrelation.SubFlowName` (the sub workflow key).

## 4. Existing building blocks (verified)

| Concern | What already exists |
|---|---|
| Parent identity on a subflow instance | `Instance.ExtraProperties`: `parent.id`, `parent.key`, `parent.domain`, `parent.flow`, `parent.version`, `parent.state`, `parent.transition`, `parent.flowtype`, `root.instance.id` — written by `SubflowStarter` from `DomainConsts.MetaDataKeys` |
| Child identity on a parent instance | `InstanceCorrelation`: `SubFlowInstanceId`, `SubFlowDomain`, `SubFlowName`, `SubFlowVersion`, `SubFlowType` (S/P), `SubFlowCurrentState`, `IsCompleted`, `TerminalOutcome`, `ParentState` |
| Correlations incl. completed | `IInstanceCorrelationRepository.GetByParentAsync(parentInstanceId)` — returns active **and** completed |
| Domain routing | `IRuntimeInfoProvider.IsDomainMatch(domain)`; `RoutedInstanceQueryGateway` / `RoutedInstanceCommandGateway` pattern (`Infrastructure/Gateway`) |
| Cross-domain transport | `IDomainDiscoveryResolver` + Dapr service invocation; internal-only endpoints already exist on `InstanceController` (`sub/state`, `sub/fault`, `child-cancel`, `child-fault`, `longpoll/ack`) |
| Script handler async-ness | All mapping contracts are async (`Task<ScriptResponse>`, `Task<bool>`) — an async accessor is usable everywhere |

Two constraints shaped the design:

1. **`ScriptBase` cannot host the helper.** `modules/BBT.Workflow.Modules.Scripting` references only
   Roslyn and `Dapr.Client`; it has no reference to `BBT.Workflow.Domain`, so it cannot see
   `ScriptContext` or `Instance`. The accessor therefore lives on `ScriptContext` (Domain), which
   already holds the instance snapshot, its `ExtraProperties`, and its correlations.
2. **`Instance.ChildCorrelations` is not sufficient.** `EfCoreInstanceRepository.WithDetailsAsync()`
   includes `ChildCorrelations.Where(!IsCompleted)`. Since completed correlations must be readable
   (otherwise a finished subflow's output is unreachable), the accessor loads correlations lazily via
   the repository instead of widening the default include — per the repository include-strategy rule.

## 5. Architecture

```
ScriptContext (Domain)
  └─ Related : IRelatedInstanceAccessor          ← script-facing surface
       ├─ resolves refs from Instance.ExtraProperties (up)
       ├─ resolves refs from IInstanceCorrelationRepository.GetByParentAsync (down, lazy + memo)
       └─ IRelatedInstanceReader  (Domain contract)
            └─ RoutedRelatedInstanceReader (Infrastructure/Gateway)
                 ├─ IsDomainMatch  → LocalRelatedInstanceReader  (Application)  → repository read
                 └─ else           → RemoteRelatedInstanceReader (Infrastructure) → internal endpoint
```

### 5.1 Script-facing surface (Domain)

`src/BBT.Workflow.Domain/Scripting/Related/`

```csharp
public interface IRelatedInstanceAccessor
{
    /// True when this instance was started as a SubFlow/SubProcess (parent.id present).
    bool HasParent { get; }

    /// Sub workflow keys of this instance's correlations. Metadata only — no data fetch.
    /// Requires the correlation list, so the first access performs the lazy correlation load.
    Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default);

    Task<RelatedInstanceView?> ParentAsync(CancellationToken cancellationToken = default);

    /// Newest correlation (by CreatedAt) whose SubFlowName equals subFlowKey.
    Task<RelatedInstanceView?> SubAsync(string subFlowKey, CancellationToken cancellationToken = default);

    /// All correlations, optionally filtered by sub workflow key. Batched — never N+1.
    Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default);
}

public sealed class RelatedInstanceView
{
    public Guid InstanceId { get; init; }
    public string? Key { get; init; }
    public string Domain { get; init; } = string.Empty;
    public string Flow { get; init; } = string.Empty;
    public string? FlowVersion { get; init; }

    /// Target instance status code: A / B / C / F / P.
    public string Status { get; init; } = string.Empty;
    public string? CurrentState { get; init; }

    /// Target instance reached a terminal completed status (Status == C).
    public bool IsCompleted { get; init; }

    /// Correlation closed flag. Null for the parent direction (no correlation involved).
    public bool? CorrelationCompleted { get; init; }

    /// Correlation terminal outcome. Null for the parent direction.
    public string? TerminalOutcome { get; init; }

    /// "S" (SubFlow) or "P" (SubProcess) for the down direction; null for the parent.
    public string? SubFlowType { get; init; }

    /// Latest instance data of the related instance, unfiltered.
    public dynamic? Data { get; init; }
}
```

`IsCompleted` (target instance status) and `CorrelationCompleted` (correlation closed) are deliberately
separate fields — a subflow instance can be `Completed` while the parent correlation is still open
(the subflow completion window), and conflating them produces wrong decisions.

`HasParent` is synchronous (reads `ExtraProperties`). `SubKeysAsync` is async because the correlation
list itself has to be loaded.

Usage:

```csharp
// input binding on a subflow — read parent data
var parent = await context.Related.ParentAsync();
var limit = GetPropertyValue<decimal>(parent?.Data, "creditLimit", 0m);

// view condition on a parent — is the KYC child done?
var kyc = await context.Related.SubAsync("kyc-flow");
return kyc?.IsCompleted == true;

// aggregate over repeated subprocesses
var uploads = await context.Related.SubsAsync("doc-upload");
return uploads.Count(u => u.CorrelationCompleted == true) >= 3;
```

### 5.2 Reference resolution

**Up (parent).** Read from `Instance.ExtraProperties`:

| Field | Key |
|---|---|
| InstanceId | `parent.id` |
| Domain | `parent.domain` |
| Flow | `parent.flow` |
| FlowVersion | `parent.version` |

If `parent.id` is missing or unparsable, `HasParent` is `false` and `ParentAsync()` returns `null`.
This is the normal case for a root instance — not an error.

**Down (correlations).** On the first `SubAsync` / `SubsAsync` / `SubKeysAsync` call, the accessor calls
`IInstanceCorrelationRepository.GetByParentAsync(Instance.Id)` (active + completed) and memoizes the
list. `Instance.ChildCorrelations` is not used — its default include filters out completed rows.

`SubAsync(key)` selects the correlation with the newest `CreatedAt` among those whose `SubFlowName`
equals `key` (ordinal comparison). `SubsAsync(key)` returns every match ordered by `CreatedAt`
ascending; `SubsAsync(null)` returns all correlations regardless of type.

`RelatedInstanceView.CorrelationCompleted`, `TerminalOutcome` and `SubFlowType` are filled from the
correlation row, not from the read result — the correlation is the parent's own record of the
relationship and is authoritative for it.

### 5.3 Internal read path

```csharp
// Domain
public sealed record RelatedInstanceRef(Guid InstanceId, string Domain, string Flow, string? FlowVersion);

public sealed class RelatedInstanceSnapshot
{
    public Guid InstanceId { get; init; }
    public string? Key { get; init; }
    public string Domain { get; init; } = string.Empty;
    public string Flow { get; init; } = string.Empty;
    public string? FlowVersion { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? CurrentState { get; init; }
    public bool IsCompleted { get; init; }
    public dynamic? Data { get; init; }
}

public interface IRelatedInstanceReader
{
    Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default);
}
```

`RoutedRelatedInstanceReader` (Infrastructure/Gateway) mirrors `RoutedInstanceQueryGateway`:

```csharp
_runtimeInfoProvider.IsDomainMatch(reference.Domain)
    ? _local.ReadAsync(reference, ct)
    : _remote.ReadAsync(reference, ct);
```

`ReadManyAsync` groups references by domain, dispatches the same-domain group locally in one query and
each foreign domain group as a single batch call.

**Partial failure is all-or-nothing, deliberately.** If any domain group fails, `ReadManyAsync` returns
a failed `Result` for the whole batch — it does not return the snapshots that succeeded. A partial set
would hand the script four children when there are five, with no way to tell the difference, so a
predicate like `uploads.Count(u => u.CorrelationCompleted == true) >= 3` could silently produce the
wrong answer. That is exactly "treating a fault as absence", which §5.5 forbids: an unreachable domain
is a fault, so the whole call faults. Instances that simply do not exist are still omitted silently —
that is absence, and it is data.

**Transaction visibility.** The local reader opens a fresh DI scope per flow group, but that isolates
only the `IServiceProvider` — Aether's ambient `CompositeUnitOfWork` keys DbContexts by `(Type, schema)`
and enlists a newly opened one in the *same* `DbConnection`/`DbTransaction` as the surrounding
transition. A related read therefore observes the current transition's uncommitted writes. That is the
intended semantic: within one transition a script should see the engine's own in-flight state rather
than a stale snapshot. It is recorded here because the isolation is easy to assume and is not there.

**`LocalRelatedInstanceReader` (Application).** Wraps `currentSchema.Use(flow)` and reads the instance
via `IInstanceRepository` with `AsNoTracking` and `DataList` only. It deliberately does **not** go
through `InstanceQueryAppService.GetInstanceDataAsync`, therefore:

- `IsInstanceQueryAllowedAsync` (query-role check → `QueryAccessDenied`) is **not** applied,
- `x-roles` field-level filtering is **not** applied,
- extensions do **not** run,
- the data-function response cache is **not** consulted or populated.

The result is the raw `LatestData` of the target instance.

**Rationale for the system identity.** The read is performed by the engine within its own
parent/child correlation frame, not on behalf of an end user. The correlation link *is* the
authorization boundary. Propagating caller roles instead would make the same script produce different
results for different users — a determinism violation for decision and view-selection logic — and
would fail outright in scheduled, automatic, event and background-job contexts where no caller exists.

**Security consequence that must be documented.** Because `x-roles` filtering is bypassed, an output
mapping that copies a related instance's field into the current instance's data makes that field
reachable by any client entitled to read the current instance. `x-roles` protection does not follow
the copy. The docs must state this explicitly, and every cross-domain read is logged
(`RelatedInstanceCrossDomainRead`).

**`Data` must be an `ExpandoObject` on both paths.** `RelatedInstanceSnapshot.Data` is `dynamic?`, which
is `object` at the CLR level, and `System.Text.Json` selects converters by *declared* type — so
`ExpandoObjectJsonConverter` never fires for it and a deserialized payload stays a boxed `JsonElement`.
The local path yields an `ExpandoObject` (`InstanceData.Attributes` → `JsonElement.ToDynamic()`). Left
alone, `context.Related.ParentAsync().Data.SomeField` would work for a same-domain parent and throw
`RuntimeBinderException` for a cross-domain one. The remote reader therefore converts via
`JsonDocumentExtensions.ToDynamic()` (which recurses into nested objects and arrays) before returning,
so scripts behave identically regardless of where the related instance lives.

**`RemoteRelatedInstanceReader` (Infrastructure).** Resolves the target app id via
`IDomainDiscoveryResolver` and calls new internal endpoints on the orchestration host:

| Method | Route |
|---|---|
| GET | `api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/internal/related-data` |
| POST | `api/v{version}/{domain}/workflows/{workflow}/internal/related-data/batch` |

Both live on `InstanceController` alongside the existing internal-to-internal endpoints
(`sub/state`, `child-cancel`, …), follow the same conventions, and are excluded from the public
Swagger group. The batch body is `{ "instanceIds": ["<guid>", ...] }`; the response is a list of
`RelatedInstanceSnapshot`. Instances that do not exist are omitted from the batch response rather than
returned as errors.

### 5.4 Caching and limits

The accessor memoizes per `ScriptContext`:

- `ConcurrentDictionary<Guid, RelatedInstanceView>` for resolved views,
- one memo slot for the correlation list.

Lifetime equals the `ScriptContext` lifetime (the transition pipeline, since `ScriptContext` is held in
`TransitionExecutionContext.Cache` and cleared at Finalize). No distributed cache: related data can
change inside the same pipeline and a stale read would corrupt a decision.

`ScriptContext.Dispose` clears the memo. `CreateParallelBranch` gives the branch an accessor bound to
the branch's instance snapshot but sharing the same thread-safe memo and reader — parallel branches
only read.

`RelatedAccessOptions.MaxResolutionsPerContext` defaults to **10** distinct related instances resolved
per `ScriptContext`. Memo hits do not count. `SubsAsync` counts the number of instances it resolves.
`RelatedAccessOptions` is bound from configuration (`Workflow:Scripting:RelatedAccess`) and registered
as a singleton option so deployments can raise or lower the limit without a code change.

### 5.5 Error semantics

| Situation | Behaviour |
|---|---|
| No parent (`parent.id` absent) | `ParentAsync()` → `null`, Debug log |
| No correlation for the requested key | `SubAsync()` → `null`; `SubsAsync()` → empty list; Debug log |
| Target instance not found (deleted / never persisted) | `null` (omitted from batch results), Debug log |
| Read failure — cross-domain HTTP error, DB error, discovery failure | **throw** `RelatedInstanceAccessException`, Warning log; the error boundary handles it |
| `MaxResolutionsPerContext` exceeded | **throw** `RelatedInstanceAccessException`, Warning log |

Rationale: a silent `null` on a read failure is indistinguishable from "there is no parent", so it
would silently produce a wrong business decision. Absence is data; failure is a fault.

The reader layer stays on the Result pattern (`Result<RelatedInstanceSnapshot?>`) per the project's
error-handling standard — infrastructure failures are returned, not thrown, across the reader boundary.
The accessor is the single place that converts a failed `Result` into
`RelatedInstanceAccessException`, so scripts never have to inspect a `Result`. A successful `Result`
carrying `null` means not-found and is passed through as `null`.

### 5.6 Wiring

- `ScriptContext.Related` — non-null; a no-op accessor (`HasParent = false`, empty results) is used
  when no reader is available, so existing unit tests keep working without changes.
- `ScriptContext.Builder.WithRelated(IRelatedInstanceAccessor)`.
- `ScriptContextBuilder` (`Domain/Scripting/Factory/Services`) constructs the accessor from the
  instance snapshot, the injected `IRelatedInstanceReader`, `IInstanceCorrelationRepository`,
  `RelatedAccessOptions` and a logger. All existing `IScriptContextFactory` consumers
  (`TransitionDataMapper`, `FunctionAppService`, `EventAppService`, `StartSubflowJobHandler`,
  `StateNotifyJobHandler`, …) pick it up without changes.
- DI: `RoutedRelatedInstanceReader` registered in
  `Infrastructure/.../GatewayServiceCollectionExtensions.cs` next to the existing routed gateways.

## 6. Logging

New `[LoggerMessage]` partials in `BBT.Workflow.Domain/Logging/WorkflowLogs.cs`, 20xxx (instance) event
id family, unique ids continuing the existing sequence:

| Method | Level | Purpose |
|---|---|---|
| `RelatedInstanceResolved` | Debug | instanceId, direction, targetInstanceId, domain, flow |
| `RelatedInstanceNotFound` | Debug | instanceId, direction, key |
| `RelatedInstanceCrossDomainRead` | Debug | instanceId, targetDomain, targetFlow, count |
| `RelatedInstanceResolutionFailed` | Error | instanceId, direction, targetInstanceId, targetDomain, targetFlow, reason |
| `RelatedInstanceResolutionLimitExceeded` | Warning | instanceId, limit |
| `RelatedInstanceReadFailed` | Error | exception, targetInstanceId, targetFlow |

`RelatedInstanceResolutionFailed` is `Error` rather than `Warning` because the accessor throws
immediately after logging it; this file reserves `Warning` for swallowed, degrade-gracefully failures.
`RelatedInstanceReadFailed` gives the Application-layer reader a `WorkflowLogs` method that accepts the
caught exception, so no code needs the forbidden raw `logger.LogError`.

No raw `logger.Log*` calls.

## 7. Testing

**`Domain.Tests`**
- `ParentAsync` resolves from `ExtraProperties`; returns `null` when `parent.id` is absent, malformed,
  `Guid.Empty`, or stored as non-string JSON, and when `parent.domain`/`parent.flow` are missing or
  stored as an unexpected type. Metadata readers fail closed — they never fabricate a `ToString()`
  value, which would otherwise produce a reference to a domain or flow that never existed.
- `SubAsync` picks the newest `CreatedAt` among same-key correlations.
- `SubsAsync(null)` returns every correlation including completed ones and both `S` and `P` types.
- `CorrelationCompleted` / `TerminalOutcome` / `SubFlowType` come from the correlation row, and
  `IsCompleted` from the read snapshot — asserted on a case where they disagree (subflow completed,
  correlation still open).
- Memoization: two identical calls perform one read.
- `MaxResolutionsPerContext` exceeded throws.
- Read failure throws; not-found returns `null`.
- `CreateParallelBranch` shares the memo and does not re-read.
- `Dispose` clears the memo.

**`Application.Tests`**
- `RoutedRelatedInstanceReader` dispatches local on domain match and remote otherwise.
- `ReadManyAsync` groups by domain and issues one call per domain.
- `LocalRelatedInstanceReader` returns unfiltered data: an `x-roles`-restricted field is present in the
  result, and no `QueryAccessDenied` is produced when no roles are supplied.
- Multi-schema: the read happens inside `currentSchema.Use(flow)`.

**`Infrastructure.Tests`**
- Internal endpoint contract: route shape, response body, batch omits unknown ids, endpoint absent from
  the public Swagger group.

## 8. Documentation and metadata

- New: `docs/runtime/script-related-instance-access.md` — API reference, up/down semantics, key
  resolution and multiplicity rules, error semantics table, and a prominent warning that copying
  related data into instance data bypasses `x-roles`.
- Update `docs/README.md` navigation grouping.
- Update `.claude/rules/vnext-workflow-developer.md` with a quick-reference block.
- Update `vnext-meta/features.json` (new capability + the two internal endpoints) and
  `component-registry.json` if the accessor is registered as a script capability. Version alignment
  follows `common.props`.

## 9. File inventory

**New**
```
src/BBT.Workflow.Domain/Scripting/Related/IRelatedInstanceAccessor.cs
src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessor.cs
src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceView.cs
src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceRef.cs
src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceSnapshot.cs
src/BBT.Workflow.Domain/Scripting/Related/IRelatedInstanceReader.cs
src/BBT.Workflow.Domain/Scripting/Related/RelatedAccessOptions.cs
src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessException.cs
src/BBT.Workflow.Domain/Scripting/Related/NullRelatedInstanceAccessor.cs
src/BBT.Workflow.Application/Instances/Related/LocalRelatedInstanceReader.cs
src/BBT.Workflow.Infrastructure/Gateway/RoutedRelatedInstanceReader.cs
src/BBT.Workflow.Infrastructure/Gateway/RemoteRelatedInstanceReader.cs
docs/runtime/script-related-instance-access.md
```

**Modified**
```
src/BBT.Workflow.Domain/Scripting/Models.cs                      (ScriptContext.Related, Builder, Dispose, CreateParallelBranch)
src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs
src/BBT.Workflow.Domain/Scripting/Factory/Contracts/IScriptContextBuilder.cs
src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs
src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/GatewayServiceCollectionExtensions.cs
orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs
docs/README.md
.claude/rules/vnext-workflow-developer.md
vnext-meta/features.json
```

No database migration. No workflow schema change.

## 10. Decisions log

| # | Decision | Alternative rejected because |
|---|---|---|
| 1 | Accessor on `ScriptContext`, lazily resolving | `IScriptServices` helper — the module cannot see Domain types, forcing scripts to hand-read `ExtraProperties`. Eager preload — pays DB/remote cost on every pipeline step even when unused. |
| 2 | Key = `correlation.SubFlowName`, newest wins, plus a list API | New alias column — migration + schema + vnext-meta churn, and opt-in leaves the current need unmet. `ParentState` key — subprocesses can start repeatedly from one state. |
| 3 | Rich projection incl. completed correlations | Data-only — forces scripts to read state from a second place. Active-only — a finished subflow's output becomes unreachable. |
| 4 | System/internal read, unfiltered | Caller roles — non-deterministic per user and unavailable in scheduled/event/job contexts. Definition allowlist — schema + migration cost, default-off blocks the current need. |
| 5 | Per-context memo, no distributed cache, limit 10 | TTL cache — stale data corrupts decisions and needs invalidation coordination with the data-function cache. No cache — repeated reads multiply on the hot path. |
| 6 | Absence → `null`, failure → throw | Failure → `null` — indistinguishable from "no parent", silently produces wrong decisions. |
| 7 | `$instance[...]` token syntax out of scope | Separate concern (template resolution), separate consumers (view/schema), separate work item. |
