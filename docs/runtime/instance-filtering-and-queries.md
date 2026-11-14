# Instance Filtering and Queries

## Purpose

`InstanceQuery` (`BBT.Workflow.Filtering`) is the fluent, type-safe way to describe instance queries
in mapping scripts — the replacement for hand-concatenated GraphQL filter JSON. One builder, two
terminals:

| Terminal | Produces | Used for |
| --- | --- | --- |
| `.First()` / `.Last()` | `InstanceFilter` — resolve **one** instance | Event correlation (`EventMappingResult.Selector`) |
| `.Build()` | `InstanceQuerySpec` — a **list/report** query | `GetInstancesTask.SetFilterSpec`, query strings for the list endpoint |

`BBT.Workflow.Filtering` is in the script engine's default imports — `.csx` mappings can use it
without a `using`. The spec serializes to **byte-identical** wire JSON the existing list endpoint
consumes, so no endpoint or engine changes are involved; when no spec is used, nothing changes.

## Fields

Two kinds of fields, distinguished by the name you pass:

- **Instance columns** — bare names, whitelisted: `id`, `key`, `flow`, `status`, `effectiveStatus`,
  `instanceType`, `state` / `currentState`, `effectiveState`, `effectiveStateType`,
  `effectiveStateSubType`, `stage`, `createdAt`, `modifiedAt`, `completedAt`. Unknown column names
  throw at build/SQL time. `status`, `effectiveStatus` and `instanceType` accept names or codes
  (`Active` / `A`, `SubFlow` / `S`) — on the list, GraphQL and `.Build()` spec terminals. The
  `.First()` / `.Last()` selector terminal has no name resolution for any of the three: it compares
  the literal text against the stored code, so pass codes there.

> **`effectiveStatus` filters the stored column, not the served field.** A filter runs in SQL; the
> served `metadata.effectiveStatus` goes through a read-time clamp for the SubFlow completion window
> (see `docs/runtime/state-function-cache-and-etag.md`). So `effectiveStatus eq Completed` can match
> a parent whose served `effectiveStatus` reads `Busy` — for "is this flow done?", filter on `status`.

