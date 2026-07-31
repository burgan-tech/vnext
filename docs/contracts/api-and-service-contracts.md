# API and Service Contracts

## Purpose

This page summarizes stable contracts used by clients, Orchestration, Execution, workers,
and remote runtimes. It is the starting point before changing route shapes, request/response
DTOs, error envelopes, or service invocation payloads.

## Boundaries

Controllers bind HTTP routes and translate results. Application services own use-case
contracts. Execution receives task envelopes only. Workers consume distributed event
contracts. Remote services call public runtime APIs rather than internal repositories.

## Contract Map

| Contract | Direction | Stability notes |
| --- | --- | --- |
| Instance start/transition APIs | Client -> Orchestration | Route, response status, sync/async semantics are client contracts. |
| Function APIs | Client -> Orchestration | `state`, `data`, `view`, `schema`, authorization, hierarchy. |
| Task envelope | Orchestration -> Execution | Strongly typed binding and task type discriminator. |
| Remote app services | Runtime -> Runtime | Uses public instance/function routes with forwarded headers. |
| Domain events | Orchestration -> Outbox -> Inbox | Event payloads are distributed contracts. |
| JSON validation errors | Runtime -> Client | Error code/detail shape should remain stable. |

## HTTP Function Contracts

| Endpoint family | Behavior |
| --- | --- |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/state` | Conditional state response, available transitions, role filtering, ETag, child correlations. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/data` | Latest data, optional extensions, ETag. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/view` | Backend-driven view selection. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/schema` | Transition-aware schema. |
| `POST /{domain}/workflows/{workflow}/instances/{instance}/transitions/{transition}` | Runs a transition sync or async. |

### State response: correlation lists

The state response exposes child correlations through two lists, and clients must pick the one
matching their intent:

| Field | Contents |
| --- | --- |
| `activeCorrelations` | Open correlations only. Stable, long-standing semantics — safe for "is a sub item still running". |
| `correlations` | Full set, active **and** completed, ordered by `createdAt` ascending. Each entry carries `isCompleted`, `completedAt`, `terminalOutcome` (`completed` / `faulted` / `canceled`), `currentState`, `stateChangedAt`. Use this to reconstruct which sub items ran and how each ended. |

Both merge the active subflow's own corresponding list when the parent is inside a subflow, so a
nested chain stays consistent. `correlations` is read through a dedicated query, so under
concurrent completion its active subset can be a moment fresher than `activeCorrelations`.
Changes to the correlation set participate in the state ETag — see
[state-function cache and fingerprint ETag](../runtime/state-function-cache-and-etag.md).

## Sync and Async Semantics

`sync=true` blocks until the pipeline completes and should be used for deterministic,
short-lived backend integrations. `sync=false` accepts the request and returns the instance
identity/status quickly; clients poll the state function until the instance becomes Active,
Completed, or Faulted.

## Error Contracts

Validation errors should carry stable codes and field details. Pipeline and domain errors
should be returned through the Aether result/controller mapping. Remote app services should
preserve remote domain errors when possible and use transient errors for network failures.

## Failure Modes

- Route-compatible but semantically incompatible DTO changes break SDKs.
- Removing ETag behavior causes polling clients to over-fetch.
- Changing task envelope discriminator breaks Execution routing.
- Publishing domain events without matching handlers causes eventual side effects to stall.

## Observability

Every boundary should carry domain, flow, instance id, transition key or task key where
available. Current-user and correlation headers should be forwarded across remote calls.

## Change Safety

- Treat routes, DTO property names, task type discriminators, event names, and validation
  detail shapes as externally visible contracts.
- Add compatibility tests for consumers when changing a contract.
- Prefer additive changes over renames.
- For breaking changes, document migration path and deprecation timing in `docs/specs/`.

## References

- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs`
- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Functions/FunctionController.cs`
- `src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs`
- `src/BBT.Workflow.Execution.Abstractions/TaskEnvelope.cs`
- `src/BBT.Workflow.Events.Contracts/`
- `src/BBT.Workflow.Domain/Validation/SchemaValidationProblemDetails.cs`

