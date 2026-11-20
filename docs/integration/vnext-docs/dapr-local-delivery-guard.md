---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# Dapr-triggered endpoints accept only the own sidecar — Handoff for vnext-docs

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/1320
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1253

## What changed in the runtime

- `POST {domain}/workflows/{workflow}/instances/events`, `.../instances/schedule` and Dapr Scheduler's
  `/job/{jobName}` accept only a delivery from the domain's own Dapr sidecar: exactly one
  `dapr-api-token` header equal to the app API token, and no `dapr-caller-app-id` header. Anything else
  gets an empty-body `403` (in `Enforce`); not `404`, so Dapr retries a misconfigured own-sidecar delivery instead of dropping it.
- `Dapr:LocalDelivery:Mode` = `Off` | `LogOnly` (default) | `Enforce`. The token comes only from the file named by
  `Workflow:RuntimeCaller:AppApiTokenFile`, read once at start and trimmed; the app no longer reads `APP_API_TOKEN` and
  there is no `Dapr:LocalDelivery:AppApiToken`. A set path with a missing or empty file fails startup. `LogOnly` serves
  the request and logs EventId 40996 (path unset: it logs EventId 40998 once at startup and nothing per request);
  `Enforce` with the path unset rejects everything and logs EventId 40997 at
  startup. The `dapr-api-token` header is removed right after routing, before controllers and any Dapr
  job handler run.
- A DROP response body's `reason` is now only the error code; the full message is in the EventId 40994 log.
- A call through the runtime's OWN sidecar API (`/v1.0/invoke/<own-app-id>/...`) passes the guard (no
  caller header, token present). On Kubernetes that API listens on loopback by default, so only the pod
  can make it; deployments must keep `dapr.io/sidecar-listen-addresses` on loopback.
- Scope: orchestration host only. Execution/inbox/outbox hosts and vNext-to-vNext internal endpoints
  are a later phase.

Source of truth: `docs/domain/event-driven-workflows.md` § Who can call it, `docs/domain/scheduled-workflow-start.md`,
`vnext-meta` migrations `dapr-local-delivery-guard` and `event-drop-reason-is-code-only`; code in
`src/BBT.Workflow.HttpApi.Shared/Middlewares/DaprLocalDelivery/`.

## What this product must do

- Event-driven and scheduled-start pages: state the own-sidecar rule, show `dapr-api-token` on every
  direct `curl`, and note that a direct call without it gets `403` in `Enforce`.
- Deployment/security pages: document the sidecar side (`APP_API_TOKEN` on daprd only; Kubernetes: one Secret per domain, key `token`, annotation `dapr.io/app-token-secret: <secret>` on the orchestration Deployment only, and the same Secret mounted read-only into the orchestration container with `Workflow__RuntimeCaller__AppApiTokenFile` pointing at it; no `APP_API_TOKEN` in the app container), the script-sandbox condition, restart-based rotation, the requirement to keep the sidecar API on loopback, and the rollout: `LogOnly` -> enable the sidecar token -> EventId 40996 at zero -> `Enforce`.
- Change any example that shows `"reason": "Code: message"` to the code-only form.
- Release blog for v0.0.107: a security fix, non-breaking while `LogOnly`; operators should plan the `Enforce` step.

## Contract and examples

```bash
curl -X POST "http://localhost:4201/api/v1/{domain}/workflows/{workflow}/instances/events?action=start" \
  -H "Content-Type: application/json" \
  -H "dapr-api-token: $(cat <token file>)" \
  -d '{"orderId":"ord-1"}'
```

```json
{ "status": "DROP", "reason": "NotAnEventTransition" }
```

## Acceptance

- No page presents the events, schedule or job endpoints as callable by any HTTP client.
- The rollout order (runtime in `LogOnly`, then sidecar token, then `Enforce`) appears once, with the rollback.

## Out of scope

- The Helm chart change (separate PR in vnext-helm-charts) and phase 2 internal endpoints.
