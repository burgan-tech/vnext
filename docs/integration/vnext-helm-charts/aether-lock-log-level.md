---
product: vnext-helm-charts
milestone: v0.0.102
status: pending
---

# Aether Dapr lock log level — Handoff for vnext-helm-charts

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/1142 (issue #1132)
- Milestone: v0.0.102

## What changed in the runtime

The orchestration host's `appsettings.json` now sets the logging category
`BBT.Aether.DistributedLock.Dapr.DaprDistributedLockService` to `Error`.

Aether logs `Failed to acquire lock for resource …` at Warning on every unsuccessful lock attempt.
That is contention, not failure, and it was most of the Warning volume reported in #1132. vNext
already logs retries at Debug and lock exhaustion once at Warning (`InstanceLockFailed`), so nothing
is lost.

Source of truth: `docs/runtime/event-publish-modes.md` § Observability contract, bullet "Aether's
per-attempt lock log".

## What this product must do

- If the chart sets per-category variables (`Logging__LogLevel__<category>`), do nothing: the
  appsettings entry stays in force.
- If the chart replaces the whole `Logging` section for the orchestration deployment (a mounted
  `appsettings.*.json` or a full `Logging` block), add the same category there. Otherwise the Warning
  flood returns in that environment.
- No change for the execution, inbox or outbox deployments. Only orchestration takes the sub-item
  lock.

## Contract and examples

```yaml
env:
  - name: Logging__LogLevel__BBT.Aether.DistributedLock.Dapr.DaprDistributedLockService
    value: "Error"
```

## Acceptance

- In an environment running the new runtime, OpenObserve or Elastic shows no `Failed to acquire lock
  for resource` Warnings from orchestration. `InstanceLockFailed` still appears if a lock is genuinely
  exhausted.

## Out of scope

- The Aether-side fix: logging a failed `TryAcquireLockAsync` attempt at Debug. It is proposed to
  Aether separately.
