# Instance Filtering

## Overview

The BBT Workflow Engine supports instance filtering with two formats:

- **Legacy format** (`field=operator:value`) for simple query strings.
- **GraphQL-style JSON** for complex filters, grouping, and aggregations.

Filtering targets both **Instance table columns** and **JSON attributes** stored in `InstancesData.Data`, using PostgreSQL JSONB operators and GraphQL-aware SQL generation.

## Architecture

The filtering system is built on these main components:

```
┌─────────────────────┐    ┌──────────────────────────┐    ┌──────────────────────┐
│   API Controller    │    │  Application Layer       │    │ PostgreSQL Native    │
│                     │    │                          │    │                      │
│ Query Parameters    │───▶│  UnifiedFilterService     │───▶│  JSONB + CTE Query    │
│ filter / groupBy    │    │  GraphQLFilterParser      │    │  InstancesData join  │
└─────────────────────┘    └──────────────────────────┘    └──────────────────────┘
```

### Components

- **FilterFormatDetector**: Determines legacy vs GraphQL JSON format
- **GraphQLFilterParser / UnifiedFilterService**: Builds filter nodes and executes GraphQL filters
- **PostgreSqlJsonFilterService**: Executes legacy filters via JSONB + CTE
- **InstanceFieldDiscriminator**: Splits Instance column filters vs JSON attribute filters
- **InputValidator / ISchemaValidator**: Validates filter length, schema, and table names
- **InstanceRepository**: Executes filtered queries and group/aggregation responses

## Filter Syntax

### Legacy Format

```
filter=attributes={field}={operator}:{value}
filter={instanceField}={operator}:{value}
```

Multiple filters are passed as repeated `filter` parameters:

```
?filter=attributes=clientId=eq:122&filter=status=eq:Active
```

### GraphQL-Style JSON Format

```
filter={"attributes":{"clientId":{"eq":"122"}}}
```

Logical operators are supported:

```
filter={"and":[{"status":{"eq":"Active"}},{"attributes":{"amount":{"gt":500}}}]}
```

### GraphQL Filter Request (with groupBy/aggregations)

You can send a full request envelope in `filter`:

```
filter={"filter":{"status":{"eq":"Active"}},"groupBy":{"field":"attributes.status"},"aggregations":{"count":true}}
```

Alternatively, pass `groupBy` and `aggregations` as query parameters:

```
?filter={"status":{"eq":"Active"}}&groupBy={"field":"attributes.status"}&aggregations={"count":true}
```

**Format precedence:** If any `filter` value is GraphQL JSON, the request is handled as GraphQL format.

### URL Encoding

When using special characters, ensure proper URL encoding:

```
?filter=attributes%3Dname%3Dlike%3AJohn%20Doe    # John Doe
?filter=attributes%3Demail%3Dstartswith%3Atest%40 # test@
```

## Filterable Fields

### Instance Columns

Filters can target these Instance columns:

| Field | Notes |
|-------|-------|
| `key` | Instance key |
| `flow` | Workflow key |
| `status` | Accepts names or codes (`Active`/`A`, `Busy`/`B`, `Completed`/`C`, `Faulted`/`F`, `Passive`/`P`) |
| `currentState` / `state` | State name (state is an alias) |
| `createdAt` | Creation timestamp |
| `modifiedAt` | Modification timestamp |
| `completedAt` | Completion timestamp |
| `isTransient` | Boolean flag |

### JSON Attributes

Any JSON field in `InstancesData.Data` can be queried using:

- **Legacy**: `attributes=field=operator:value`
- **GraphQL**: `{"attributes":{"field":{"operator":value}}}`

GraphQL filters can also target Instance columns directly at the top level (e.g., `{"status":{"eq":"Active"}}`).

## Instance list ordering (orderBy)

Instance list results can be sorted via the `sort` or `orderBy` query parameter (same JSON format; `orderBy` wins if both are set).

### OrderBy JSON format

**Single field:**

```
?sort={"field":"createdAt","direction":"desc"}
?orderBy={"field":"status","direction":"asc"}
```

**Multiple fields:**

```
?sort={"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}
```

- `direction`: `"asc"` or `"desc"` (case-insensitive). Defaults to `"asc"` if omitted.

### Sortable fields

