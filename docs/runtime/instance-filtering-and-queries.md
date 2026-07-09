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

- **Instance columns** — bare names, whitelisted: `id`, `key`, `flow`, `status`, `state` /
  `currentState`, `effectiveState`, `effectiveStateType`, `effectiveStateSubType`, `stage`,
  `createdAt`, `modifiedAt`, `completedAt`. Unknown column names throw at build/SQL time.
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
    getInstancesTask.SetDomain(GetConfigValue("APP_DOMAIN"));
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
