# SignalR Task

## Overview

The SignalR Task is a workflow task type that sends workflow instance state updates to a configured SignalR hub endpoint. This task is useful for notifying external systems or clients about workflow state changes.

## Task Type

- **Type**: `SignalR` (TaskType = 10)
- **Task Executor**: `SignalRTaskExecutor`

## Configuration

### appsettings.json

Add the SignalR URL to your configuration:

```json
{
  "SignalR": {
    "Url": "https://your-signalr-hub.com/api/notify"
  }
}
```

### Task Definition

```json
{
  "key": "notify-task",
  "domain": "your-domain",
  "version": "1.0",
  "type": "10",
  "config": {
    "notificationType": 1,
    "allowedHeaders": [
      "Authorization",
      "X-Correlation-Id",
      "X-Request-Id"
    ],
    "additionalData": {
      "customField": "customValue",
      "eventType": "workflow-completed"
    }
  }
}
```

## Properties

### SignalRTask

| Property | Type | Description | Required |
|----------|------|-------------|----------|
| `NotificationType` | `SignalRTaskType` | The SignalR notification type (currently only `WorkflowSignalR = 1`) | Yes |
| `AllowedHeaders` | `List<string>?` | List of header names that are allowed to be forwarded from context to SignalR request (case-insensitive). If null or empty, no headers will be forwarded. | No |
| `AdditionalData` | `JsonElement?` | Additional custom data to include in the SignalR request | No |

### SignalRTaskType Enum

- `WorkflowSignalR = 1`: Standard workflow SignalR notification

## Request Format

The SignalR task sends a request in the following format (CloudEvents-like structure):

### Without Additional Data

```json
{
  "id": "instance-guid",
  "source": "vnext",
  "type": "vnext.workflow",
  "subject": "workflow-completed",
  "data": {
    "data": {
      "href": "/domain/workflows/workflow-key/instances/instance-id/functions/data"
    },
    "view": {
      "href": "/domain/workflows/workflow-key/instances/instance-id/functions/view",
      "loadData": true
    },
    "state": "current-state",
    "status": "Active",
    "activeCorrelations": [],
    "transitions": [
      {
        "name": "transition-key",
        "href": "/domain/workflows/workflow-key/instances/instance-id/transitions/transition-key"
      }
    ],
    "eTag": "etag-value"
  }
}
```

### With Additional Data

When `additionalData` is configured in the task, it's included in the `data` object:

```json
{
  "id": "instance-guid",
  "source": "vnext",
  "type": "vnext.workflow",
  "subject": "workflow-completed",
  "data": {
    "data": {
      "href": "/domain/workflows/workflow-key/instances/instance-id/functions/data"
    },
    "view": {
      "href": "/domain/workflows/workflow-key/instances/instance-id/functions/view",
      "loadData": true
    },
    "state": "current-state",
    "status": "Active",
    "activeCorrelations": [],
    "transitions": [
      {
        "name": "transition-key",
        "href": "/domain/workflows/workflow-key/instances/instance-id/transitions/transition-key"
      }
    ],
    "eTag": "etag-value",
    "additionalData": {
      "customField": "customValue",
      "notificationType": "workflow-completed"
    }
  }
}
```

## Features

### 1. Instance State Output

The task automatically builds a complete `GetInstanceStateOutput` (or `GetInstanceStateOutputWithAdditionalData` when additional data is present) containing:
- Data href link
- View href link
- Current state
- Instance status
- Active correlations (retrieved directly from `context.Instance.ChildCorrelations`)
- Available transitions
- ETag
- Additional data (optional)

### 2. Selective Headers Forwarding

Only headers specified in the `allowedHeaders` configuration are forwarded from the workflow context to the SignalR hub request. This provides fine-grained control over which headers are sent and enhances security by preventing unintended header leakage.

**Features:**
- Case-insensitive header matching
- Only configured headers are forwarded
- If `allowedHeaders` is null or empty, no headers are forwarded
- Logs which headers were added for debugging purposes

**Example:**
```json
{
  "allowedHeaders": ["Authorization", "X-Correlation-Id", "X-Request-Id"]
}
```

This configuration will only forward `Authorization`, `X-Correlation-Id`, and `X-Request-Id` headers from the context, ignoring all other headers.

### 3. Additional Data

