---
product: vnext-helm-charts
milestone: v0.0.107
status: pending
---

# Dapr app API token as a mounted Secret — Handoff for vnext-helm-charts

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/1320
- Milestone: v0.0.107
- Issue: <https://github.com/burgan-tech/vnext/issues/1253>
- Related handoff: [Sub-endpoint gate](sub-endpoint-gate.md) (shares the script-sandbox condition)

## What changed in the runtime

The orchestration host guards the Dapr-triggered endpoints (events, schedule, job) so that only its own
sidecar can call them. The guard compares the `dapr-api-token` header with an app API token.

- **Source of the token.** Only a file. The app reads the path in `Workflow:RuntimeCaller:AppApiTokenFile` (env `Workflow__RuntimeCaller__AppApiTokenFile`) once at start and trims the content. It is never reloaded.
- **Not read any more.** The app ignores `APP_API_TOKEN`. `Dapr:LocalDelivery:AppApiToken` no longer exists.
- **Why a file.** Flow scripts can read env and configuration, so the token must not travel there.
- **Startup fails when:**
  - the path is set and the file is missing, unreadable or empty;
  - another token-, key- or secret-named entry is set under `Workflow:RuntimeCaller` (the known keys are excepted);
  - the path is set on Orchestration with `Scripting:Sandbox:Enabled=false`, unless `Workflow:RuntimeCaller:AllowWithoutScriptSandbox=true` (Critical log at every start).
- **Path unset.** `LogOnly` logs EventId 40998 once at startup. `Enforce` fails startup (the pod never becomes ready), so the chart must never render `Enforce` without the token file path.
- **daprd** still takes the token from its own `APP_API_TOKEN` env. On Kubernetes that is the annotation `dapr.io/app-token-secret`.

Source of truth:
- `docs/domain/event-driven-workflows.md` § Who can call it
- vnext-meta migration `dapr-local-delivery-guard`
- `src/BBT.Workflow.HttpApi.Shared/Middlewares/DaprLocalDelivery/`

## What this product must do

- **Secret per domain.** Name it `<release>-app-api-token`, key `token`.
  - Take the value from `lookup`, so an existing Secret is reused; otherwise `randAlphaNum 64` on first install.
  - Never put the value in values files.
  - The value must not end in a newline. The app trims it, but daprd sends `APP_API_TOKEN` raw as a header value and Go rejects CR/LF. `randAlphaNum` is safe; a hand-made `--from-file` may not be.
- **Annotation.** Set `dapr.io/app-token-secret: <that secret>` on the **orchestration** Deployment only.
- **Mount.** Mount the same Secret read-only into the orchestration container at a fixed path, and set `Workflow__RuntimeCaller__AppApiTokenFile` to that path.
  - The image runs as the non-root `workflowuser` and a Secret's files are owned by root. Set the pod's `securityContext.fsGroup` and the volume's `defaultMode: 0440`. A plain `0400` without `fsGroup` is unreadable and the pod fails to start (same rule as in the sub-endpoint gate handoff).
- **Remove `APP_API_TOKEN`.** No `APP_API_TOKEN` in the app container's env.
- **Scope of the Secret.** Every replica of a domain shares the Secret; each domain has its own.
  - "One Secret per domain" means one per orchestration Deployment (host set): domains co-hosted on one host set (`--with` locally, one Deployment) share its sidecar and therefore its token. Never reuse the environment-wide key ring of the sub-endpoint gate.
- **Rotation.** Change or delete the Secret, then `rollout restart` the orchestration Deployment. There is no in-place reload.
- **Script sandbox.** Keep `Scripting__Sandbox__Enabled=true` on Orchestration. This is the same condition as in [Sub-endpoint gate](sub-endpoint-gate.md).
- **Sidecar API.** Keep `dapr.io/sidecar-listen-addresses` on its loopback default (`[::1],127.0.0.1`).

## Contract and examples

```yaml
metadata:
  annotations:
    dapr.io/app-token-secret: <release>-app-api-token   # orchestration Deployment only
spec:
  template:
    spec:
      securityContext:
        fsGroup: <workflowuser gid>
      volumes:
        - name: app-api-token
          secret:
            secretName: <release>-app-api-token
            defaultMode: 0440
      containers:
        - name: orchestration
          env:
            - name: Workflow__RuntimeCaller__AppApiTokenFile
              value: /run/secrets/vnext/app-api-token/token
          volumeMounts:
            - name: app-api-token
              mountPath: /run/secrets/vnext/app-api-token
              readOnly: true
```

## Acceptance

- `helm upgrade` run twice leaves the token unchanged.
- The orchestration container env has no `APP_API_TOKEN`.
- A pod whose token file is missing or empty fails to start.
- EventId 40996 is zero in `LogOnly` before the mode is switched to `Enforce`.

## Out of scope

- Switching any environment to `Enforce`.
- The execution, inbox and outbox hosts (a later phase).