| Field | Notes |
|-------|-------|
| `createdAt` | Creation timestamp |
| `modifiedAt` | Modification timestamp |
| `completedAt` | Completion timestamp |
| `status` | Instance status |
| `key` | Instance key |
| `currentState` / `state` | Current state (state is alias) |
| `attributes.fieldName` | JSON attribute (path into `InstancesData.Data`); nested paths supported (e.g. `attributes.nested.path`) |

Instance columns are applied in the database; `attributes.*` ordering uses the latest instance data JSON and is subject to the same schema/security as filtering.

## Supported Operators

### Equality Operators

| Operator | Description | Example | SQL Equivalent |
|----------|-------------|---------|----------------|
| `eq` | Equal to | `filter=attributes=clientId=eq:122` | `Data @> '{"clientId":"122"}'` |
| `ne` | Not equal to | `filter=attributes=clientId=ne:122` | `NOT (Data @> '{"clientId":"122"}')` |

### Comparison Operators

| Operator | Description | Example | SQL Equivalent |
|----------|-------------|---------|----------------|
| `gt` | Greater than | `filter=attributes=testValue=gt:2` | `(Data ->> 'testValue')::numeric > 2` |
| `ge` | Greater than or equal | `filter=attributes=testValue=ge:3` | `(Data ->> 'testValue')::numeric >= 3` |
| `lt` | Less than | `filter=attributes=testValue=lt:4` | `(Data ->> 'testValue')::numeric < 4` |
| `le` | Less than or equal | `filter=attributes=testValue=le:3` | `(Data ->> 'testValue')::numeric <= 3` |
| `between` | Between two values | `filter=attributes=testValue=between:2,4` | `(Data ->> 'testValue')::numeric BETWEEN 2 AND 4` |

### String Operators

| Operator | Description | Example | SQL Equivalent |
|----------|-------------|---------|----------------|
| `like`/`match` | Contains substring | `filter=attributes=name=like:John` | `(Data ->> 'name') ILIKE '%John%'` |
| `startswith` | Starts with | `filter=attributes=email=startswith:test` | `(Data ->> 'email') ILIKE 'test%'` |
| `endswith` | Ends with | `filter=attributes=email=endswith:.com` | `(Data ->> 'email') ILIKE '%.com'` |

### Collection Operators

| Operator | Description | Example | SQL Equivalent |
|----------|-------------|---------|----------------|
| `in` | Value in list | `filter=attributes=clientId=in:122,177,83` | `(Data ->> 'clientId') IN ('122','177','83')` |
| `nin` | Value not in list | `filter=attributes=clientId=nin:122,177` | `(Data ->> 'clientId') NOT IN ('122','177')` |

**GraphQL-only operator:** `isNull` (e.g., `{"attributes":{"field":{"isNull":true}}}`)

## Data Type Support

The filtering system automatically handles different data types. GraphQL JSON filters preserve native JSON types, while legacy filters pass values as strings.

### String Values
```json
{"clientId": "177", "status": "active", "email": "user@example.com"}
```

**Examples:**
```
filter=attributes=clientId=eq:177
filter=attributes=status=ne:inactive
filter=attributes=email=endswith:@example.com
```

### Numeric Values
```json
{"testValue": 1, "amount": 99.99, "count": 42}
```

**Examples:**
```
filter=attributes=testValue=gt:2
filter=attributes=amount=between:50.00,150.00
filter=attributes=count=le:100
```

### Boolean Values
```json
{"isActive": true, "isVerified": false}
```

**Examples:**
```
filter=attributes=isActive=eq:true
filter=attributes=isVerified=ne:false
```

## Practical Examples

### Sample Data

Consider these workflow instances with the following `InstanceData.Data`:

```json
{"clientId": "177", "testValue": 1, "status": "pending", "amount": 99.50}
{"clientId": "110", "testValue": 2, "status": "active", "amount": 150.00}
{"clientId": "19", "testValue": 3, "status": "completed", "amount": 75.25}
{"clientId": "83", "testValue": 4, "status": "active", "amount": 200.00}
{"clientId": "122", "testValue": 4, "status": "pending", "amount": 125.75}
```

### Basic Filtering Examples

#### Find specific client
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=clientId=eq:122"
```
**Result:** Returns instance with clientId "122"

#### Find all except specific client
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=clientId=ne:122"
```
**Result:** Returns all instances except clientId "122"

