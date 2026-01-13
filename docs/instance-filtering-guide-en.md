# Instance Filtering Guide

## Overview

The vNext workflow system provides powerful filtering capabilities for querying instances. You can filter on both **Instance table columns** and **JSON data fields** using either legacy format or GraphQL-style JSON format.

## Supported Routes

### 1. Function/Data Route
```http
GET /{domain}/function/{workflow}/data
```

### 2. Workflow Instances Route
```http
GET /{domain}/workflows/{workflow}/instances
```

Both routes support the same filtering capabilities with the `filter` query parameter.

---

## Filter Formats

### Legacy Format
Simple key-value format: `field=operator:value`

### GraphQL Format (Recommended)
JSON-based format with support for logical operators: `{"field":{"operator":"value"}}`

---

## Filterable Fields

### Instance Table Columns
Direct database columns that can be filtered:

| Column | Type | Description | Supported Operators |
|--------|------|-------------|---------------------|
| `key` | string | Instance key | eq, ne, like, startswith, endswith, in, nin |
| `flow` | string | Workflow name | eq, ne, like, startswith, endswith, in, nin |
| `status` | string | Instance status | eq, ne, in, nin |
| `currentState` (or `state`) | string | Current state | eq, ne, like, startswith, endswith, in, nin |
| `createdAt` | DateTime | Creation timestamp | eq, ne, gt, ge, lt, le, between |
| `modifiedAt` | DateTime | Modification timestamp | eq, ne, gt, ge, lt, le, between |
| `completedAt` | DateTime | Completion timestamp | eq, ne, gt, ge, lt, le, between |
| `isTransient` | boolean | Transient flag | eq, ne |

### JSON Data Fields (attributes)
Any field stored in the instance's JSON data can be filtered using the `attributes` prefix.

---

## Supported Operators

| Operator | Description | Example Value |
|----------|-------------|---------------|
| `eq` | Equals | `"1111"` |
| `ne` | Not equals | `"test"` |
| `gt` | Greater than | `"100"` |
| `ge` | Greater than or equal | `"100"` |
| `lt` | Less than | `"100"` |
| `le` | Less than or equal | `"100"` |
| `between` | Between (inclusive) | `["2024-01-01", "2024-12-31"]` |
| `like` | Contains (case-insensitive) | `"workflow"` |
| `startswith` | Starts with | `"payment"` |
| `endswith` | Ends with | `"flow"` |
| `in` | In list | `["Active", "Busy"]` |
| `nin` | Not in list | `["Completed", "Faulted"]` |
| `isnull` | Is null or not null | `true` or `false` |

---

## Status Values

The `status` field accepts both codes and names:

| Status Name | Code | Description |
|-------------|------|-------------|
| `Active` | `A` | Instance is active |
| `Busy` | `B` | Instance is processing |
| `Completed` | `C` | Instance completed successfully |
| `Faulted` | `F` | Instance failed |
| `Passive` | `P` | Instance is passive |

---

## GraphQL Format Examples

