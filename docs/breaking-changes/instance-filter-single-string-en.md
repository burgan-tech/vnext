# Breaking Change: Instance Filter — Single String (No Array)

## Summary

Instance list and Data function APIs, as well as the **GetInstances** task and related bindings, now accept **a single filter string** instead of an array of filters. Multiple separate filter expressions are no longer supported in one request; complex conditions must be expressed in a single filter (e.g. one GraphQL-style JSON with `and`/`or`).

## Affected Areas

### 1. HTTP API

| Endpoint / usage | Before | After |
|------------------|--------|--------|
| `GET .../instances` query param | `filter` as `string[]` (e.g. `?filter=x&filter=y`) | `filter` as single `string` (e.g. `?filter=<one value>`) |
| Data function `GET .../functions/data` | `filter` in query/body as array | `filter` as single string |

- **Controller:** `InstanceController.GetInstanceListAsync` — parameter changed from `[FromQuery] string[]? filter` to `[FromQuery] string? filter`.
- **Query model:** `FunctionListQueryParameters.Filter` — type changed from `string[]?` to `string?`.

### 2. Application / DTOs

- **GetInstanceListInput.Filter** — type changed from `string[]?` to `string?`. Callers must pass one filter string or `null`.

### 3. GetInstances Task (workflow definition)

- **GetInstancesTask.Filter** — property type changed from `string[]?` to `string?`.
- **SetFilter** — method signature changed from `SetFilter(string[]? filter)` to `SetFilter(string? filter)`.
- **Task JSON config:**  
  - **Before:** `"filter": ["expr1", "expr2"]`  
  - **After:** `"filter": "expr"` (single string) or, for backward compatibility, a **single-element array** is still supported and is treated as that one filter string.  
  - Multiple elements in an array are no longer supported; only the first element was previously used in practice; now the API is explicitly a single string.

### 4. GetInstances Binding (execution)

- **GetInstancesBinding.Filter** — type changed from `string[]?` to `string?`. Remote invokers and gateways build the query string with one `filter` parameter.

### 5. Domain / infrastructure

- **IInstanceRepository** — `GetPagedResultsAsync` and `GetPagedResultsWithGroupsAsync` (the overload that takes filters) now take `string? filter` instead of `string[]? filters`.
- **FilterFormatDetector** — `DetectFormat(string[]?)` overload removed; `CombineFilters` and `ConvertLegacyToGraphQL` now take `string?` (single filter).
- **UnifiedFilterService.ApplyFilters** — parameter changed from `string[]? filters` to `string? filter`.
- **PostgreSqlJsonFilterService.ApplyJsonFilters** — parameter changed from `string[] filters` to `string? filter`.
- **FilterSpecification&lt;T&gt;** — constructor changed from `(string[]? filters, ...)` to `(string? filter, ...)`.
- **InstanceFilterSpecification** — constructor changed from `(string[]? filters)` to `(string? filter)`.
- **InputValidator** — `ValidateFilters(string? filter)` overload added; the existing `ValidateFilters(string[]? filters)` remains for internal use.

## Migration

### Clients (HTTP)

- If you sent multiple `filter` query parameters, combine the intended logic into **one** filter string (e.g. one GraphQL-style JSON with `and`/`or` nodes, or one legacy-style string if your backend supports it).
- Example: instead of `?filter={"a":"eq:1"}&filter={"b":"eq:2"}`, send one filter such as  
  `?filter={"and":[{"attributes":{"a":{"eq":"1"}}},{"attributes":{"b":{"eq":"2"}}}]}` (syntax may vary by your API contract).

### Workflow / task definitions

- Change task config from array to single string:
  - **Before:** `"filter": ["status=Active", "flow=my-flow"]`
  - **After:** `"filter": "status=Active"` (or a single combined expression).  
  For multiple conditions, use one GraphQL-style JSON string that expresses the full condition (e.g. with `and`/`or`).
- Update any code that calls **SetFilter** to pass a `string?` instead of `string[]?`.

### Code using GetInstanceListInput / repository / filter services

- Pass a single `string?` for the filter (or `null`) instead of `string[]?`.
- Any logic that built or iterated over a filter array should be updated to build or pass one filter string.

## Backward compatibility (task config only)

- In **GetInstancesTask** JSON configuration, if `"filter"` is still an **array**, it is interpreted as follows for compatibility:
  - One element: that element is used as the single filter string.
  - Multiple elements: only the **first** element is used as the filter string.  
  Prefer migrating to a single `"filter": "..."` string or one combined expression.

## Version / date

This change is effective as of the commit/version that introduces the single-string filter across the listed APIs and types. Check the repository history or release notes for the exact version.