#### Find instances with high test values
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=testValue=gt:2"
```
**Result:** Returns instances with testValue 3 and 4

### Advanced Filtering Examples

#### Multiple conditions (AND logic)
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=status=eq:active&filter=attributes=testValue=ge:2"
```
**Result:** Returns active instances with testValue >= 2

#### Range filtering
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=amount=between:100.00,200.00"
```
**Result:** Returns instances with amount between 100.00 and 200.00

#### List filtering
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=clientId=in:110,122,83"
```
**Result:** Returns instances for clients 110, 122, and 83

#### Text search
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=status=startswith:act"
```
**Result:** Returns instances with status starting with "act" (e.g., "active")

#### Exclusion filtering
```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter=attributes=status=nin:completed,cancelled"
```
**Result:** Returns instances that are NOT completed or cancelled

## Group By and Aggregations

Group and aggregate results using GraphQL JSON parameters:

```bash
curl -X GET "http://localhost:4201/api/v1.0/{domain}/workflows/my-workflow/instances?filter={\"status\":{\"eq\":\"Active\"}}&groupBy={\"field\":\"attributes.status\"}&aggregations={\"count\":true}"
```

When grouping, the response `items` list contains group summaries with `name`, `count`, and optional `sum`, `avg`, `min`, `max` fields.

## Performance Considerations

### PostgreSQL Optimization

The filtering system uses PostgreSQL native JSONB operators for optimal performance:

- **JSONB Containment (`@>`)**: Used for equality checks, leverages GIN indexes
- **JSON Field Extraction (`->>`)**: Used for comparisons and text operations
- **Common Table Expressions (CTE)**: Filters latest `InstancesData` and joins back to `Instances`
- **Instance Column Conditions**: Applied on the outer query using `InstanceColumnConditionBuilder`

### Recommended Indexes

For optimal performance, create these PostgreSQL indexes:

```sql
-- GIN index for JSONB containment operations
CREATE INDEX CONCURRENTLY idx_instances_data_gin 
ON "InstancesData" USING gin ("Data");

-- Specific field indexes for frequently queried fields
CREATE INDEX CONCURRENTLY idx_instances_data_client_id 
ON "InstancesData" (("Data" ->> 'clientId'));

CREATE INDEX CONCURRENTLY idx_instances_data_status 
ON "InstancesData" (("Data" ->> 'status'));
```

### Query Performance Tips

1. **Use specific operators**: `eq` with GIN indexes is faster than `like` operations
2. **Limit result sets**: Always use pagination for large datasets
3. **Index frequently filtered fields**: Create specific indexes for commonly queried JSON fields
4. **Avoid complex text searches**: Use dedicated search solutions for full-text search requirements

## API Integration

### Controller Usage

The filtering integrates seamlessly with the Instance Controller:

```csharp
[HttpGet("{domain}/workflows/{workflow}/instances")]
public async Task<IActionResult> GetInstanceListAsync(
    [FromRoute] string domain,
    [FromRoute] string workflow,
    [FromQuery] string[]? filter = null,
    [FromQuery] string[]? extension = null,
    [FromQuery] string? groupBy = null,
    [FromQuery] string? aggregations = null,
    [FromQuery] [Range(1, 1000)] int page = 1,
    [FromQuery] [Range(1, 100)] int pageSize = 10,
    [FromQuery] string? sort = null,
    CancellationToken cancellationToken = default)
