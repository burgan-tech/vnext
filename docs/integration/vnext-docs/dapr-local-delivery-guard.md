---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# Dapr-triggered endpoints accept only the own sidecar — Handoff for vnext-docs

## Context

- vNext PR: burgan-tech/vnext — `feat(security): guard Dapr-triggered endpoints to own-sidecar deliveries`
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1253

## What changed in the runtime

- `POST {domain}/workflows/{workflow}/instances/events`, `.../instances/schedule` and Dapr Scheduler's
  `/job/{jobName}` accept only a delivery from the domain's own Dapr sidecar: exactly one
  `dapr-api-token` header equal to the app API token, and no `dapr-caller-app-id` header. Anything else
  gets an empty-body `404` (in `Enforce`).
- `Dapr:LocalDelivery:Mode` = `Off` | `LogOnly` (default) | `Enforce`; `Dapr:LocalDelivery:AppApiToken`,
  falling back to env `APP_API_TOKEN`. `LogOnly` serves the request and logs EventId 40996;
  `Enforce` with no token rejects everything and logs EventId 40997 at startup.
- A DROP response body's `reason` is now only the error code; the full message is in the EventId 40994 log.
- Scope: orchestration host only. Execution/inbox/outbox hosts and vNext-to-vNext internal endpoints
  are a later phase.

Source of truth: `docs/domain/event-driven-workflows.md` § Who can call it, `docs/domain/scheduled-workflow-start.md`,
`vnext-meta` migrations `dapr-local-delivery-guard` and `event-drop-reason-is-code-only`; code in
`src/BBT.Workflow.HttpApi.Shared/Middlewares/DaprLocalDelivery/`.

## What this product must do

- Event-driven and scheduled-start pages: state the own-sidecar rule, show `dapr-api-token` on every
  direct `curl`, and note that a direct call without it gets `404` in `Enforce`.
- Deployment/security pages: document the sidecar side (`APP_API_TOKEN` on daprd; Kubernetes annotation
  `dapr.io/app-token-secret: <secret>`, key `token`, plus the same secret on the app container via
  `secretKeyRef`) and the rollout: `LogOnly` -> enable the sidecar token -> EventId 40996 at zero -> `Enforce`.
- Change any example that shows `"reason": "Code: message"` to the code-only form.
- Release blog for v0.0.107: a security fix, non-breaking while `LogOnly`; operators should plan the `Enforce` step.

## Contract and examples

```bash
curl -X POST "http://localhost:4201/api/v1/{domain}/workflows/{workflow}/instances/events?action=start" \
  -H "Content-Type: application/json" \
  -H "dapr-api-token: $APP_API_TOKEN" \
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