> **`modifiedAt` is the last time the instance changed.** Every transition stamps it, a same-state one
> included, whether it leaves the instance Active, Busy, Completed (a cancel too) or Faulted. An
> `updateData` stamps it with the `EnteredAt` of each data row it writes — its request body or its
> tasks' output, the last row winning; one that changes no data, stage, tags or key writes nothing and
> moves nothing. "Transition" means a hop a request, timer, event or chain fires: an internal SubFlow or
> long-poll resume that rests Busy moves it only through the status projection writes listed below.
> Earlier runtimes moved it only on the set-based status writes, so a finished instance showed a time
> from before its final transition started. `LastTouchedAt` (`COALESCE(ModifiedAt, CreatedAt)`) follows it.
>
> The stamp rides on a write the hop makes anyway wherever the hop has one. `Instance.ChangeState`
> stamps at order 50 when the state moves; a same-state hop has nothing to write there, so
> `ChangeStateStep` leaves its stamp pending (`PipelineDirectives.DeferModifiedAtStamp`) and the end of
> the hop applies it, where its outcome is known (`TransitionSettlement`, or the handoff in
> `TransitionPipeline`) — unless the write that closes the hop already stamped it:
>
> | Transition | Stamped by | Extra cost |
> | --- | --- | --- |
> | Changes the state | `Instance.ChangeState` (order 50), in the UPDATE that writes the new state | none |
> | Completes, cancels or faults | `Complete` / `Cancel` / `Fault` (same instant as `completedAt`), in the status UPDATE | none |
> | Same state, ends with the Busy release (the start transition into the pre-positioned initial state, a `$self` shared transition resting Active) or pauses at the long-poll arm | the release or arm CAS that closes the hop | none |
> | Same state, ends without a release (parked in Busy, waiting on a SubFlow, or the status is not held) | `Instance.MarkModified` at the end of the hop (settlement, or the post-commit handoff), sent by its commit | one UPDATE of its own, after the hop's last save; the admission's Busy flip, when there was one, stamped the hop's start and this moves it to the hop's end |
> | Changes the stage, tags or key (any transition, `updateData` included) | `SetStage` / `AddTags` / `SetKey`, only when the value changes | none — the changed column is written anyway |
> | `updateData` that writes data | each data row it writes — body (`CreateTransitionRecordStep`) or task output (`TaskExecutionEngine`), via `AppendAsync(touchInstance: true)` | one extra statement per row, in the batch that inserts it: same round trip, the row already locked |
>
> Status ownership is not consulted: a job re-entry is classified as the owner without having flipped
> anything (a parent waiting on its SubFlow), so only the outcome says which write closes the hop. A
> same-state hop that continues in-process (an inline automatic transition) is closed by the next hop,
> whose own outcome stamps the instance.
>
> Every other set-based status write stamps it too (`EfCoreInstanceRepository`: Busy flip and release,
> un-fault, effective-status projection and its revert, long-poll arm). Data appended inside any other
> transition (request body, task output) is not stamped separately. A same-state hop that hands off
> post-commit work which is then settled with a Busy release (version-registry invalidation) is
> stamped at the handoff and again by that release — admin-rate traffic.
>
> **Cost** (one-off measurements, 2026-11-05/06, harness not committed). An instance-row UPDATE that
> changes only `ModifiedAt` is one non-HOT row version — the indexed `LastTouchedAt` changes, so the
> `Instances` indexes get a new entry (up to ten today; five are partial, seven apply to an Active instance
> without a long-poll token or incident) — and about 650 bytes of WAL (Testcontainers Postgres 16,
> 500 operations, `pg_stat_user_tables` and `pg_current_wal_lsn()` deltas). It is paid inside the
> instance's row lock, so it shows on a busy instance. `pgbench` on a local Docker Postgres 18 replaying
> the `updateData` append (row lock, two data rows per hop, `Instances` DDL and indexes copied from a lab
> schema, 20-second runs alternated), stamping per row as the runtime does — average throughput, with the
> single alternated runs in brackets: over 20,000 instances (8 clients) about −4% (+1% to −13%); over 50
> instances (16 clients) −5% (−3% to −7%); one instance under five concurrent `updateData` calls about
> −20% (−5% to −35%).
> One stamp per hop measured −2% and −6% on the last two, but no write can tell reliably which row is
> its hop's first: keying it on the database transaction was tried and reverted, because Npgsql reuses
> the transaction object on a pooled connection and later hops stopped stamping
> (`UpdateData_hops_in_consecutive_transactions_each_stamp`).
> A single stamp sent as its own round trip cost about as much as two batched ones; a same-state hop
> that ends without a release pays that (about 0.25 ms locally). In the vnext-example E2E (local Docker,
> OpenObserve; eight branch runs, two of them on the final tree, against two master runs, 27–31
> `updateData` row inserts per run) the per-run median of a stamped row insert was 0.64–0.81 ms against
> 0.36–0.48 ms on master — about +0.3–0.4 ms per row, so roughly +0.7 ms for an `updateData` whose body
> and task both write. Inserts that do not stamp read 0.38–0.45 ms against 0.36–0.39 ms; with two master
> runs that is not separated from run-to-run drift.

> **`instanceType` records how the instance was STARTED, not a live relationship.** `Root` / `R`,
> `SubFlow` / `S`, `SubProcess` / `P`, stamped once at creation and never updated — so it stays
> answerable after the instance finishes, but it says a child *was created by* a parent, not that
> the correlation is still open. The `parent.*` ExtraProperties remain the source of truth for the
> relationship itself. Only `eq` / `ne` / `in` / `nin` are accepted, as for the status columns.
>
> It is deliberately **not** exposed as the bare name `type`. Instance columns and data attributes
> share one namespace on the legacy and GraphQL filter surfaces and are told apart purely by name —
> and the legacy surface strips the `attributes=` prefix *before* that check — so claiming `type`
> would silently retarget the existing filters of every domain whose schema has a business field
> called `type`, with no escape hatch. `type` keeps meaning your own attribute; the column is
> `instanceType`.
- **Instance-data attributes** — prefixed with `attributes.`, dotted for nesting:
  `attributes.amount`, `attributes.address.city`, `attributes.employment.department.name`.

## Operator reference

Every operator, with the fluent call and the wire JSON it emits (`ToFilterJson()`):

