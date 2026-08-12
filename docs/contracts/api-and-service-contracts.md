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
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/state` | Conditional state response, available transitions, role filtering, ETag, child correlations, workflow function discovery links. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/data` | Latest data, optional extensions, ETag. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/view` | Backend-driven view selection. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/schema` | Transition-aware schema. |
| `POST /{domain}/workflows/{workflow}/instances/{instance}/transitions/{transition}` | Runs a transition sync or async. |
| `GET /{domain}/functions` | Lists domain function definitions, including `verbs[]` and input/output schema and view references. |
| `GET\|POST\|PATCH\|DELETE /{domain}/functions/{function}` | Invokes a custom domain function. `GET /{function}` invokes — it is not a metadata route. |
| `GET\|POST\|PATCH\|DELETE /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}` | Invokes a custom instance function. |
| `GET /{domain}/functions/{function}/info` | Describes a custom domain function: verbs, scope, executable href, and view/schema hyperlinks. |
| `GET /{domain}/functions/{function}/view?target=input\|output` | Returns the view the function's `inputView`/`outputView` resolves to. |
| `GET /{domain}/functions/{function}/schema?target=input\|output` | Returns the schema the function's `inputSchema`/`outputSchema` resolves to. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/info` | Same description, resolved against the instance. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/view?target=input\|output` | Instance-bound view resolution. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/schema?target=input\|output` | Instance-bound schema resolution. |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/catalog` | Lists the workflow's declared functions, role-filtered, each linked to its `info` endpoint. |

### Custom function verbs and payload validation

A custom function may declare `verbs[]` and an `inputSchema`. Both are opt-in — a function that
declares neither accepts every routed verb and any body, which is the pre-existing behavior.

| Condition | Response |
| --- | --- |
| Verb not in declared `verbs[]` | `405 Method Not Allowed` with an `Allow` header listing the declared verbs. |
| Body present and fails `inputSchema` | `400` with field-level validation errors (same shape as transition schema validation). |
| `outputSchema` | Never enforced; declarative contract for clients only. |

All four contract slots may be authored as a single reference **or** as rule-based entries evaluated
in declaration order. When no entry matches, no contract applies: the body is not validated, `/info`
reports `hasView`/`hasSchema` false, and the content routes return `404` (`Function:800004`).
An unrecognized `target` is `400` (`Function:800005`). See
[Function Contract Resolution](../runtime/function-contract-resolution.md).

### State response: function catalog pointer

The state response points at the workflow's function catalog rather than carrying the list:

```jsonc
"functions": {
  "hasFunctions": true,
  "href": "/api/core/workflows/onboarding/instances/{id}/functions/catalog"
}
```

`hasFunctions` is `workflow.Functions.Count > 0`. The href is always emitted; the flag lets a client
skip the call when the flow ships no functions.

The list is **not** inlined because resolving it costs one component read per declared function plus a
role evaluation each — work that does not belong on a response served on every long-poll, and that is
wasted for clients which never look at functions.

`functions` is **not part of the ETag material**: `hasFunctions` belongs to the flow version, which the
fingerprint already covers. See
[Instance Function Cache and Fingerprint ETag](../runtime/state-function-cache-and-etag.md).

### Function catalog (`catalog`)

```
GET /{domain}/workflows/{workflow}/instances/{instance}/functions/catalog
```

Returns `{ "functions": [ { name, version, scope, href } ] }` in declaration order. Each href matches
the function's `scope`: `D` links to the domain `info` route, `F` and `I` to the instance one — the
domain route rejects the latter two with `403`, so linking them there would be a dead link.

**Role-filtered** through the same `IFunctionAccessPolicy` as execution and `/info`, so a function the
caller could not invoke is not advertised and every link is actionable. A reference whose component
cannot be resolved is omitted rather than failing the catalog.

### Function discovery (`/info`)

