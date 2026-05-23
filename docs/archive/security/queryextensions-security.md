# QueryExtensions Security

## Overview

The QueryExtensions filter pipeline defends against SQL/JSON injection and ReDoS by validating
inputs, sanitizing field paths, enforcing schema/table whitelists, and applying regex timeouts.
Security is enforced at the filter services that build SQL, not in controllers.

## Core Protections

- **Schema validation** via `ISchemaValidator` (async with cache or sync format-only fallback).
- **Table whitelist** for instance-related tables only.
- **Field sanitization** for JSON paths (length, depth, allowed characters, must start with letter).
- **Input size limits** for filters and values (`InputValidator`).
- **JSON containment building** uses `JsonSerializer` to avoid injection.
- **Regex timeouts** in operator parsing and format detection (100ms).

## Enforcement Points

### Legacy filter format

`PostgreSqlJsonFilterService.ApplyJsonFilters`:

- Validates filters via `InputValidator`.
- Validates schema/table via `ISchemaValidator` or `SyncSchemaValidator`.
- Sanitizes JSON field names before SQL composition.
- Uses parameterized `FromSqlRaw` with `NpgsqlParameter`.

### GraphQL JSON filters

`GraphQLJsonFilterService.ApplyGraphQLFilter`:

- Validates JSON length.
- Builds JSON/Instance where clauses with parameters.
- Applies schema/table validation the same way as legacy filters.

### Aggregations and group-by

`GraphQLAggregationService.ExecuteAggregationAsync` / `ExecuteGroupByAsync`:

- Validates schema with `ISchemaValidator`.
- Builds JSON accessors only after field sanitization.

### Format detection and parsing

- `FilterFormatDetector` uses a regex with timeout and rejects ambiguous input.
- `FilterOperatorParser` uses regex with timeout and returns safe operator/value tokens.

## Schema Validation Behavior

`SchemaValidator` (infrastructure) enforces:

- Format: lowercase + underscores, max 63 characters.
- System schemas always allowed (`public`, `sys_flows`, `sys_extensions`, `sys_functions`, `sys_schemas`, `sys_tasks`, `sys_views`).
- Active flow schemas loaded from `sys_flows.Instances` (`Status = 'A'`).
- Cache TTL: 5 minutes in `IDistributedCache`.

`SyncSchemaValidator` (domain) is a safe fallback that validates format only and skips DB/cache.

## Input Limits

`InputValidator`:

- `MaxFilterLength` (5000)
- `MaxFiltersCount` (50)
- `MaxFieldNameLength` (100)
- `MaxValueLength` (1000)
- `MaxFieldDepth` (10)

These throw `ArgumentException` before any SQL generation.

## Cache Invalidation

`SchemaCacheInvalidationService` (application) calls `ISchemaValidator.InvalidateCacheAsync` after:

- Flow created
- Flow deleted
- Flow status changed to/from Active

Failures are logged and do not block the flow operation.

## Error Handling and Logging

Filter parsing failures are logged as warnings and skipped so other filters still apply. Schema
validation failures throw `SecurityException` and should be handled by the API layer.

## Implementation References

- `src/BBT.Workflow.Domain/Security/ISchemaValidator.cs`
- `src/BBT.Workflow.Domain/Security/SyncSchemaValidator.cs`
- `src/BBT.Workflow.Infrastructure/Security/SchemaValidator.cs`
- `src/BBT.Workflow.Domain/Security/InputValidator.cs`
- `src/BBT.Workflow.Domain/QueryExtensions/PostgreSqlJsonFilterService.cs`
- `src/BBT.Workflow.Domain/QueryExtensions/GraphQL/GraphQLJsonFilterService.cs`
- `src/BBT.Workflow.Domain/QueryExtensions/GraphQL/GraphQLAggregationService.cs`
- `src/BBT.Workflow.Domain/QueryExtensions/FilterOperatorParser.cs`
- `src/BBT.Workflow.Domain/QueryExtensions/GraphQL/FilterFormatDetector.cs`
- `src/BBT.Workflow.Application/Security/SchemaCacheInvalidationService.cs`

