# Instance Query Validation — Breaking Changes

**Target release:** 0.0.80 (unreleased — confirm against `vnext-meta/version-manifest.json` before publishing)
**Affects:** Orchestration instance list API, Monitoring instance/counter APIs, `GetInstancesTask` in workflow definitions
**Severity:** High — running workflow definitions and working client calls can break on deploy, without any code change on the consumer side.

## Purpose

Instance-query parsing used to fail **open**. A filter, sort, groupBy or aggregation the runtime
could not execute was silently dropped and the query ran anyway, answering HTTP 200. A caller who
asked to *narrow* a result set received *everything*.

This release makes those paths fail **closed**: anything the runtime cannot execute exactly as
authored is rejected up front. That is the correct behavior, but it converts a class of silently
broken requests into visible errors. Requests that were already wrong now say so — which means
**they stop working**.

Read this page before upgrading. Everything below describes a request or definition that used to
return 200 and now does not.

## Impact at a Glance

| Surface | Was | Now | Blast radius |
| --- | --- | --- | --- |
| `GET .../instances?sort=` | Unparseable sort silently ignored, fell back to `CreatedAt DESC` | HTTP 400 | Client request fails; no data effect |
| `GET .../instances?filter=` | Unsupported operator / truncated JSON silently dropped → every row returned | HTTP 400 | Client request fails; previously **over-broad results** |
| `GET /monitor/.../instances` | Same as above | HTTP 400 | Monitoring dashboard list screens |
| `GET /monitor/.../stats/instances` | Filter ignored → counters counted everything | HTTP 400 | Monitoring counters |
| `GetInstancesTask` (`sort` / `filter`) | Invalid value ignored, task ran unfiltered | Task returns `Result.Fail` | **Error boundary fires; instance can end up `Faulted`** |

The last row is the dangerous one. The others break a request. That one breaks a *running workflow*,
with no code change, purely by deploying the runtime.

---

## 1. `sort` / `orderBy` — HTTP API

Endpoints:

- `GET /api/v1/{domain}/workflows/{workflow}/instances` (orchestration, `sort` or `orderBy`; `orderBy` wins if both are given)
- `GET /api/v1/monitor/{domain}/workflows/{workflow}/instances` (monitoring, `sort`)

`GraphQLFilterParser.ParseOrderBy` now throws instead of returning `null` on malformed JSON, and
`OrderByRequest` / `OrderByField` reject unmapped members.

### 1.1 The `-field` shorthand was never implemented

```bash
# Was: 200 OK — sort ignored, results came back CreatedAt DESC
# Now: 400 — Validation:900012 / sort.invalidJson
curl -i "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances?sort=-createdAt"
curl -i "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances?sort=createdAt"
curl -i "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances?orderBy=-modifiedAt"
```

`sort` has always required JSON. A bare field name is not valid JSON, so it was never parsed — the
shorthand only ever appeared to work because the failure was swallowed.

**Fix:** use the JSON form.

```
sort={"field":"createdAt","direction":"desc"}
```

### 1.2 Invalid `direction`

```bash
# Was: 200 OK — anything that is not exactly "desc" was treated as ascending
# Now: 400 — sort.invalidDirection
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'sort={"field":"createdAt","direction":"descending"}'
```

Only `asc` and `desc` are accepted (case-insensitive). An absent `direction` still means ascending.

### 1.3 Misspelled property names

`OrderByRequest` and `OrderByField` now carry `JsonUnmappedMemberHandling.Disallow`.

```bash
# Was: 200 OK — unknown member ignored, Direction defaulted to ascending
# Now: 400 — sort.invalidJson
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'sort={"field":"createdAt","order":"desc"}'

curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'sort={"fld":"createdAt"}'
```

Property names remain **case-insensitive** (`{"Field":"CreatedAt"}` is fine). Only genuinely
unknown members are rejected.

### 1.4 Unknown sort field

```bash
# Was: 200 OK — unknown column skipped, ordering silently lost
# Now: 400 — sort.unknownField
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'sort={"field":"musteriNo"}'
```

A field that lives in instance data needs the `attributes.` prefix:
`sort={"field":"attributes.musteriNo"}`.

Valid instance columns (case-insensitive): `id`, `key`, `flow`, `currentState`, `state`, `status`,
`createdAt`, `modifiedAt`, `completedAt`, `isTransient`, `effectiveState`, `currentStateType`,
`currentStateSubType`, `effectiveStateType`, `effectiveStateSubType`, `stage`, `createdBy`,
`createdByBehalfOf`, `modifiedBy`, `modifiedByBehalfOf`.

### 1.5 Unsafe `attributes.` path