| Operator | Fluent call | Wire JSON |
| --- | --- | --- |
| Equal | `.Where("attributes.status", f => f.Eq("active"))` | `{"attributes":{"status":{"eq":"active"}}}` |
| Not equal | `.Where("attributes.status", f => f.Ne("cancelled"))` | `{"attributes":{"status":{"ne":"cancelled"}}}` |
| Greater than | `.Where("attributes.amount", f => f.Gt(1000))` | `{"attributes":{"amount":{"gt":1000}}}` |
| Greater/equal | `.Where("attributes.age", f => f.Ge(18))` | `{"attributes":{"age":{"ge":18}}}` |
| Less than | `.Where("attributes.amount", f => f.Lt(500))` | `{"attributes":{"amount":{"lt":500}}}` |
| Less/equal | `.Where("attributes.age", f => f.Le(65))` | `{"attributes":{"age":{"le":65}}}` |
| Contains (case-insensitive) | `.Where("attributes.name", f => f.Like("Ada"))` | `{"attributes":{"name":{"like":"Ada"}}}` |
| Starts with | `.Where("attributes.email", f => f.StartsWith("info"))` | `{"attributes":{"email":{"startswith":"info"}}}` |
| Ends with | `.Where("attributes.email", f => f.EndsWith("@x.com"))` | `{"attributes":{"email":{"endswith":"@x.com"}}}` |
| In list | `.Where("attributes.city", f => f.In("London", "Paris"))` | `{"attributes":{"city":{"in":["London","Paris"]}}}` |
| Not in list | `.Where("attributes.city", f => f.NotIn("Rome"))` | `{"attributes":{"city":{"nin":["Rome"]}}}` |
| Between (inclusive) | `.Where("attributes.age", f => f.Between(18, 65))` | `{"attributes":{"age":{"between":[18,65]}}}` |
| Is null / not null | `.Where("attributes.phone", f => f.IsNull(false))` | `{"attributes":{"phone":{"isNull":false}}}` |
| Array containment | `.Where("attributes.participants", f => f.Includes(new { userId }))` | `{"attributes":{"participants":{"includes":{"userId":"..."}}}}` |

Notes:

- `Includes` matches a JSON **array** whose elements contain the given partial object (PostgreSQL
  `jsonb @>`). **List queries only** — `First()/Last()` reject it at build time.
- Dates are passed as ISO-8601 strings for range operators: `f.Ge("2026-07-01T00:00:00Z")`.

## Composing conditions

**AND** — every top-level `Where` (and `OrGroup`/`Not`) composes as a logical AND:

```csharp
InstanceQuery.Create()
    .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
    .Where("currentState",          f => f.Eq("complete"))
// {"and":[{"attributes":{"scopeGroup":{"eq":"bireysel-3"}}},{"currentState":{"eq":"complete"}}]}
```

**OR** — `OrGroup` takes branches; at least one must match:

```csharp
.OrGroup(
    q => q.Where("currentState", f => f.Eq("complete")),
    q => q.Where("currentState", f => f.Eq("active-leave")))
// {"or":[{"currentState":{"eq":"complete"}},{"currentState":{"eq":"active-leave"}}]}
```

**NOT** — negates its inner group:

```csharp
.Not(q => q.Where("attributes.status", f => f.Eq("cancelled")))
// {"not":{"attributes":{"status":{"eq":"cancelled"}}}}
```

**Multiple operators on one field** — chain them; they AND together:

```csharp
.Where("attributes.age", f => f.Ge(18).Lt(65))
// {"and":[{"attributes":{"age":{"ge":18}}},{"attributes":{"age":{"lt":65}}}]}
```

Groups nest freely — `(city=London OR city=Paris) AND (dept=Research OR age>=30)`:

```csharp
InstanceQuery.Create()
    .OrGroup(
        q => q.Where("attributes.address.city", f => f.Eq("London")),
        q => q.Where("attributes.address.city", f => f.Eq("Paris")))
    .OrGroup(
        q => q.Where("attributes.employment.department.name", f => f.Eq("Research")),
        q => q.Where("attributes.age", f => f.Ge(30)))
```

## Ordering and First/Last

```csharp
.OrderBy("createdAt")                       // ascending
.OrderByDescending("attributes.startDateTime")
```

Default order is `createdAt` ascending. For single-resolve: `First()` takes the top row under the
effective ordering, `Last()` the bottom (implemented as reversed order + top row). Numeric
attributes order **numerically** (9 < 20 < 100), not as text — the engine orders jsonb natively.

## Type semantics (single-resolve engine)

The `First()/Last()` engine compares in the domain implied by the **operand's .NET type**:

| Operand | Comparison |
| --- | --- |
| `Eq(30)`, `In(1, 2, 3)` (real numbers/dates) | typed — `Eq(30)` matches a stored `30.0` |
| `Eq("123")`, `Eq("2026-04-27")` (strings, even numeric/date-looking) | **text** — safe for IDs and codes; never fails on mixed data |
| `Gt("2026-07-01T00:00:00Z")`, `Between("2026-01-01", "2026-12-31")` | range bounds **are** probed: date-like strings compare as timestamps, numeric strings as numbers |
| `Gt("M")` (non-numeric, non-date string) | text — alphabetical ranges work |

