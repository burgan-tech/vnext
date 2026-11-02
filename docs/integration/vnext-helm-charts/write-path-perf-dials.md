---
product: vnext-helm-charts
milestone: v0.0.102
status: pending
---

# Write-path performance dials — Handoff for vnext-helm-charts

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/1152
- Milestone: v0.0.102
- Origin: digital-investment performance review, 2026-11-02 (local measurements; no production numbers yet)

## What changed in the runtime

Four performance changes on the transition write path, all with code defaults that keep current
behaviour or improve it without configuration:

1. **HTTP task clients are pooled** (`SocketsHttpHandler`, infinite handler lifetime). The Execution
   host's named clients, previously hardcoded at `MaxConnectionsPerServer = 10`, now read a new
   `WorkflowHttpClient` section; Orchestration's `Workflow:TaskInvocation` section gained two pool
   dials. Defaults are equal on both hosts.
2. **Sub-item terminal lock wait** defaults changed from 4 × 120 ms to 10 × 20 ms
   (`WorkflowExecution:SubItemTerminalLockRetry`). No chart change unless an environment overrides it.
3. **InstanceData append** runs in two database round-trips instead of six. No configuration.
4. **Bookkeeping commits**: the task journal is written once per task; a new, default-off switch
   `WorkflowExecution:Bookkeeping:AsynchronousCommit` lets the journal and transition-record writes
   commit with `synchronous_commit = off`.

Source of truth: `vnext-meta/unreleased/http-client-pool-and-terminal-lock-wait.json`,
`instance-data-append-pipelining.json`, `bookkeeping-commits.json`; `docs/runtime/task-invocation-routing.md`.

## What this product must do

- Expose the new keys as overridable values (defaults below are the code defaults; the chart should
  not hard-code different ones):
  - Orchestration: `Workflow__TaskInvocation__MaxConnectionsPerServer` (50),
    `Workflow__TaskInvocation__PooledConnectionLifetimeSeconds` (600),
    `Workflow__TaskInvocation__PooledConnectionIdleTimeoutSeconds` (120).
  - Execution: `WorkflowHttpClient__MaxConnectionsPerServer` (50),
    `WorkflowHttpClient__PooledConnectionLifetimeSeconds` (600),
    `WorkflowHttpClient__PooledConnectionIdleTimeoutSeconds` (120).
  - Orchestration: `WorkflowExecution__Bookkeeping__AsynchronousCommit` (false).
- `RESOURCE_TUNING.md`: add the guidance below on `PooledConnectionIdleTimeoutSeconds` (keep it below
  the gateway's keep-alive timeout) and on when to enable `AsynchronousCommit`.
- PostgreSQL: recommend `commit_delay` (group commit, e.g. `1000` µs with the default
  `commit_siblings = 5`) for environments where the runtime's many small autocommit transactions
  contend. No durability loss. This is a database parameter, not a runtime key; document it where the
  chart documents the PostgreSQL deployment.

## Contract and examples

```yaml
orchestration:
  env:
    - name: Workflow__TaskInvocation__MaxConnectionsPerServer
      value: "50"
    - name: Workflow__TaskInvocation__PooledConnectionLifetimeSeconds
      value: "600"
    - name: Workflow__TaskInvocation__PooledConnectionIdleTimeoutSeconds
      value: "120"
    - name: WorkflowExecution__Bookkeeping__AsynchronousCommit
      value: "false"
execution:
  env:
    - name: WorkflowHttpClient__MaxConnectionsPerServer
      value: "50"
```

`AsynchronousCommit=true` trades three extra round-trips per bookkeeping write for no WAL fsync on
the request path, and accepts losing the last few hundred milliseconds of journal / transition-record
rows on a database crash (never instance state, data or outbox). It pays off where fsync latency
exceeds roughly three network round-trips to the database — measure with OpenObserve
(`Db.*` span durations) before enabling.

## Acceptance

- Both hosts start with the defaults and with every key overridden.
- With `AsynchronousCommit=true`, OpenObserve shows `SET LOCAL synchronous_commit = off` `Db.Query`
  spans around `InstanceTasks` / `InstanceTransitions` writes; with `false`, none.

## Out of scope

- Changing the code defaults; `commit_delay` tuning per environment (DBA).
