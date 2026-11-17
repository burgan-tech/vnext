---
product: vnext-helm-charts
milestone: v0.0.107
status: pending
---

# Sub-endpoint gate and runtime-caller key ring — Handoff for vnext-helm-charts

## Context

- vNext PR: (link added when the PR opens) — branch `1265-enhancement-add-a-role-gate-to-sub-start-and-complete-endpoints`
- Milestone: v0.0.107
- Issue: <https://github.com/burgan-tech/vnext/issues/1265>
- Decision: council `2026-11-17-sub-endpoint-role-gate` (APPROVE_WITH_CONDITIONS, Awaiting Chair)

## What changed in the runtime

The runtime-internal endpoints `POST {domain}/workflows/{workflow}/sub/instances/start` and
`POST {domain}/workflows/{workflow}/instances/{instance}/complete` can now be put behind an optional,
domain-configured gate.

- **Default.** The gate is `Off`, and every key below is optional. A chart that sets nothing gets exactly today's behaviour.
- **Ingress strip.** The Orchestration host removes the request header `x-vnext-runtime-token` from every request. This happens even with the gate Off.
- **Runtime calls.** The runtime proves its own calls to these endpoints with a short-lived HMAC token. The token is signed with a **key ring read from a mounted file**, which the three hosts that sign or verify share: Orchestration, Execution and Inbox.
- **Why a file.** The key must not travel in env or configuration, because flow scripts can read both. The key ring therefore has to be a mounted Secret.

Source of truth:
- `src/BBT.Workflow.HttpApi.Shared/RuntimeCaller/RuntimeCallerOptions.cs`
- `src/BBT.Workflow.Application/Authorization/SubEndpoints/SubEndpointGateOptions.cs`
- `docs/runtime/sub-endpoint-gate.md`
- vnext-meta `engine.subEndpointGate`

## What this product must do

- **Secret.** An optional, environment-wide Kubernetes Secret holding the key ring. It is opt-in, off by default, and **one ring per environment, shared by every domain** — pending council condition C-3 (that a shared ring across domains is acceptable in that environment).
- **Mount and env.** When the Secret is enabled, mount it read-only (mode `0400`) into the **Orchestration, Execution and Inbox** containers of every domain. Do not mount it into Outbox or DbMigrator. Set on those three containers:
  - `Workflow__RuntimeCaller__KeyFile=<mount path>`;
  - `Workflow__RuntimeCaller__Required=true`.
- **Gate values.** Per domain, Orchestration only, all optional:
  - `Workflow__SubEndpointGate__Mode` (`Off` | `Observe` | `Enforce`);
  - `Workflow__SubEndpointGate__CompleteMode`;
  - `Workflow__SubEndpointGate__Grants__N__Role` / `__Grant`.
  - Keep the default `Off`.
- **No key material in values or env.** Startup fails when a key- or secret-named entry appears under `Workflow__RuntimeCaller__`.
- **Redaction list.** If a values file overrides `Telemetry__Logging__Body__AdditionalSensitiveHeaderNames` for Orchestration, it must keep `x-vnext-runtime-token` in the list.
- **Script sandbox.** On Orchestration, keep `Scripting__Sandbox__Enabled=true` whenever the ring is mounted. Startup fails otherwise, unless the Chair-gated `Workflow__RuntimeCaller__AllowWithoutScriptSandbox=true` is set.
- **Rotation.** Document it as restart-based:
  1. add the new key to the ring everywhere;
  2. move `activeKeyId` everywhere;
  3. remove the old key.

## Contract and examples

Key-ring file (the Secret's single key):

```json
{ "activeKeyId": "k2", "keys": { "k2": "<base64, at least 32 bytes>", "k1": "<base64>" } }
```

Runtime-only gate on one domain (Orchestration env):

```
Workflow__SubEndpointGate__Mode=Enforce
Workflow__SubEndpointGate__Grants__0__Role=$Everyone
Workflow__SubEndpointGate__Grants__0__Grant=deny
```

Errors when the gate refuses: 403 `Authorization:110001`. When the role provider fails for a caller without a token: 503 `Authorization:110006`.

## Acceptance

- With the Secret disabled, rendered manifests are identical to today's (the header strip lives in the image, not the chart).
- With the Secret enabled, every Orchestration, Execution and Inbox pod logs `Runtime-caller identity loaded. ActiveKeyId: <id>` (EventId 50047) at start, with the same id everywhere.
- A pod without the mount, but with `Required=true` set, fails to start.

## Out of scope

- Turning the gate on in any environment. That follows the enable order in `docs/runtime/sub-endpoint-gate.md` and needs council conditions C-3 and C-4 closed.
- Per-domain keys. A shared ring is the v1 contract.