### 1. Simple Instance Column Filter

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"key":{"eq":"payment-12345"}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "flow": "payment-workflow",
      "flowVersion": "1.0.0",
      "domain": "banking",
      "status": {
        "code": "A",
        "description": "Active"
      },
      "attributes": {
        "amount": 1000,
        "currency": "USD",
        "customerId": "CUST-123"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 2. Multiple Instance Column Filters (AND Logic)

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"status":{"eq":"Active"},"createdAt":{"gt":"2024-01-01"}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "flow": "payment-workflow",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 500,
        "customerId": "CUST-124"
      }
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440002",
      "key": "payment-12347",
      "flow": "payment-workflow",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 750,
        "customerId": "CUST-125"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 3. JSON Data Field Filter (attributes)

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"attributes":{"customerId":{"eq":"CUST-123"}}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "attributes": {
        "amount": 1000,
        "currency": "USD",
        "customerId": "CUST-123",
        "transactionDate": "2024-01-15"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 4. Mixed Filter (Instance + JSON Fields)

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"key":{"like":"payment"},"status":{"eq":"Active"},"attributes":{"amount":{"gt":"500"}}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 1000,
        "customerId": "CUST-124"
      }
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440002",
      "key": "payment-big-12347",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 750,
        "customerId": "CUST-125"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 5. Date Range Filter

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"createdAt":{"between":["2024-01-01","2024-01-31"]}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440003",
      "key": "payment-jan-001",
      "createdAt": "2024-01-15T10:30:00Z",
      "status": {"code": "C", "description": "Completed"},
      "attributes": {
        "amount": 250,
        "transactionDate": "2024-01-15"
      }
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440004",
      "key": "payment-jan-002",
      "createdAt": "2024-01-20T14:45:00Z",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 890,
        "transactionDate": "2024-01-20"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 6. Status IN Filter

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"status":{"in":["Active","Busy"]}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440005",
      "key": "payment-12348",
      "status": {"code": "A", "description": "Active"},
      "attributes": {"amount": 100}
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440006",
      "key": "payment-12349",
      "status": {"code": "B", "description": "Busy"},
      "attributes": {"amount": 200}
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 7. Logical Operators - AND

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"and":[{"status":{"eq":"Active"}},{"attributes":{"amount":{"gt":"500"}}}]}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440007",
      "key": "payment-12350",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 1500,
        "customerId": "CUST-200"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 8. Logical Operators - OR

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"or":[{"key":{"eq":"payment-12345"}},{"key":{"eq":"payment-12346"}}]}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "attributes": {"amount": 1000}
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "attributes": {"amount": 500}
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 9. Logical Operators - NOT

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"not":{"status":{"in":["Completed","Faulted"]}}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440008",
      "key": "payment-12351",
      "status": {"code": "A", "description": "Active"},
      "attributes": {"amount": 300}
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440009",
      "key": "payment-12352",
      "status": {"code": "B", "description": "Busy"},
      "attributes": {"amount": 450}
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

---

## Group By and Aggregations

### 10. Group By with Count

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"groupBy":{"field":"attributes.status","aggregations":{"count":true}}}
```

**Response:**
```json
{
  "groups": [
    {
      "name": "pending",
      "count": 45
    },
    {
      "name": "approved",
      "count": 123
    },
    {
      "name": "rejected",
      "count": 12
    }
  ]
}
```

### 11. Group By with Multiple Aggregations

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"groupBy":{"field":"attributes.currency","aggregations":{"count":true,"sum":"attributes.amount","avg":"attributes.amount","min":"attributes.amount","max":"attributes.amount"}}}
```

**Response:**
```json
{
  "groups": [
    {
      "name": "USD",
      "count": 150,
      "sum": 450000,
      "avg": 3000,
      "min": 10,
      "max": 50000
    },
    {
      "name": "EUR",
      "count": 75,
      "sum": 180000,
      "avg": 2400,
      "min": 50,
      "max": 25000
    },
    {
      "name": "GBP",
      "count": 30,
      "sum": 90000,
      "avg": 3000,
      "min": 100,
      "max": 15000
    }
  ]
}
```

### 12. Group By Multiple Fields

