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
| `GET /{domain}/functions` | Lists domain function definitions, including `verbs[]` and input/output schema and view references. |
| `GET\|POST\|PATCH\|DELETE /{domain}/functions/{function}` | Invokes a custom domain function. `GET /{function}` invokes — it is not a metadata route. |
| `GET\|POST\|PATCH\|DELETE /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}` | Invokes a custom instance function. |

### Custom function verbs and payload validation

A custom function may declare `verbs[]` and an `inputSchema`. Both are opt-in — a function that
declares neither accepts every routed verb and any body, which is the pre-existing behavior.

| Condition | Response |
| --- | --- |
| Verb not in declared `verbs[]` | `405 Method Not Allowed` with an `Allow` header listing the declared verbs. |
| Body present and fails `inputSchema` | `400` with field-level validation errors (same shape as transition schema validation). |
| `outputSchema` | Never enforced; declarative contract for clients only. |

The HTTP `QUERY` method is **not supported** — declaring it is a component validation error and no
route accepts it. Surrounding tooling (Swagger/OpenAPI, gateways, client SDKs) does not handle an
unrecognised method yet; model body-carrying reads as `POST`. See
[Function Handler Architecture](../domain/function-handler-architecture.md) § Custom Function Contract.

### View response: display modes

The view response keeps `display` as the SDI (single-document) string and adds a `modes` object
carrying `{ sdi, mdi }`. Clients predating MDI support keep reading `display` unchanged. See
[View Display Modes](../domain/view-display-modes.md).

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

## Internal-Only Endpoints

Several routes on `InstanceController` exist purely for runtime-to-runtime calls and carry
`[ApiExplorerSettings(IgnoreApi = true)]` — hidden from the public Swagger group. Existing examples:
`sub/state`, `sub/fault`, `child-cancel`, `child-fault`, `longpoll/ack`. Two more back cross-domain
related-instance reads (see
[Script Related Instance Access](../runtime/script-related-instance-access.md)):

| Method | Route | Response |
| --- | --- | --- |
| GET | `.../instances/{instance}/internal/related-data` | `200` snapshot, `204` if the instance does not exist. |
| POST | `.../workflows/{workflow}/internal/related-data/batch` | `200` array (possibly `[]`), `400` above 100 ids. |

**`[ApiExplorerSettings(IgnoreApi = true)]` hides a route from Swagger. It does nothing to routing.**
Confirmed by reading the orchestration host directly:

- There is no `[Authorize]` on `InstanceController` or on these actions.
- `Program.cs` for the orchestration host registers **no** authentication or authorization middleware
  (`UseAuthentication` / `UseAuthorization` are absent).
- No NetworkPolicy or ingress manifest for this host exists anywhere in this repository.
- `etc/docker/docker-compose.dev.yml` publishes the orchestration container's Kestrel port straight to
  the host (`4201:5000`), bypassing the Dapr sidecar entirely. In local development these routes **are**
  reachable directly from the host machine — expected for dev, not for any deployed environment.

Their safety rests **entirely** on network isolation: sidecar-to-sidecar Dapr traffic only, with the
orchestration host's public port unreachable from outside the cluster/mesh. This is the same posture as
the pre-existing internal endpoints above, but the **blast radius is larger**: `sub/state`,
`child-cancel`, and `child-fault` perform one narrow, parameterized action each, whereas
`internal/related-data` and its batch form return **complete, unfiltered instance data for any instance
id supplied** (no `x-roles` filtering, no query-role check). Whoever owns ingress and NetworkPolicy for
this host must confirm these paths are not exposed before any environment goes live — this cannot be
verified from application code alone, since nothing in the application layer restricts it.

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