Rule of thumb: pass numbers as numbers and dates as ISO strings for ranges; pass identifiers as
strings.

## List queries: `Build()` and `InstanceQuerySpec`

`Build()` produces a spec whose serializers emit exactly the wire values the list endpoint
(`GET /api/v1/{domain}/workflows/{workflow}/instances`) consumes:

| Serializer | Query parameter | Notes |
| --- | --- | --- |
| `ToFilterJson()` | `filter` | null when no conditions (match-all is allowed for lists) |
| `ToSortJson()` | `sort` | `{"fields":[{"field":"createdAt","direction":"desc"}]}` |
| `ToGroupByJson()` | `groupBy` | `{"fields":[...],"aggregations":{...}}` — aggregations nest here |
| `ToAggregationsJson()` | `aggregations` | only when there is **no** groupBy |
| `ToQueryString(page, pageSize)` | all of the above | URL-encoded convenience |
| `ToFilterRequestJson()` | `filter` (single param) | plain filter JSON, or the request envelope embedding groupBy/aggregations — what `GetInstancesTask` uses |

**GroupBy + aggregations** — grouped reports return `GroupSummary` items instead of instances:

```csharp
var spec = InstanceQuery.Create()
    .Where("attributes.scopeGroup", f => f.Eq(scopeGroup))
    .GroupBy("attributes.limitKey")            // one or more fields
    .Sum("attributes.amount")                  // Count() / Sum / Avg / Min / Max
    .Count()
    .Build();
```

Rules: when grouping, aggregations are **nested inside groupBy** (the only combination the engine
honors). `GroupBy`/aggregations with `First()/Last()` throw at build time — they are list features.
Standalone aggregations (no groupBy) are currently not surfaced by the list endpoint.

## Using with `GetInstancesTask` (recommended)

`GetInstancesTask` (type `"15"`) is the platform-native way to query instances from a mapping — the
same trigger-task family as `StartTask`/`DirectTriggerTask`. Same-domain queries run **in-process**
(no HTTP/Dapr hop); cross-domain queries route automatically. Hand it the typed spec and stop — the
platform serializes to the wire format on both paths:

```csharp
public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
{
    var getInstancesTask = task as GetInstancesTask;
    getInstancesTask.SetDomain(context.Runtime.Domain); // the domain being served — also right when co-hosted
    getInstancesTask.SetFlow("rezervation");
    getInstancesTask.SetPageSize(100);

    getInstancesTask.SetFilterSpec(InstanceQuery.Create()
        .OrGroup(
            q => q.Where("currentState", f => f.Eq("active")),
            q => q.Where("currentState", f => f.Eq("in-meet")))
        .Where("status", f => f.Eq("A"))
        .Where("attributes.endDateTime", f => f.Gt(startDateIso))
        .OrderBy("attributes.startDateTime")
        .Build());

    return Task.FromResult(new ScriptResponse());
}
// OutputHandler: items are at context.Body.data.items (camelCase), same as the HTTP response shape.
```

Companion task component:

```jsonc
{ "attributes": { "type": "15", "config": {
    "domain": "my-domain", "flow": "rezervation", "pageSize": 100 } } }
```

Semantics: `SetFilterSpec` materializes the task's `Filter`/`Sort` strings from the spec (so local
and remote execution carry identical values). A later `SetFilter`/`SetSort` call overrides and
clears the spec. `FilterSpec` null ⇒ the legacy string path, byte-identical to before.

## Using with `DaprServiceTask` (explicit wiring)

When the task is a raw HTTP/Dapr call to the instances endpoint, the spec is a type-safe string
factory — you wire the values yourself:

```csharp
var spec = InstanceQuery.Create()
    .Where("attributes.absenceType", f => f.Eq(absenceType))
    .OrGroup(
        q => q.Where("currentState", f => f.Eq("complete")),
        q => q.Where("currentState", f => f.Eq("active-leave")))
    .Build();

serviceTask.SetQueryString($"pageSize=100&filter={Uri.EscapeDataString(spec.ToFilterJson())}"
    + $"&sort={Uri.EscapeDataString(spec.ToSortJson() ?? "")}");
// or simply: serviceTask.SetQueryString(spec.ToQueryString(page: 1, pageSize: 100));
```

Prefer `GetInstancesTask` for new code — it removes the URL, app-id, and encoding concerns from the
script and short-circuits same-domain calls in-process.

## Migrating from hand-written GraphQL strings