**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"groupBy":{"fields":["attributes.currency","attributes.status"],"aggregations":{"count":true,"sum":"attributes.amount"}}}
```

**Response:**
```json
{
  "groups": [
    {
      "name": "USD_pending",
      "count": 30,
      "sum": 90000
    },
    {
      "name": "USD_approved",
      "count": 100,
      "sum": 300000
    },
    {
      "name": "EUR_pending",
      "count": 15,
      "sum": 36000
    },
    {
      "name": "EUR_approved",
      "count": 50,
      "sum": 120000
    }
  ]
}
```

---

## Function/Data Route Examples

The Function/Data route provides a simplified interface for querying instances.

### 13. Function/Data - Simple Filter

**Request:**
```bash
GET /banking/function/payment-workflow/data?filter={"attributes":{"customerId":{"eq":"CUST-123"}}}
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "flow": "payment-workflow",
      "domain": "banking",
      "attributes": {
        "customerId": "CUST-123",
        "amount": 1000,
        "currency": "USD",
        "status": "approved"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 14. Function/Data - Complex Filter with Extensions

**Request:**
```bash
GET /banking/function/payment-workflow/data?filter={"status":{"eq":"Active"},"attributes":{"amount":{"gt":"500"}}}&extensions=customerInfo,transactionHistory
```

**Response:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "attributes": {
        "amount": 1500,
        "customerId": "CUST-200"
      },
      "extensions": {
        "customerInfo": {
          "name": "John Doe",
          "email": "john.doe@example.com",
          "tier": "gold"
        },
        "transactionHistory": {
          "totalTransactions": 45,
          "totalAmount": 67500,
          "averageAmount": 1500
        }
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 15. Function/Data - Group By

**Request:**
```bash
GET /banking/function/payment-workflow/data?filter={"groupBy":{"field":"attributes.paymentMethod","aggregations":{"count":true,"sum":"attributes.amount"}}}
```

**Response:**
```json
{
  "groups": [
    {
      "name": "credit_card",
      "count": 250,
      "sum": 750000
    },
    {
      "name": "bank_transfer",
      "count": 150,
      "sum": 900000
    },
    {
      "name": "paypal",
      "count": 100,
      "sum": 300000
    }
  ]
}
```

---

## Best Practices

### 1. Use GraphQL Format for Complex Queries
GraphQL format is more readable and supports logical operators.

**Good:**
```json
{
  "and": [
    {"status": {"eq": "Active"}},
    {"attributes": {"amount": {"gt": "500"}}}
  ]
}
```

**Also Works (Legacy):**
```text
status=eq:Active&attributes=amount=gt:500
```

### 2. Use Specific Fields for Better Performance
Filter on indexed Instance columns when possible.

**Better Performance:**
```json
{"key": {"eq": "payment-12345"}}
```

**Slower:**
```json
{"attributes": {"someUnindexedField": {"eq": "value"}}}
```

### 3. Use Status Names for Readability
```json
{"status": {"eq": "Active"}}
```
is equivalent to:
```json
{"status": {"eq": "A"}}
```

### 4. Combine Filters Efficiently
Use AND for restrictive filters, OR for inclusive filters.

**Restrictive (fewer results):**
```json
{
  "and": [
    {"status": {"eq": "Active"}},
    {"createdAt": {"gt": "2024-01-01"}},
    {"attributes": {"amount": {"gt": "1000"}}}
  ]
}
```

**Inclusive (more results):**
```json
{
  "or": [
    {"status": {"eq": "Active"}},
    {"status": {"eq": "Busy"}}
  ]
}
```

### 5. Use Group By for Analytics
When you need statistics, use group by instead of fetching all records.

**Efficient:**
```json
{
  "groupBy": {
    "field": "attributes.status",
    "aggregations": {
      "count": true,
      "sum": "attributes.amount"
    }
  }
}
```

---

## Error Handling

### Invalid Filter Syntax
**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={invalid json}
```

**Response:**
```json
{
  "error": {
    "code": "invalid_filter",
    "message": "Invalid filter syntax. Expected valid JSON.",
    "details": "Unexpected character at position 1"
  }
}
```

### Unsupported Operator
**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"status":{"regex":".*Active.*"}}
```

**Response:**
```json
{
  "error": {
    "code": "unsupported_operator",
    "message": "Operator 'regex' is not supported",
    "supportedOperators": ["eq", "ne", "gt", "ge", "lt", "le", "between", "like", "startswith", "endswith", "in", "nin"]
  }
}
```

### Invalid Column Name
**Request:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"invalidColumn":{"eq":"value"}}
```

**Response:**
```json
{
  "error": {
    "code": "invalid_column",
    "message": "Column 'invalidColumn' is not a valid Instance column. Use 'attributes.fieldName' for JSON fields.",
    "validColumns": ["key", "flow", "status", "currentState", "createdAt", "modifiedAt", "completedAt", "isTransient"]
  }
}
```

---

## Performance Tips

1. **Use Pagination**: Always use `page` and `pageSize` parameters
   ```text
   ?page=1&pageSize=20
   ```

2. **Filter on Indexed Columns**: Prefer `key`, `status`, `createdAt` for better performance

3. **Limit Group By Fields**: Group by on a maximum of 2-3 fields for optimal performance

4. **Use Date Ranges Wisely**: Narrow date ranges improve query performance
   ```json
   {"createdAt": {"between": ["2024-01-01", "2024-01-31"]}}
   ```

5. **Avoid Wildcard Searches on Large Datasets**: Use `startswith` or `endswith` instead of `like` when possible

---

## Summary

- **Instance Columns**: Direct table columns (key, status, createdAt, etc.)
- **JSON Fields**: Use `attributes` prefix for JSON data
- **Format**: GraphQL JSON format recommended for complex queries
- **Operators**: 13 operators supported (eq, ne, gt, ge, lt, le, between, like, startswith, endswith, in, nin, isnull)
- **Logical Operators**: AND, OR, NOT for complex conditions
- **Group By**: Analytics with count, sum, avg, min, max aggregations
- **Routes**: Both `/function/{workflow}/data` and `/workflows/{workflow}/instances` support filtering

For more examples and documentation, visit the vNext Runtime documentation.

