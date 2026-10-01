# Workflow Timeout

A workflow-level timeout is armed once at instance start and fires the virtual `$timeout`
transition. This page covers how the effective timeout is resolved (the parent's
`subFlow.overrides.timeout` versus the workflow's own), what the state body's `timeout` block
carries, why `$timeout` is resolved to a key at order 20 rather than 38, and the fact that
`timer.reset` is read nowhere.

## The `timeout` block and the effective-timeout resolver

- **`timeout` block**: `{ key, target, executeAtUtc, annotations }`, the workflow-level deadline armed for the
  polled instance. `annotations` is the effective timeout's (`timeout.annotations`); an override
  replaces it with the rest of the timeout, never merges. **Not** a `transitions[]` entry — a workflow timeout is instance-scoped, armed
  once at start, never re-armed, and keyed by the virtual `$timeout`, so it has no callable key and
  `TransitionItem` has no `target`. `key`/`target` come from the **effective** timeout, resolved by
  `InstanceMetadataExtensions.ResolveEffectiveTimeout` — the parent's `subFlow.overrides.timeout`
  when the instance carries one, else `workflow.Timeout`. **That one resolver is also what the arm
  (`InstanceCommandAppService`) and the fire path (`FlowTimeoutJobHandler`, `ApplyTimeoutStateStep`)
  call; do not reintroduce a second reading.** Before it existed the override was consumed only by
  the arm, so a child declaring `"timeout": null` had a job armed from the parent's timer that fired
  into `TimeoutConfigMissing` and did nothing. `executeAtUtc` is the persisted `InstanceJob.ExecuteAt`
  of the active `Timeout` row. Omitted when nothing resolves, when no job is armed, when `ExecuteAt`
  is null, or once the polled instance's **own** `Status.IsTerminal` — the cleanup that closes the
  job row is asynchronous, so the guard must not wait for it. Polled instance only, no subflow
  descent. Needs no fingerprint member (immutable instant + status-governed presence).

## `$timeout` is resolved at order 20

- **`$timeout` is resolved to a key at order 20, not 38.** `CreateTransitionRecordStep` turns the
  virtual key into the audit record's transition key, and it must do so through the **effective**
  timeout for the same reason the target does. `Workflow.ResolveWellKnownKey` *throws*
  `TimeoutNotConfiguredForWorkflowException` when the workflow has no timeout of its own — so before
  this was fixed, an override-driven child died at step 20, three steps before `ApplyTimeoutStateStep`
  could have resolved anything. Worse than not firing: `SetBusy` (19) had already committed, so the
  child sat **Busy in its waiting state forever** with its job row marked processed. Measured on the
  bench, twice, by `timeout-lab`. `ResolveWellKnownKey` itself is deliberately untouched — it is a
  definition-level method with no instance in scope and is right about what it can see; the
  instance-aware answer belongs at the call sites that hold `context.Instance`.

## `timer.reset` is read nowhere

- **`timer.reset` is read nowhere.** A workflow timeout always means "not finished within `duration`,
  from instance start", never "idle for `duration`", whatever the definition declares. An idle
  timeout needs a per-state scheduled transition (`triggerType: 2`), which IS cancelled and re-armed
  on state entry.

## Related

- [Instance Function Cache and Fingerprint ETag](state-function-cache-and-etag.md)
- [SubFlow Overrides](../domain/subflow-overrides.md) — `subFlow.overrides.timeout`.
- [Workflow Execution Pipeline](../architecture/workflow-execution-pipeline.md) — `CreateTransitionRecordStep` (20), `ApplyTimeoutStateStep` (38).