| Before (string concatenation) | After (fluent) |
| --- | --- |
| `"{\"attributes\":{\"type\":{\"eq\":\"" + t + "\"}}}"` | `.Where("attributes.type", f => f.Eq(t))` |
| `"{\"or\":[{\"currentState\":{\"eq\":\"a\"}},{\"currentState\":{\"eq\":\"b\"}}]}"` | `.OrGroup(q => q.Where("currentState", f => f.Eq("a")), q => q.Where("currentState", f => f.Eq("b")))` |
| `"{\"and\":[c1,c2]}"` via `string.Join` | consecutive `.Where(...)` calls |
| `JsonSerializer.Serialize(new { field = "x", direction = "asc" })` for sort | `.OrderBy("attributes.x")` |
| groupBy param `{"fields":[...],"aggregations":{"sum":...}}` | `.GroupBy(...).Sum(...)` |

The emitted JSON is structurally identical to the hand-written form (values serialized safely — no
escaping/injection concerns), so migrations are behavior-preserving. Migrated reference examples:
`GetAbsenceEntryFluentQueryMapping.csx` / `GetRezervationsFluentQueryMapping.csx` (DaprServiceTask)
and `GetAbsenceEntryFilterSpecMapping.csx` / `GetRezervationsFilterSpecMapping.csx`
(GetInstancesTask) in the morph-touch domain.

## Rules and gotchas

- `First()/Last()` require at least one condition; `Build()` allows an empty filter (match-all
  lists/reports).
- `GroupBy`/aggregations/`Includes` are list-query features — `First()/Last()` throw at build time.
- Aggregations must be paired with `GroupBy` to be returned by the list endpoint.
- Event `Selector` filters are automatically scoped to the target workflow (`flow` condition added
  by the runtime).
- Column names are whitelisted; a typo throws instead of silently matching nothing.
- The `id` column is a PostgreSQL `uuid`: `eq`, `ne`, `in` and `nin` bind each operand as a uuid
  and an operand that is not a valid GUID returns HTTP 400 (not a 500 `42883`). Comparison,
  `between` and text-match operators (`like`, `startswith`, `endswith`) are rejected on `id`.
- Filter scalar values are limited to 1000 characters (`string.Length` after JSON decoding).
  The limit applies to attributes and instance columns in GraphQL and legacy filters, including
  nested/logical conditions and aggregation envelopes. `in`, `nin`, and `between` check each
  operand separately, so a list can exceed 1000 characters overall. Oversized operands return
  HTTP 400 with a field-specific validation message before query execution; values are never
  truncated. The separate 5000-character total-filter limit still applies. Structured `includes`
  payloads retain their own size, depth, and property-count limits in `ValidateIncludesObject`.

## Quick reference

- **Filtering**: author instance queries with fluent `InstanceQuery` (default script import), never
  hand-concatenated GraphQL JSON. Terminals: `.First()/.Last()` → event `Selector` (single-resolve);
  `.Build()` → `InstanceQuerySpec` for `GetInstancesTask.SetFilterSpec(...)` (preferred; in-process when
  same-domain) or `spec.ToFilterJson()/ToSortJson()/ToQueryString()` for raw `DaprServiceTask` calls.
  Operators: Eq/Ne/Gt/Ge/Lt/Le/Like/StartsWith/EndsWith/In/NotIn/Between/IsNull/Includes + OrGroup/Not;
  GroupBy + Count/Sum/Avg/Min/Max (list-only, aggregations nest under groupBy). Full guide with operator
  table and migration examples: `docs/runtime/instance-filtering-and-queries.md`.

## References

- Fluent builder: `src/BBT.Workflow.Domain/QueryExtensions/Fluent/InstanceQuery.cs`
- Filter model: `src/BBT.Workflow.Domain/Filtering/InstanceFilter.cs`
- Spec + serializers: `src/BBT.Workflow.Domain/Filtering/InstanceQuerySpec.cs`, `GraphQlWireWriter.cs`
- Single-resolve SQL engine: `src/BBT.Workflow.Infrastructure/Instances/InstanceFilterSqlBuilder.cs`
- Task wiring: `src/BBT.Workflow.Application/Tasks/Mapping/TaskBindingMapper.cs`,
  `Definitions/Tasks/GetInstancesTask.cs`
- Executable examples (tests): `test/BBT.Workflow.Domain.Tests/Filtering/InstanceQuerySpecTests.cs`,
  `InstanceQuerySpecEndpointEquivalenceTests.cs`,
  `test/BBT.Workflow.Infrastructure.Tests/Domains/Instances/InstanceFilterQueryTests.cs`
