# OpenTelemetry Logging

## Overview

vNext uses OpenTelemetry-compatible logging and tracing through the Aether SDK, with workflow-specific logging helpers in `BBT.Workflow.Domain/Logging`. This document focuses on the workflow-specific primitives and conventions used in the codebase.

## Workflow Logging Primitives

### WorkflowLogs (source-generated)

`WorkflowLogs` provides source-generated logging methods with structured `EventId` values:

```csharp
logger.StateChanged(fromState, toState, instanceId);
logger.TransitionEnqueued(transitionKey, instanceId, jobName);
logger.TaskExecutionFailed(ex, taskKey, taskType, instanceId);
```

Implementation: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`

### WorkflowEventIds

Event IDs are centralized in `WorkflowEventIds` with a tiered numbering scheme:

- `10xxx`: Execution layer
- `20xxx`: Orchestration layer
- `40xxx`: Application layer

Within each range:

- `xx01-xx39`: Information
- `xx40-xx69`: Warning
- `xx70-xx99`: Error

Implementation: `src/BBT.Workflow.Domain/Logging/WorkflowEventIds.cs`

### TelemetryConstants

Span/trace tags are standardized for OpenTelemetry:

```csharp
activity?.SetTag(TelemetryConstants.TagNames.Domain, ctx.Workflow.Domain);
activity?.SetTag(TelemetryConstants.TagNames.InstanceId, ctx.InstanceId);
activity?.SetTag(TelemetryConstants.TagNames.TransitionKey, ctx.TransitionKey);
```

Implementation: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`

### ActivitySources (task and subflow spans)

Workflow-specific spans use these ActivitySource names. If the host configures OpenTelemetry with explicit sources, register them so these spans are exported:

| Source name | Purpose |
|------------|---------|
| `BBT.Workflow.Tasks` | Task execution phases (PrepareInput, Invoke, ProcessOutput). See `TaskExecutionActivityHelper`. |
| `BBT.Workflow.SubFlow` | SubFlow operations. See `SubFlowActivityHelper`. |
| `BBT.Workflow.BackgroundJobs` | Background job handlers. See `BackgroundJobActivityHelper`. |

If the host uses a wildcard (e.g. `AddSource("BBT.Workflow.*")`), no per-source registration is needed.

### ActivityExtensions

Helper methods for activity naming and error status:

```csharp
Activity.Current?.SetDisplayName("[Schedule] ScheduleTransitionsStep");
Activity.Current?.RecordExceptionWithStatus(ex, "Task execution failed");
```

Implementation: `src/BBT.Workflow.Domain/Logging/ActivityExtensions.cs`

## Error Factories

Workflow errors are created as `BBT.Aether.Results.Error` objects. Two factories are used in core flows:

### ExecutionErrors (Application)

`ExecutionErrors` provides centralized errors for execution scenarios (e.g., missing instances, transition mapping failures):

```csharp
ExecutionErrors.InstanceNotFoundForResponse(instanceId);
ExecutionErrors.TransitionRuleEvaluationFailed(transitionKey, errorMessage, detail);
```

Implementation: `src/BBT.Workflow.Application/Execution/ExecutionErrors.cs`

### WorkflowErrors (Domain)

`WorkflowErrors` provides domain-level errors for instance, transition, schema, and task validation cases.

Implementation: `src/BBT.Workflow.Domain/Logging/WorkflowErrors.cs`

## Usage Patterns

### Prefer Source-Generated Logs

Use `WorkflowLogs` extensions instead of ad-hoc `LogInformation`:

```csharp
logger.TransitionRuleFailed(transitionKey, instanceId, reason);
logger.AutoTransitionSelected(transitionKey, stateKey, instanceId);
```

### Set Activity Metadata

Enrich spans with telemetry tags and a readable display name:

```csharp
Activity.Current?.SetDisplayName($"[{Order}] {nameof(ScheduleTransitionsStep)}");
Activity.Current?.SetTag(TelemetryConstants.TagNames.JobName, jobName);
```

### Error Handling

Errors should be surfaced as `Result<T>` failures. When logging, use the dedicated workflow log methods and preserve structured fields.

## Log Schema (Example)

```json
{
  "level": "Information",
  "event.id": 10003,
  "event.name": "StateChanged",
  "category": "BBT.Workflow.Execution.Pipeline.Steps.ChangeStateStep",
  "instanceId": "8b3e4a5c-...",
  "transitionKey": "submit",
  "traceId": "4d5e6f...",
  "spanId": "1a2b3c..."
}
```

## Implementation References

- `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- `src/BBT.Workflow.Domain/Logging/WorkflowEventIds.cs`
- `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`
- `src/BBT.Workflow.Domain/Logging/ActivityExtensions.cs`
- `src/BBT.Workflow.Domain/Logging/WorkflowErrors.cs`
- `src/BBT.Workflow.Application/Execution/ExecutionErrors.cs`