You can include custom additional data that will be added to the instance output within the `data` object:

```csharp
var signalRTask = SignalRTask.Create(config);
signalRTask.SetAdditionalData(new {
    UserId = 123,
    NotificationPreference = "email",
    Priority = "high"
});
```

**Important**: The `additionalData` is part of the `GetInstanceStateOutputWithAdditionalData` model, not a separate property on the SignalR request. This ensures the additional data is logically grouped with the instance state information.

### 4. Error Handling

The executor properly handles:
- HTTP request failures
- Cancellation tokens
- Configuration errors (missing SignalR URL)
- Invalid response formats

## Response Handling

### Success Response

When the SignalR hub returns a successful status code (2xx):

```json
{
  "success": true,
  "data": "response-from-hub",
  "taskType": "SignalR",
  "executionDurationMs": 123,
  "statusCode": 200,
  "headers": {
    "content-type": "application/json"
  },
  "metadata": {
    "url": "https://signalr-hub.com/api/notify",
    "taskType": "WorkflowSignalR",
    "instanceId": "instance-guid",
    "reasonPhrase": "OK"
  }
}
```

### Error Response

When the SignalR hub returns an error status code:

```json
{
  "success": false,
  "errorMessage": "SignalR request failed with status 500: Internal Server Error",
  "taskType": "SignalR",
  "executionDurationMs": 123,
  "statusCode": 500,
  "metadata": {
    "url": "https://signalr-hub.com/api/notify",
    "taskType": "WorkflowSignalR",
    "instanceId": "instance-guid",
    "reasonPhrase": "Internal Server Error",
    "responseContent": "error-details"
  }
}
```

## Usage Example

### 1. Create SignalR Task

```json
{
  "key": "notify-workflow-completion",
  "domain": "sales",
  "version": "1.0",
  "type": "10",
  "config": {
    "notificationType": 1,
    "allowedHeaders": [
      "Authorization",
      "X-Correlation-Id",
      "X-User-Id"
    ],
    "additionalData": {
      "eventType": "OrderCompleted",
      "priority": "high"
    }
  }
}
```

### 2. Use in Workflow State

```json
{
  "key": "completed-state",
  "onEntry": {
    "tasks": [
      {
        "domain": "sales",
        "key": "notify-workflow-completion",
        "version": "1.0"
      }
    ]
  }
}
```

## Object Pooling

The SignalR task supports object pooling for high-performance scenarios:

```json
{
  "TaskFactory": {
    "UseObjectPooling": true,
    "PooledTaskTypes": [
      "SignalRTask",
      "HttpTask",
      "DaprServiceTask"
    ]
  }
}
```

## Logging

The executor provides comprehensive logging:

- **Information**: Task execution start/completion, HTTP request/response
- **Debug**: Input preparation, header addition, response deserialization
- **Warning**: SSL validation disabled, non-success status codes
- **Error**: HTTP request failures, unexpected errors

Example log output:

```
[Information] Starting SignalR task execution for task notify-task - Type: WorkflowSignalR
[Debug] SignalR URL configured as: https://signalr-hub.com/api/notify
[Debug] Building instance state output for instance 12345678-1234-1234-1234-123456789abc
[Debug] Adding 3 headers to SignalR request for task notify-task
[Information] Sending SignalR request for task notify-task to https://signalr-hub.com/api/notify
[Information] SignalR request completed for task notify-task - Status: OK, Duration: 123ms
[Information] SignalR task notify-task completed successfully with status OK
```

## Best Practices

1. **Configure Timeout**: The executor uses the default HTTP client timeout (30 seconds). Configure appropriately for your SignalR hub.

2. **Error Handling**: Always handle potential errors in your workflow. The task will return error responses that can be processed by output mappings.

3. **Additional Data**: Use `additionalData` sparingly and only include necessary information to keep payloads small.

4. **Headers**: Be mindful of sensitive information in headers as they will be forwarded to the SignalR hub.

5. **Object Pooling**: Enable object pooling for high-throughput scenarios to reduce GC pressure.

## Related

- [Task Executors](task-executors.md)
- [HTTP Task](../src/BBT.Workflow.Application/Tasks/Executors/HttpTaskExecutor.cs)
- [Task Factory Pooling](task-factory-pooling.md)

