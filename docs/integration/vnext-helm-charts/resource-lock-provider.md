---
product: vnext-helm-charts
milestone: v0.0.105
status: pending
---

# Resource-lock provider selection — Handoff for vnext-helm-charts

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/1143
- Milestone: v0.0.105

## What changed in the runtime

Transition-level resource locks were served by one implementation only: Dapr's lock building block
(`DaprResourceLockService`). The runtime now also ships a Postgres lease implementation
(`NpgsqlResourceLockService`), and which one is used is chosen by configuration:

```
ResourceLock:Provider = dapr | postgres        (default: dapr)
```

Two things matter for the chart:

1. **The default is unchanged.** A deployment that sets nothing keeps the Dapr provider and behaves
   exactly as before, so no chart change is required to upgrade.
2. **An unknown value now fails startup.** Previously a typo fell through to Dapr silently; the host
   throws on any non-empty value other than `dapr` or `postgres`. A chart that templates this key
   from a values file must not emit an empty-but-present placeholder or a misspelling.

The Postgres provider leases rows in `sys_queues."DistributedLocks"` with a `wfres:` key prefix and
needs no Dapr lock component; the Dapr provider still reads the store name from
`DAPR_LOCK_STORE_NAME`.

Source of truth: `WorkflowApiBaseServiceCollectionExtensions.AddResourceLock(services,
configuration, lockStoreName)` and `vnext-meta/unreleased/resource-lock-conflict-routing.json`.

## What this product must do

- Expose `ResourceLock:Provider` as an optional chart value (env `ResourceLock__Provider`), defaulting
  to unset so the runtime default applies.
- Make sure the template omits the env var entirely when the value is empty, rather than rendering an
  empty string — an empty value is treated as "not configured" today, but an accidental whitespace or
  typo is a startup failure by design.
- For a deployment that opts into `postgres`: no Dapr lock component is required for resource locks,
  but `ConnectionStrings:Default` must be present — the host throws at startup without it. The
  existing database connection is what the provider leases against. `DAPR_LOCK_STORE_NAME` stays
  relevant only for the Dapr provider and for Aether's own distributed lock.

## Contract and examples

```yaml
# values.yaml — opt in to the Postgres lease provider
resourceLock:
  provider: postgres        # omit or leave empty to keep the Dapr provider
```

```yaml
# rendered env, only when a value is set
- name: ResourceLock__Provider
  value: "postgres"
```

Startup failure on a bad value (expected, not a regression):

```
InvalidOperationException: Unknown ResourceLock:Provider 'postgress'.
Supported values are 'dapr' (default) and 'postgres'.
```

`postgres` additionally requires a connection string; without it the host also refuses to start:

```
InvalidOperationException: ConnectionStrings:Default is required when ResourceLock:Provider is 'postgres'.
```

## Acceptance

- A chart rendered with no `resourceLock.provider` produces no `ResourceLock__Provider` env var, and
  the host starts and acquires locks through Dapr as before.
- A chart rendered with `provider: postgres` starts, and a flow carrying a `resourceLock` transition
  acquires and releases without a Dapr lock component present.
- A chart rendered with a misspelled provider fails the pod's startup with the message above, rather
  than silently running on Dapr.

## Out of scope

- Choosing a provider per environment is a deployment decision, not a chart default; the chart only
  needs to make the key settable.
- The `sys_queues."DistributedLocks"` table is created by the runtime's own migrations; the chart does
  not provision it.
