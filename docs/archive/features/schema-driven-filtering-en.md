# Schema-Driven Filtering

## Overview

A mechanism that uses custom JSON Schema extensions (`x-filterOperators`, `x-sortable`, `x-displayFormat`) defined in the workflow's master schema to control which fields are filterable/sortable, which operators are allowed per field, and how comparison types (numeric, date, text) are determined from the field's schema type.

## Schema Contract

Each property in the workflow's master schema can define the following extensions:

| Extension | Type | Required | Description |
|---|---|---|---|
| `x-filterOperators` | `string[]` | No | Allowed filter operators. If empty or missing, the field is not filterable |
| `x-sortable` | `boolean` | No | If `true`, the field supports sorting. If missing, the field is not sortable |
| `x-displayFormat` | `string` | No | UI-oriented display format hint (e.g., `yyyy-MM-dd'T'HH:mm:ssXXX`) |

### Rules

1. A field is filterable only when `x-filterOperators` is present and non-empty. If missing or empty, the field cannot be filtered
2. A field is sortable only when `x-sortable: true`. If not defined, the field cannot be sorted
3. When a non-filterable field is queried or a disallowed operator is used, a `SchemaFilterValidationException` is thrown
4. For the GraphQL-only `includes` operator on array-valued JSON paths, the field must declare `includes` in `x-filterOperators` (same as other operators). Payload size and nesting are bounded by `InputValidator` limits

### Type-Operator Relationship

| Schema `type` | Operator category | SQL behavior |
|---|---|---|
| `number` / `integer` | gt, lt, gte, lte, between | `accessor::numeric {op} @param` |
| `string` + gt/lt/gte/lte/between | date comparison | `accessor::timestamptz {op} @param` |
| `string` + eq/contains/startsWith/endsWith | text comparison | `accessor ILIKE @param` |
| `boolean` | eq, neq | equality |
| `array` (JSON array in instance data) | `includes` | `Data @> @param` with a pattern where the leaf path is a **one-element array** containing the partial object from the filter |