`/info` and the four content routes are `GET`-only and carry no ETag/304. All six run the same scope
and role gates as execution, so a caller denied on execution gets `403` rather than a description.
Built-in system functions (`state`, `view`, `data`, `schema`, `authorize`, `permissions`,
`hierarchy`, `human-task`, `master`, `catalog`) have no `sys-functions` component and return `404`
from `/info`.

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

### State response: scheduled transitions

The state response lists the transitions the runtime has already armed to fire automatically, so a
client can render countdowns and upcoming-action information without polling anything else:

```jsonc
"scheduledTransitions": [
  { "name": "payment-timeout", "kind": "scheduled", "executeAtUtc": "2026-08-03T14:30:00Z" }
]
```

- Built from the **persisted job state**: active `InstanceJob` rows of type `ScheduledTransition`
  whose `ExecuteAt` was captured at scheduling time — the exact instant the scheduler was armed
  with, never a re-evaluation of the transition's timer script. Ordered by `executeAtUtc`
  ascending; empty array when nothing is scheduled.
- `name` is the transition key; `kind` is currently always `scheduled` (a job-kind vocabulary —
  the workflow-level timeout is a candidate future kind). `executeAtUtc` is always UTC with the
  `Z` designator.
- **Not role-filtered**, unlike `availableTransitions`: a scheduled transition fires regardless of
  the caller, so the list is a fact about the instance, not a caller capability.
- Always describes the **polled instance itself** — during an active-subflow window it is not
  merged with the subflow's own list (poll the subflow instance for its schedule).
- Rows persisted before the `ExecuteAt` column existed are omitted rather than emitted without a
  time; they age out as their jobs fire or are cancelled.
- An entry may briefly remain visible with a past `executeAtUtc` while the fired transition's
  pipeline is still settling — the list reflects the persisted job rows, and the row is marked
  processed when the handler completes.
- Changes to the scheduled-job set participate in the state ETag (count + newest row), so a
  cancel-and-reschedule on a `$self` re-entry moves the ETag even though state and status do not —
  see [state-function cache and fingerprint ETag](../runtime/state-function-cache-and-etag.md).

### Client-facing hrefs and the `UrlTemplates` section

Every `href` a client receives — `availableTransitions[].href`, `data`, `view`, `schema`, `master`,
the function catalog entries, the long-poll `ack` — is a **relative path** built by
`IUrlTemplateBuilder`. The path below the prefix is fixed by the controller routes and lives in code
(`UrlTemplateDefaults`); the only per-deployment variable is the prefix, because an href must point at
the API gateway route rather than at the pod.

So a host configures **one key**:

```json
"UrlTemplates": { "BasePath": "/api/v1/monitor" }
```

Omit the section entirely and the application serves its own prefix, `/api/v1` — the same one its
controllers are routed under (`[Route("api/v{version:apiVersion}")]`). The orchestration host relies
on exactly that and declares nothing. An empty `BasePath` emits prefix-less paths, for a host mounted
at the root; a leading slash is added and a trailing one trimmed, so `api/v1/` and `/api/v1` are
equivalent.

The nineteen per-endpoint keys (`Start`, `Transition`, `Data`, …) remain available as optional
overrides for a gateway that routes one endpoint differently from its siblings. **An override is a
complete path and is used verbatim — `BasePath` is not prepended to it.** Overriding a template with
exactly what `BasePath` already yields is a test failure, so the section cannot drift back into
restating every route.

Note that `InstanceUrlTemplates` is a separate mechanism for internal service-to-service calls and
takes its version prefix from `vNextApi:ApiVersion`; it is unaffected by this section.

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
- `src/BBT.Workflow.Domain/Definitions/UrlTemplateOptions.cs`
- `src/BBT.Workflow.Domain/Definitions/UrlTemplateDefaults.cs`
- `src/BBT.Workflow.Execution.Abstractions/TaskEnvelope.cs`
- `src/BBT.Workflow.Events.Contracts/`
- `src/BBT.Workflow.Domain/Validation/SchemaValidationProblemDetails.cs`