```bash
# Was: 200 OK — path skipped
# Now: 400 — sort.unsafePath
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'sort={"field":"attributes.musteri-no"}'
```

Each dot-separated segment must match `^[a-zA-Z0-9_]+$`.

### 1.6 Still valid

```
sort={"field":"createdAt","direction":"desc"}
sort={"field":"attributes.musteri.no","direction":"asc"}
sort={"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}
```

---

## 2. `filter` — HTTP API

### 2.1 Unsupported operators

Previously an operator the parser did not recognize was dropped, leaving the field with zero
conditions — which compiled to no `WHERE` clause at all.

```bash
# Was: 200 OK returning EVERY instance (the amount condition vanished)
# Now: 400 — Validation:900011 / filter.unknownOperator, with a correction hint
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'filter={"attributes":{"amount":{"gte":1000}}}'
```

Response carries the suggestion: `Operator 'gte' is not supported on field 'amount'. Did you mean 'ge'?`

Known corrections: `gte`→`ge`, `lte`→`le`, `neq`/`notequals`→`ne`, `equals`→`eq`, `contains`→`like`,
`notin`→`nin`, `null`→`isNull`.

> These are the **schema-authoring** spellings used by `x-filterOperators`. They are deliberately not
> accepted as wire aliases — `contains` is ambiguous (both `like` and `match` map to it), so aliasing
> would have to guess.

Supported wire operators: `between`, `endswith`, `eq`, `ge`, `gt`, `in`, `includes`, `isNull`, `le`,
`like`, `lt`, `match`, `ne`, `nin`, `startswith`.

### 2.2 Truncated or malformed filter

```bash
# Was: 200 OK — DetectFormat returned Empty, no filter applied, every row returned
# Now: 400 — filter.unrecognizedFormat
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'filter={"attributes":{"status":{"eq":"A"}}'
```

### 2.3 Misspelled envelope key

```bash
# Was: 200 OK — "fitler" dropped, aggregation ran over every instance
# Now: 400 — filter.unknownProperty
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'filter={"fitler":{"attributes":{"a":{"eq":1}}},"groupBy":{"fields":["status"]}}'
```

Recognized envelope properties: `filter`, `groupBy`, `aggregations`, `orderBy`.

### 2.4 Field with no operator, empty logical operator

```bash
# Now: 400 — filter.noOperator
--data-urlencode 'filter={"attributes":{"amount":{}}}'

# Now: 400 — filter.emptyLogicalOperator
--data-urlencode 'filter={"and":[]}'
```

`{}` and `{"attributes":{}}` remain **valid** — an empty filter legitimately means "no restriction".

### 2.5 Legacy format combined with aggregation

```bash
# Now: 400 — filter.legacyNotAggregatable
curl -i -G "http://localhost:4201/api/v1/kredi/workflows/basvuru/instances" \
  --data-urlencode 'filter=status=eq:A' \
  --data-urlencode 'groupBy={"fields":["currentState"]}'
```

The aggregation path feeds the filter straight into the GraphQL parser without converting it, so a
legacy `field=operator:value` string silently produced no condition there. Express the filter as
GraphQL-style JSON when grouping or aggregating.

### 2.6 Legacy format with an unsupported operator

`FilterFormatDetector.ConvertLegacyToGraphQL` used to swallow an unknown operator and return a
condition with nothing in it. It now throws, and the boundary validator rejects the request.

---

## 3. `GetInstancesTask` in workflow definitions

**This is the change most likely to cause an incident.**

The task's `filter` and `sort` are now validated before execution, on both the local and remote
(Dapr) paths. An invalid value returns `Result.Fail` instead of degrading into an unfiltered read.

```json
{
  "key": "aktif-basvurulari-getir",
  "type": "GetInstances",
  "config": {
    "triggerDomain": "kredi",
    "triggerFlow": "basvuru",
    "filter": "{\"status\":{\"eq\":\"Active\"}}",
    "sort": "-CreatedAt",
    "pageSize": 50
  }
}
```

`"sort": "-CreatedAt"` was documented as a supported shorthand. It never was — `ParseOrderBy`
returned `null` and the query fell back to `CreatedAt DESC`. It is now rejected, so the task fails.

### What failure means here

The task returns a failed `Result`, which enters the error boundary chain. Under an `Abort` rule
**the instance is set to `Faulted`**. Unlike an HTTP 400, this is not a client retrying a bad
request — it is a live workflow stopping, on definitions that were deployed and working before the
upgrade.

### Migration

| Was | Becomes |
| --- | --- |
| `"sort": "-CreatedAt"` | `"sort": "{\"field\":\"createdAt\",\"direction\":\"desc\"}"` |
| `"sort": "CreatedAt"` | `"sort": "{\"field\":\"createdAt\",\"direction\":\"asc\"}"` |
| `"sort": "-Status,CreatedAt"` | `"sort": "{\"fields\":[{\"field\":\"status\",\"direction\":\"desc\"},{\"field\":\"createdAt\",\"direction\":\"asc\"}]}"` |