## Example Schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "properties": {
    "advisor": {
      "type": "string",
      "x-filterOperators": ["eq", "neq", "contains", "startsWith", "endsWith", "in", "nin"],
      "x-sortable": true
    },
    "advisorType": {
      "type": "string"
    },
    "startDateTime": {
      "type": "string",
      "format": "date-time",
      "x-filterOperators": ["eq", "gt", "gte", "lt", "lte", "between"],
      "x-sortable": true,
      "x-displayFormat": "yyyy-MM-dd'T'HH:mm:ssXXX"
    },
    "endDateTime": {
      "type": "string",
      "format": "date-time",
      "x-filterOperators": ["eq", "gt", "gte", "lt", "lte", "between"],
      "x-sortable": true,
      "x-displayFormat": "yyyy-MM-dd'T'HH:mm:ssXXX"
    },
    "amount": {
      "type": "number",
      "x-filterOperators": ["eq", "gt", "gte", "lt", "lte", "between"],
      "x-sortable": true
    },
    "customerName": {
      "type": "string",
      "x-filterOperators": ["eq", "contains", "startsWith", "endsWith"],
      "x-sortable": true
    },
    "isActive": {
      "type": "boolean",
      "x-filterOperators": ["eq", "neq"]
    },
    "members": {
      "type": "array",
      "x-filterOperators": ["includes"]
    }
  }
}
```

According to this schema:

- `advisor` -- text filters and sorting are supported
- `advisorType` -- no `x-filterOperators`, **not filterable and not sortable**
- `startDateTime` / `endDateTime` -- date comparison (`gt`, `lt`, `between`) and sorting are supported
- `amount` -- numeric comparison is supported
- `customerName` -- text search is supported, range operators (`gt`, `lt`) are **not allowed**
- `isActive` -- only equality check, range and text searches are **not allowed**
- `members` -- only the `includes` operator is allowed on this path (array containment)

## Operator Naming

There is a mapping between schema operator names and filter API operator names:

| Schema operator | Filter API operator | Description |
|---|---|---|
| `eq` | `eq` | Equal |
| `neq` | `ne` | Not equal |
| `gt` | `gt` | Greater than |
| `gte` | `ge` | Greater than or equal |
| `lt` | `lt` | Less than |
| `lte` | `le` | Less than or equal |
| `between` | `between` | Range |
| `contains` | `like` / `match` | Contains (ILIKE) |
| `startsWith` | `startswith` | Starts with |
| `endsWith` | `endswith` | Ends with |
| `in` | `in` | In list |
| `nin` | `nin` | Not in list |
| `isNull` | `isnull` | Null check |
| `includes` | `includes` | Array containment (GraphQL only): at least one element under the field path matches the partial JSON object |

Sample request JSON for `includes` is kept in [Instance filtering](./instance-filtering.md) (Array containment).

## Example Filter Queries

### Date comparison (string + gt)

Records starting after a specific date:

```
GET /api/v1/morph-touch/workflows/absence-entry/instances?page=1&pageSize=10&filter=...
```

Filter JSON:
```json
{
  "attributes": {
    "startDateTime": {
      "gt": "2026-04-18T23:59:59+03:00"
    }
  }
}
```

Generated SQL:
```sql
SELECT s.*
FROM "absence_entry"."Instances" s
WHERE s."Id" IN (
    SELECT "InstanceId"
    FROM "absence_entry"."InstancesData"
    WHERE "IsLatest" = true
      AND ("Data" ->> 'startDateTime')::timestamptz > @p0
)
ORDER BY s."CreatedAt" DESC
```

### Date range (between)

Records within a date range:

```json
{
  "attributes": {
    "startDateTime": {
      "between": ["2026-04-01T00:00:00Z", "2026-04-30T23:59:59Z"]
    }
  }
}
```

Generated SQL:
```sql
... AND ("Data" ->> 'startDateTime')::timestamptz BETWEEN @p0 AND @p1
```

### Text search (contains + eq)

Records for a specific advisor:

```json
{
  "attributes": {
    "advisor": {
      "eq": "touch.portfolio-manager.pm-001"
    }
  }
}
```

Advisor name with content search:

```json
{
  "attributes": {
    "advisor": {
      "like": "pm-001"
    }
  }
}
```

### Numeric comparison

Records where amount is greater than 1000:

```json
{
  "attributes": {
    "amount": {
      "gt": 1000
    }
  }
}
```

Generated SQL:
```sql
... AND ("Data" ->> 'amount')::numeric > @p0
```

### Multiple conditions (AND)

Date filter + advisor filter combined:

```json
{
  "and": [
    {
      "attributes": {
        "startDateTime": {
          "gt": "2026-04-17T23:59:59Z"
        }
      }
    },
    {
      "attributes": {
        "advisor": {
          "eq": "touch.portfolio-manager.pm-001"
        }
      }
    }
  ]
}
```

### Filter + GroupBy together

Group records starting after a specific date by advisor:

```
GET /api/v1/.../instances?filter=...&groupBy=...
```

Filter:
```json
{
  "attributes": {
    "startDateTime": {
      "gt": "2026-04-17T23:59:59Z"
    }
  }
}
```

GroupBy:
```json
{
  "field": "advisor",
  "aggregations": {
    "count": true
  }
}
```

Expected response:
```json
{
  "groups": [
    {
      "keys": { "advisor": "touch.portfolio-manager.pm-001" },
      "aggregations": { "count": 45 }
    },
    {
      "keys": { "advisor": "touch.portfolio-manager.pm-002" },
      "aggregations": { "count": 32 }
    }
  ]
}
```

### Sorting (orderBy)

Sort by `startDateTime` descending:

```
GET /api/v1/.../instances?sort={"field":"attributes.startDateTime","direction":"desc"}
```

Sort requests for fields without `x-sortable: true` in the schema are silently ignored.

## Error Cases

### Non-filterable field queried

When filtering by `advisorType` (no `x-filterOperators` in schema):

```json
{
  "attributes": {
    "advisorType": {
      "eq": "portfolio-manager"
    }
  }
}
```

Response:
```json
{
  "error": {
    "code": "Validation:900010",
    "message": "Field 'advisorType' is not filterable."
  }
}
```

### Disallowed operator used

When using `gt` operator on `isActive` field (schema only allows `eq` and `neq`):

```json
{
  "attributes": {
    "isActive": {
      "gt": true
    }
  }
}
```

Response:
```json
{
  "error": {
    "code": "Validation:900010",
    "message": "Operator 'gt' is not allowed for field 'isActive'."
  }
}
```

## Binding Schema to Workflow

The `schema` reference must be added to the workflow JSON:

```json
{
  "key": "absence-entry",
  "flow": "sys-flows",
  "domain": "morph-touch",
  "version": "1.0.0",
  "attributes": {
    "type": "S",
    "schema": {
      "key": "absence-entry",
      "domain": "morph-touch",
      "version": "1.0.0"
    },
    "labels": [...]
  }
}
```

If no schema is bound (`schema: null`), all fields are filterable with current behavior (backward compatible).

## Fallback Behavior

| Scenario | Behavior |
|---|---|
| No schema defined on workflow | All fields filterable, all operators allowed, `gt`/`lt` numeric only (current behavior) |
| Orchestration `Workflow:InstanceFiltering:EnforceMasterSchemaFiltering` = `false` | Even when a schema is bound to the workflow, filtering behaves as schema-less mode: all fields are treated as filterable, range comparisons use `::numeric`, and attribute `orderBy` does not enforce `x-sortable` (configured in Orchestration `appsettings`) |
| Schema defined but field not in schema | Field is not filterable (`SchemaFilterValidationException`) |
| Schema defined, field exists, `x-filterOperators` empty/missing | Field is not filterable |
| Schema defined, field exists, operator not in list | Operator not allowed (`SchemaFilterValidationException`) |

## Architecture

```
InstanceQueryAppService
    |
    |-- 1. componentCacheStore.GetFlowAsync(domain, workflow)
    |-- 2. componentCacheStore.GetSchemaAsync(workflow.Schema)
    |-- 3. SchemaFilterMetadataResolver.Resolve(schema.Schema) --> SchemaFilterContext
    |
    |-- 4. if Workflow:InstanceFiltering:EnforceMasterSchemaFiltering is false -> schemaContext = null
    |
    |-- 5a. parsedRequest.SchemaContext = schemaContext  (GraphQLFilterRequest path)
    |-- 5b. schemaContext parameter to repository         (string filter path)
    |
    v
