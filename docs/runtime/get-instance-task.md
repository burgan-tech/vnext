# GetInstance Task

`GetInstanceTask` (type `"19"`, `TaskType.GetInstance`) is a trigger task that retrieves a **full single-instance
projection** — metadata **and** data — for a workflow instance. It is the task-level equivalent of:

```
GET /api/v1/{domain}/workflows/{workflow}/instances/{instance}
```

It complements the two existing instance-query tasks:

| Task | Type | Returns | Endpoint |
|------|------|---------|----------|
| `GetInstancesTask` | 15 | Paged/grouped list | `GET .../instances` |
| `GetInstanceDataTask` | 13 | Data (attributes) only | `GET .../instances/{instance}/data` |
| **`GetInstanceTask`** | **19** | **Full projection (metadata + attributes)** | `GET .../instances/{instance}` |

## When to use

Use `GetInstanceTask` when a mapping needs both the instance **metadata** (state, status, audit,
duration, incident info) and its **attributes** in a single call — for example, to branch on the
target instance's `status`/`currentState` while also reading its data.

If you only need the attributes, prefer `GetInstanceDataTask` (lighter payload).

## Response shape (local == remote)

The response exposed to the script context is a `GetInstanceOutput`:

```jsonc
{
  "id": "d2d65771-5595-44aa-b0e5-630353d87a80",
  "key": "...",
  "flow": "...",
  "domain": "...",
  "flowVersion": "...",
  "eTag": "\"...\"",
  "tags": [],
  "metadata": {
    "currentState": "...",
    "effectiveState": "...",
    "status": "A",
    "createdAt": "...",
    "modifiedAt": "...",
    "incident": { /* present only when the instance has incidents */ }
  },
  "attributes": { /* instance data */ },
  "extensions": { /* present only when extensions requested */ }
}
```

Same-domain execution runs in-process through `IInstanceQueryGateway.GetInstanceAsync`; cross-domain
execution calls the same REST endpoint on the target domain (HTTP or Dapr). **Both paths surface the
identical `GetInstanceOutput` template**, so a single mapping works regardless of where the target
instance lives. `304 Not Modified` (ETag) is handled the same as `GetInstanceDataTask`.

## Definition example

```jsonc
{
  "key": "load-account",
  "type": "19",
  "domain": "core",
  "flow": "account-opening",
  "instanceId": "d2d65771-5595-44aa-b0e5-630353d87a80",
  // or: "key": "some-business-key"
  "extensions": [],
  "useDapr": false,
  "validateSsl": true,
  "timeoutSeconds": 30
}
```

Either `instanceId` (GUID) or `key` (business key) must be supplied; `instanceId` takes precedence.

## Mapping example

```csharp
// OutputHandler mapping — reads the StandardTaskResponse
var response = context.GetTaskResponse("load-account");
var status = response.Data.metadata.status;      // "A", "C", "F", ...
var amount = response.Data.attributes.amount;     // instance data
```