Definitions built with the fluent `InstanceQuery` API and `SetFilterSpec(...)` are unaffected — the
spec serializes to the JSON wire form already.

---

## 4. New error codes

| Code | Constant | Raised for |
| --- | --- | --- |
| `Validation:900011` | `WorkflowErrorCodes.InstanceFilterInvalid` | Filter grammar, unknown operator, unrecognized format |
| `Validation:900012` | `WorkflowErrorCodes.InstanceSortInvalid` | Sort/orderBy |
| `Validation:900013` | `WorkflowErrorCodes.InstanceGroupByInvalid` | GroupBy |
| `Validation:900014` | `WorkflowErrorCodes.InstanceAggregationInvalid` | Aggregations |

`Validation:900010` (`SchemaFilterValidation`) is unchanged and still means a **master-schema policy**
rejection — the field is not filterable/sortable, or the operator is not in `x-filterOperators`.

All rejection reasons are returned together (capped at 20) so a caller can fix them in one round
trip. The machine-readable sub-code is on each error's `code` (`filter.unknownOperator`,
`sort.invalidDirection`, …); the top-level code above reflects the first error's parameter family.

## 5. Exception types (extension and task authors)

Two distinct fail-closed exceptions, both mapping to HTTP 400:

| Exception | Meaning | Logged as |
| --- | --- | --- |
| `FilterCompilationException` | The boundary validator and the SQL builder disagree about what is executable — a runtime defect | `Error` (EventId 20441, `InstanceFilterCompilationFailed`) |
| `SchemaFilterValidationException` | The master schema rejected a well-formed request — routine | Not logged as drift |

Do not collapse these into one type. `InstanceQueryAppService` catches only the first; conflating
them would fire the drift alarm on every routine policy rejection and make the signal worthless.

## 6. New log events

| EventId | Level | Method | Meaning |
| --- | --- | --- | --- |
| 20440 | Warning | `InstanceQueryParameterRejected` | A query parameter was rejected at the boundary; the query never ran |
| 20441 | Error | `InstanceFilterCompilationFailed` | Passed validation but failed to compile — validator/builder drift |
| 20442 | Warning | `InstanceTaskFilterRejected` | A workflow task's authored filter was rejected (definition defect) |

Alert on 20441. It should never fire; if it does, the two rule sets have diverged.

## 7. Pre-upgrade checklist

**Scan workflow definitions** — every hit is a task that will fail after the upgrade:

```bash
# sort values that are not JSON (do not start with '{')
grep -rn '"sort"[[:space:]]*:[[:space:]]*"[^{]' --include="*.json" . | grep -v node_modules

# direction values outside asc/desc
grep -rn '"direction"' --include="*.json" . \
  | grep -viE '"direction"[[:space:]]*:[[:space:]]*"(asc|desc)"'
```

**Scan client traffic** — from ingress/access logs, before deploying:

```bash
grep -oE '[?&](sort|orderBy)=[^& ]*' access.log | sort | uniq -c | sort -rn
```

Any value not starting with `{` (URL-encoded `%7B`) will return 400 after the upgrade.

**Then:**

- [ ] Migrate every `GetInstancesTask.sort` hit to the JSON form and redeploy the domain package **before** the runtime upgrade.
- [ ] Fix client `sort`/`orderBy` values found in access logs.
- [ ] Audit filters using `gte`/`lte`/`neq`/`contains` — note that these were returning **over-broad** result sets, so any report or screen built on them may have been showing wrong numbers.
- [ ] Add an alert on log EventId 20441.
- [ ] Add `deprecations.json` / `migrations.json` entries in `vnext-meta` for the `-field` sort shorthand.

## 8. What did not change

- Empty filters (`{}`, `{"attributes":{}}`) and absent parameters still mean "no restriction".
- Legacy `field=operator:value` filters still work on the plain list path.
- Property names stay case-insensitive; instance column names stay case-insensitive.
- Schema-driven policy (`x-filterable`, `x-sortable`, `x-filterOperators`) is enforced where it
  always was, still raising `Validation:900010`. The boundary validator deliberately does not
  duplicate it — one rule, one place.
- `transition.roles` enforcement, pagination shape, and the response envelope are untouched.

## Related

- [API and Service Contracts](api-and-service-contracts.md)
- [Instance Filtering and Queries](../runtime/instance-filtering-and-queries.md) — fluent `InstanceQuery`, operator reference, migration from hand-written GraphQL filters
- [JSON Validation](json-validation.md)