IInstanceRepository / EfCoreInstanceRepository
    |
    v
UnifiedFilterService --> GraphQLJsonFilterService
    |
    |-- BuildFieldConditions: IsFieldFilterable + IsOperatorAllowed checks
    |-- BuildComparisonCondition: type-based numeric/datetime/text SQL cast
    |-- BuildOrderByClause: IsFieldSortable check
```

### Related Files

| File | Role |
|---|---|
| `Domain/Definitions/Schemas/SchemaFieldMetadata.cs` | Single field's filter/sort metadata model |
| `Domain/Definitions/Schemas/SchemaFilterContext.cs` | Field metadata map + validation methods |
| `Domain/Definitions/Schemas/SchemaFilterMetadataResolver.cs` | JSON Schema parser (reads x-extensions) |
| `Domain/QueryExtensions/GraphQL/GraphQLJsonFilterService.cs` | SQL generation, type-based comparison, validation |
| `Domain/QueryExtensions/GraphQL/GraphQLFilterModels.cs` | `GraphQLFilterRequest.SchemaContext` property |
| `Domain/QueryExtensions/GraphQL/UnifiedFilterService.cs` | Filter routing, schema context threading |
| `Domain/ExceptionHandling/SchemaFilterValidationException.cs` | Validation error exception |
| `Domain/WorkflowErrorCodes.cs` | `SchemaFilterValidation = "Validation:900010"` |
| `Application/Instances/InstanceQueryAppService.cs` | Schema loading and context creation |
| `Application/Instances/InstanceFilteringOptions.cs` | `Workflow:InstanceFiltering:EnforceMasterSchemaFiltering` (Orchestration configuration) |
| `Infrastructure/Instances/EfCoreInstanceRepository.cs` | Schema context threading to SQL pipeline |

## Performance

### SQL Structural Optimization

Filtered queries use subquery IN instead of CTE + JOIN. This ensures PostgreSQL optimizer selects Semi Join instead of Nested Loop at low LIMIT values:

```sql
-- Optimized: Semi Join (162ms @ LIMIT 5)
SELECT s.*
FROM "schema"."Instances" s
WHERE s."Id" IN (
    SELECT "InstanceId"
    FROM "schema"."InstancesData"
    WHERE "IsLatest" = true AND (json filter conditions)
)
ORDER BY s."CreatedAt" DESC

-- Previous: CTE + JOIN (22s @ LIMIT 5, Nested Loop problem)
WITH FilteredData AS (...)
SELECT s.* FROM "Instances" s JOIN FilteredData d ON s."Id" = d."InstanceId"
```

### Recommended Indexes

```sql
-- GIN index: for eq, ne, in operators using JSONB containment (@>)
CREATE INDEX IX_InstancesData_Data_GIN 
ON "{schema}"."InstancesData" 
USING GIN ("Data" jsonb_path_ops)
WHERE "IsLatest" = true;

-- Advisor text index: for eq, contains, startsWith
CREATE INDEX IX_InstancesData_Advisor 
ON "{schema}"."InstancesData" 
(("Data" ->> 'advisor'))
WHERE "IsLatest" = true;

-- Composite index for subquery performance
CREATE INDEX IX_InstancesData_IsLatest_InstanceId_EnteredAt 
ON "{schema}"."InstancesData" 
("InstanceId", "EnteredAt" DESC)
WHERE "IsLatest" = true;

-- Instances default ORDER BY
CREATE INDEX ix_instances_createdat 
ON "{schema}"."Instances" 
("CreatedAt" DESC);
```

### PostgreSQL Optimizer Settings (for SSD)

```sql
ALTER DATABASE "dbname" SET random_page_cost = 1.1;
ALTER DATABASE "dbname" SET work_mem = '16MB';
```
