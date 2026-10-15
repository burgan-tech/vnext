# SubFlow Transition Proxy

A transition that arrives at a parent with an active `S` SubFlow is **proxied** to that SubFlow
instead of being queued on the parent. The parent takes no lock, flips no Busy flag and enqueues no
job; the call goes straight down the active-correlation chain and is admitted, validated and run
**at the leaf**, in the leaf's own scope. This replaced the accept-time chain reserve
(`SubflowProxyService`, vnext#101); the old behaviour survives only on the receiving side, see
[Backward compatibility](#backward-compatibility).

## Rules

- **Forwardable.** `SubflowProxyService.TryProxyAsync` proxies only when all of these hold; otherwise
  it returns "not proxied" and the transition runs on the parent as before:
  - the parent's execution snapshot has an active `S` SubFlow (`InstanceExecutionSnapshot.ActiveSubFlow`,
    the oldest open `S` correlation);
  - the key classifies as `AdmissionKind.Normal`: `updateData`, `cancel`, `exit` (alias or configured
    key) and the timeout key are never proxied;
  - the key is not a **parent shared transition that is available in the parent's current state**
    (`SubFlowBypassSpecification.IsParentSharedTransitionAndAvailable`). A shared transition that is
    *not* available in the current state is forwarded (the leaf normally answers 400).
  The pipeline's `ForwardToActiveSubflowStep` / `SubflowForwardRule`, `FileAdmission` and
  `AuthorizeAppService` use the same predicate, so they all agree on who owns the key.
- **The parent resolves the mode once** (`TransitionRequestMode.Resolve`): transition `executionType`
  beats flow `executionType` beats the caller's `sync`. The child is called in that mode. A
  runtime-internal relay (`SuppressResponseEnrichment`) keeps the mode decided above it, so every
  level of a chain uses the same one. See [Execution Type](../runtime/execution-type.md).
- **Response.** `202` (async) or `200` (sync) carrying the **parent's** id and the **child's** status.
  A sync response is enriched from the parent as before (attributes, ETag); async and internal relays
  answer identity only. If a sync child completed, the parent's fresh status is re-read.
- **Nothing is written on the parent.** No status lock, no Busy flag, no job. Only the leaf runs
  [admission](../runtime/status-locking.md): its own lock, validation, Busy flip and job.
- **Recursion per level.** The child's `TransitionAsync` runs the same check, so an intermediate
  level proxies again and the call reaches the deepest active SubFlow.
- **Async pre-stamp.** Before forwarding an async request the parent's raw `EffectiveStatus` is stamped
  `Busy` (own `RequiresNew` UoW), so a state long-poll on the parent never reads a stale `Active` after
  the 202. If the forward fails or throws, the stamp is reverted with a single-column compare-and-set
  (`TryCompareAndSetEffectiveStatusAsync`, expected `Busy`); no revert when the child answered its own
  `InstanceBusy`, when the prior value was already `Busy`, or when the pre-stamp itself failed. A stamp
  failure is logged (`SubFlowProxyEffectiveStatusWriteFailed`) and never fails the request. Sync mode
  stamps nothing; the child's rest-point `sub:state-changed` relay owns the projection.
- **Completion window and missing child.** If the child answers `InstanceCompleted` or
  `InstanceNotFound` (the SubFlow ended while the parent correlation is still open), the proxy maps it
  to `409 InstanceBusy` naming the **parent** at every level. Any other child error is returned as is.
- **`TrustedPayload` passes through** to the child (local hops only: a DirectTrigger body that lands on
  a parent stays trusted at the leaf). Headers are rebuilt with the parent-instance-id header; route
  values, termination, actor and correlation id are copied.
- **x-storage swap happens at the leaf.** Intermediate levels never swap file content
  (`FileAdmission` skips them via the same forward predicate), so the object is written once, for the
  instance that owns the data.
- **Trace.** The proxy opens a `Transition.Proxy` span, enters a child lane like
  `ForwardToSubflowJobHandler`, and calls through `ISubflowForwardingService` (`SubFlow.Forward` span).

## Backward compatibility

Older runtimes still reserve the chain on an async accept and relay with a claim. This runtime keeps
the receiving side and removes only the sending side:

- A request with `ChainReserved=true` (relay from an older parent) is **not proxied**: it keeps the
  owner re-entry admission at the leaf. The leaf runs the x-storage file swap for it
  (`WorkflowExecutionContext.ChainReservedRelay`, set only by `BuildTransitionContext`), but not the
  schema check, exactly as before.
- `ForwardToActiveSubflowStep` still passes the inherited claim for a parent job an older accept
  enqueued across an upgrade; `TransitionRunner` still releases a chain reserved by an older accept
  when such a job fails. For every request this runtime admits the claim is false and the release a no-op.
- `PUT .../instances/{id}/busy` (chain propagation) and `PUT .../internal/busy-release` are
  **LEGACY**: kept so older parents keep working, marked in code, no longer called by new accepts.
- Deprecation id `subflow-chain-reserve-claim` in `vnext-meta/deprecations.json` (since 0.0.100,
  removal no earlier than 0.0.103). Mixed-version domains need no action.

The reserve itself is described on the [LEGACY page](subflow-chain-reserve.md).

## Known limitations

- **Proxy-declined async race.** If the snapshot has no active SubFlow but the loaded aggregate does
  (a race), the request falls to the pipeline and is forwarded by a parent job. Nothing is stamped
  before the 202, so a long-polling client may briefly see the leaf `Active`; a leaf failure then
  surfaces as a fault after the 202, not as the 409.
- **Overlapping async requests.** A second request that pre-stamps between the first request's stamp and
  its revert makes the compare-and-set revert to the prior value while the other request is in flight;
  the column corrects at the child's next notification.
- **Cross-domain `TrustedPayload`** is not carried; only same-domain hops keep it.
- **A correlation to a never-created child** answers `InstanceNotFound` from the leaf, which maps to
  `409 InstanceBusy` on every request, so the caller retries forever. This is a data defect, not a
  runtime one.

## Related

- [Subflow Execution](subflow-execution.md)
- [Status Locking](../runtime/status-locking.md)
- [Execution Type](../runtime/execution-type.md)
- [Workflow Execution Pipeline](workflow-execution-pipeline.md)