```

**Notes:**
- `filter` accepts legacy or GraphQL JSON (array via repeated query parameters).
- `groupBy` and `aggregations` are parsed via `GraphQLFilterParser` and executed by `UnifiedFilterService`.
- When `filter` contains a full `GraphQLFilterRequest` envelope, `InstanceQueryAppService` parses it directly.

### Response Format

Instance list responses use `InstanceListWithGroupsResponse`:

```json
{
  "links": { "self": "...", "next": "..." },
  "items": [
    {
      "id": "123e4567-e89b-12d3-a456-426614174000",
      "flow": "my-workflow",
      "flowVersion": "1.0.0",
      "domain": "my-domain",
      "key": "unique-key",
      "attributes": {
        "clientId": "122",
        "testValue": 4,
        "status": "pending"
      },
      "etag": "\"abc123def456\""
    }
  ]
}
```

When `groupBy` is used, `items` contains group summaries (e.g., `name`, `count`, `sum`, `avg`, `min`, `max`) and links may be omitted.

## Error Handling

### Common Error Scenarios

#### Invalid Filter Format
```
HTTP 400 Bad Request
{
  "error": "Invalid filter format: clientId=invalid. Expected format: field=operator:value"
}
```

#### Unsupported Operator
```
HTTP 400 Bad Request
{
  "error": "Unsupported operator: xyz. Supported operators: eq, ne, gt, ge, lt, le, between, like, match, startswith, endswith, in, nin"
}
```

#### Invalid Data Type
```
HTTP 400 Bad Request
{
  "error": "Value 'abc' is not numeric for comparison operator 'gt'"
}
```

### Fallback Behavior

For legacy filters, `EfCoreInstanceRepository` falls back to `InstanceFilterSpecification` when filter parsing fails (e.g., invalid format or DB update exceptions). GraphQL filters are validated by `UnifiedFilterService` and `GraphQLFilterParser` before execution.

## Best Practices

### Filter Design

1. **Use appropriate operators**: Choose the most specific operator for your use case
2. **Combine filters efficiently**: Order filters from most selective to least selective
3. **Validate input**: Always validate filter values on the client side
4. **Handle edge cases**: Account for null values and empty strings

### Security Considerations

1. **Input validation**: `InputValidator` enforces max filter count, length, and field depth
2. **Schema/table validation**: `ISchemaValidator` whitelists schema and table names
3. **Field sanitization**: Field names are sanitized and validated before SQL construction
4. **Parameter binding**: All values are parameterized via `NpgsqlParameter`

### Development Guidelines

1. **Test with real data**: Always test filters with production-like datasets
2. **Monitor performance**: Use query execution plans to optimize slow queries
3. **Document field types**: Maintain documentation of JSON schema for filtered fields
4. **Version compatibility**: Ensure filter compatibility across workflow versions

## Troubleshooting

### Common Issues

#### No Results Returned
- Verify field names match exactly (case-sensitive)
- Check data types (string vs numeric comparison)
- Ensure proper URL encoding of special characters

#### Slow Query Performance
- Add appropriate indexes for frequently filtered fields
- Use pagination to limit result sets
- Consider using simpler operators for large datasets

#### Filter Parsing Errors
- Validate filter syntax: `field=operator:value`
- Check for supported operators
- Ensure proper escaping of special characters

### Debug Tips

1. **Enable SQL logging**: Set logging level to debug to see generated SQL
2. **Use query profiling**: Analyze PostgreSQL query execution plans
3. **Test incrementally**: Start with simple filters and add complexity gradually

## Migration Guide

### From EF Core to PostgreSQL Native

If migrating from EF Core filtering to PostgreSQL native filtering:

1. **Audit existing filters**: Identify commonly used filter patterns
2. **Create indexes**: Add appropriate JSONB indexes before migration
3. **Test performance**: Compare query performance before and after migration
4. **Update documentation**: Update API documentation with new capabilities

### Version Compatibility

The filtering system is backward compatible and automatically handles:

- Legacy filter formats
- Different JSON schema versions
- Mixed data types in the same field across instances

## Conclusion

The instance filtering system supports both legacy and GraphQL-style filters, with optional groupBy and aggregation support. It uses PostgreSQL JSONB operations for performance while keeping a safe validation layer for schemas and fields.

For additional questions or advanced use cases, refer to the source code in:
- `BBT.Workflow.Domain/QueryExtensions/PostgreSqlJsonFilterService.cs`
- `BBT.Workflow.Domain/QueryExtensions/GraphQL/UnifiedFilterService.cs`
- `BBT.Workflow.Domain/QueryExtensions/GraphQL/GraphQLFilterParser.cs`
- `BBT.Workflow.Domain/QueryExtensions/InstanceFieldDiscriminator.cs`
- `BBT.Workflow.Domain/QueryExtensions/InstanceColumnConditionBuilder.cs`
- `BBT.Workflow.Domain/Security/InputValidator.cs`
- `BBT.Workflow.Domain/Security/ISchemaValidator.cs`
- `BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs`
