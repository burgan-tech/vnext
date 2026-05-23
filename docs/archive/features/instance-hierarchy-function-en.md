# Instance Hierarchy Function

## Overview

The hierarchy function returns the runtime hierarchy of a workflow instance as a recursive tree structure. It includes both direct and indirect child subflow and subprocess instances, enabling full visibility into nested workflow relationships.

## API Endpoints

### Single Instance Hierarchy

Returns the complete hierarchy tree for a specific instance.

```
GET /api/v1/{domain}/workflows/{workflow}/instances/{instance}/functions/hierarchy
```

| Parameter  | Location | Description                          |
|-----------|----------|--------------------------------------|
| `domain`  | Route    | Domain name                          |
| `workflow`| Route    | Workflow (flow) name                 |
| `instance`| Route    | Instance key or GUID                 |

### List Hierarchy

Returns hierarchy trees for a paged list of instances (one tree per instance in the page).

```
GET /api/v1/{domain}/workflows/{workflow}/functions/hierarchy
```

Supports standard list parameters: `page`, `pageSize`, `filter`, `sort`, `orderBy`.

## Response Structure

### GetInstanceHierarchyOutput

| Property | Type                    | Description                                  |
|----------|-------------------------|----------------------------------------------|
| `root`   | InstanceHierarchyNode   | Root node (the requested instance)           |

### InstanceHierarchyNode

| Property       | Type                    | Description                                                     |
|----------------|-------------------------|-----------------------------------------------------------------|
| `id`           | Guid                    | Instance ID                                                     |
| `key`          | string?                 | Human-readable instance key                                     |
| `flow`         | string                  | Workflow (flow) name                                            |
| `domain`       | string                  | Domain                                                          |
| `flowVersion`  | string?                 | Flow version                                                   |
| `currentState` | string?                 | Current state key                                              |
| `status`       | InstanceStatus?         | Instance status (Active, Completed, Faulted, etc.)             |
| `subFlowType`  | SubFlowType?            | SubFlow (S) or SubProcess (P). Null for root instance.         |
| `isCompleted`  | bool                    | Whether the subflow/subprocess correlation is completed        |
| `completedAt`  | DateTime?               | When the subflow/subprocess completed                          |
| `parentState`  | string?                 | State in parent from which this subflow was started            |
| `children`     | List\<InstanceHierarchyNode\> | Nested child subflow/subprocess instances              |

## Example Response

```json
{
  "root": {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "key": "order-123",
    "flow": "OrderWorkflow",
    "domain": "sales",
    "flowVersion": "1.0.0",
    "currentState": "ApprovalPending",
    "status": { "code": "A", "description": "Active" },
    "subFlowType": null,
    "isCompleted": false,
    "completedAt": null,
    "parentState": null,
    "children": [
      {
        "id": "7cb85f64-5717-4562-b3fc-2c963f66b0a7",
        "key": "approval-sub-1",
        "flow": "ApprovalSubflow",
        "domain": "sales",
        "flowVersion": "1.0.0",
        "currentState": "Completed",
        "status": { "code": "C", "description": "Completed" },
        "subFlowType": { "code": "S", "description": "Sub Flow" },
        "isCompleted": true,
        "completedAt": "2025-03-02T10:30:00Z",
        "parentState": "ApprovalPending",
        "children": [
          {
            "id": "9db85f64-5717-4562-b3fc-2c963f66c1b8",
            "key": "notification-task",
            "flow": "NotificationSubprocess",
            "domain": "sales",
            "flowVersion": "1.0.0",
            "currentState": "Sent",
            "status": { "code": "C", "description": "Completed" },
            "subFlowType": { "code": "P", "description": "Sub Process" },
            "isCompleted": true,
            "completedAt": "2025-03-02T10:25:00Z",
            "parentState": "Review",
            "children": []
          }
        ]
      }
    ]
  }
}
```

## Use Cases

- **Runtime visualization**: Display instance hierarchy in admin UIs or dashboards
- **Audit and traceability**: Trace execution flow across nested workflows
- **Debugging**: Understand parent-child relationships during development
- **Reporting**: Aggregate metrics across hierarchy levels

## Multi-Schema Support

Child instances may reside in different workflows (schemas). The hierarchy function automatically switches schema context when traversing the tree:

- Correlations are queried in the parent's schema
- Child instances are loaded from the child's schema (SubFlowName/SubFlowDomain)

## Scope

- **Included**: Both active and completed child correlations (full historical hierarchy)
- **Recursive**: Unlimited nesting depth—subflows of subflows are fully traversed
- **Both types**: SubFlows (blocking) and SubProcesses (non-blocking) are included

## Related

- [Domain Models](./architecture/domain-models.md) — Instance and InstanceCorrelation entities
- [Multi-Schema](./architecture/multi-schema.md) — Schema switching and resolution
