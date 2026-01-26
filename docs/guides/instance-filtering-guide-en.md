# Instance Filtering Guide

## Overview

Instance filtering supports two formats and works on both instance columns and JSON data fields.
This guide focuses on **verified routes**, **query parameters**, and **safe examples** that match
the current controllers.

## Supported Routes

### Instance list

```
GET /api/v{version}/{domain}/workflows/{workflow}/instances
```

Query parameters:

- `filter` (repeatable, string[])
- `page` (1–1000)
- `pageSize` (1–100)
- `sort` (e.g., `CreatedAt` or `-CreatedAt`)
- `extension` (repeatable)
- `groupBy` (string, optional)
- `aggregations` (string, optional)

### Data function list

```
GET /api/v{version}/{domain}/workflows/{workflow}/functions/data
```

Query parameters:

- `filter` (repeatable, string[])
- `page`, `pageSize`, `sort`

## Filter Formats

### Legacy format (query string)

```
field=operator:value
```

Use `attributes=` for JSON fields:

```
attributes=customerId=eq:CUST-123
```

### GraphQL JSON format (recommended)

Provide a JSON object inside `filter` (URL-encode it):

```
filter={"attributes":{"customerId":{"eq":"CUST-123"}}}
```

Logical operators:

```
{"and":[{...},{...}]}
{"or":[{...},{...}]}
{"not":{...}}
```

## Filterable Fields

### Instance columns

`key`, `flow`, `currentState` (alias: `state`), `status`, `createdAt`, `modifiedAt`, `completedAt`,
`isTransient`

### JSON data fields

Use `attributes` for JSON fields:

```
{"attributes":{"amount":{"gt":500}}}
```

## Supported Operators

`eq`, `ne`, `gt`, `ge`, `lt`, `le`, `between`, `like`, `match`, `startswith`, `endswith`, `in`, `nin`, `isNull`

Notes:

- `match` is an alias of `like`.
- `isNull` uses camel case.

## Status Values

Both codes and names are accepted:

`A`/`Active`, `B`/`Busy`, `C`/`Completed`, `F`/`Faulted`, `P`/`Passive`

## Examples

### 1) Instance list with JSON filter

```
GET /api/v1/banking/workflows/payment-workflow/instances?filter={"attributes":{"customerId":{"eq":"CUST-123"}}}
```

### 2) Instance list with legacy filters (repeatable)

```
GET /api/v1/banking/workflows/payment-workflow/instances?filter=status=eq:Active&filter=attributes=amount=gt:500
```

### 3) Instance list with groupBy and aggregations

`groupBy` and `aggregations` are **query parameters** (JSON strings):

```
GET /api/v1/banking/workflows/payment-workflow/instances?groupBy={"field":"attributes.currency"}&aggregations={"count":true,"sum":"attributes.amount"}
```

### 4) GraphQL filter request (single `filter` value)

You can send a single `filter` value that includes `filter`, `groupBy`, and `aggregations`:

```
filter={"filter":{"attributes":{"status":{"eq":"approved"}}},"groupBy":{"field":"attributes.currency"},"aggregations":{"count":true}}
```

### 5) Data function list with filtering

```
GET /api/v1/banking/workflows/payment-workflow/functions/data?filter={"status":{"eq":"Active"}}
```

## Response Shape

### Instance list (no groupBy)

`items` contains `GetInstanceOutput` objects:

- `id`, `key`, `flow`, `domain`, `flowVersion`
- `tags`, `attributes`, `extensions`
- `etag` is returned as a response header when available

### Instance list (groupBy)

`items` contains `GroupSummary` objects:

- `name`, `count`, `sum`, `avg`, `min`, `max`

### Data function list

Items are `GetInstanceDataOutput`:

- `data`, `extensions`
- `etag` returned via response header when available

## Notes

- Always URL-encode JSON query values.
- If you are unsure about an endpoint, verify in the `InstanceController` or `FunctionController`.

