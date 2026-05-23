# Spec 03: Contracts and Validation

## Purpose

Give consumer teams one place to understand API, service invocation, event, and JSON
validation contracts.

## Deliverables

- [API and Service Contracts](../contracts/api-and-service-contracts.md)
- [JSON Validation](../contracts/json-validation.md)

## Key Decisions

- Route shapes and DTO property names are compatibility contracts.
- Validation error details must remain stable and consumer-safe.
- Task envelope and domain event contracts are cross-process contracts.
- ETag behavior is part of the state/data API contract.

## Acceptance Checklist

- Validation output shape is documented.
- Sync/async transition behavior is documented.
- ETag and `304` behavior are documented.
- Breaking and non-breaking contract changes are identified.

## Source Alignment

Review these files when the spec changes:

- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/`
- `src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs`
- `src/BBT.Workflow.Domain/Validation/`
- `src/BBT.Workflow.Execution.Abstractions/`
- `src/BBT.Workflow.Events.Contracts/`

