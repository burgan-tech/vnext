# Accept-Time Chain Reserve

When an async transition arrives at a parent that has an active SubFlow, the accept marks the whole
active-correlation chain Busy down to the leaf before answering 202, and the relay that later carries
the transition to the leaf claims that reserve. This page explains why the reserve exists (the client
only ever observes the leaf), how the claim is threaded, why the sync path deliberately does not
reserve, and how compensation releases only what the reserve flipped.

## Rules

- **The client only ever observes the leaf.** The state function walks the active-correlation chain
  and reports the **deepest** active subflow's status (`InstanceQueryAppService`,
  `Status = subFlowStateInfo.Status`). A parent holding an open SubFlow correlation is `Busy` for that
  subflow's entire lifetime by design (`Instance.AddCorrelation` → `Busy()`; `CompleteCorrelation`
  deliberately does not clear it), so an ancestor's Busy carries **no** information about in-flight work.
- Therefore `AsyncTransitionStrategy` calls `ITransitionAdmissionService.ReserveSubflowChainAsync`
  on the `IsSubflowForward` branch — marking the chain down to the leaf **before** the 202 commits.
  Without it the accept answers while the leaf still reads `Active`, and a client long polling on the
  parent concludes nothing is in progress and stalls the flow.
- It uses `MarkBusyWithPropagationAsync`, **not** the `Try…` variant: the latter short-circuits on
  `AlreadyBusy` (its 409 contract, pinned by tests) and would never reach the leaf. Do not "unify" them.
- **The relay must then claim that reserve**, or the leaf rejects it with `Instance:100031` for being
  Busy — the Busy the accept just set. The claim is `TransitionInput.ChainReserved` →
  `WorkflowExecutionContext.IsPreReserved` → `AdmissionKind.OwnerReentry`, threaded
  accept → `TransitionJobPayload.SubflowChainReserved` → `ForwardToActiveSubflowStep` →
  `ForwardToSubflowJob.ChainReserved` → `ForwardToSubflowJobHandler`.
- **Never claim a reserve that was not taken.** The flag is narrower than `IsPreReserved` (which every
  job re-entry sets) precisely so a sync-origin or cancel/exit/timeout relay cannot barge past a leaf
  that is Busy for its own reasons.
- Cross-domain hops go through the internal-only `POST .../internal/subflow-forward`, whose body
  carries the claim. Not the public transition endpoint: it copies caller headers unfiltered, so a
  claim routed through it would be forgeable.
- **The sync path deliberately does not chain-reserve** (`TransitionPipeline`, `IsSubflowForward`
  branch): a blocking caller cannot observe a stale `Active`, so it would only widen stranded-Busy.
- Compensation: `ReleaseSubflowChainAsync` → `ReleaseWithPropagationAsync` (and internal
  `PUT .../internal/busy-release` cross-domain) releases **only what the reserve flipped** — levels
  with an open SubFlow correlation are recursed past, not settled, so effectively just the leaf.

## Related

- [Subflow Execution](subflow-execution.md)
- [Status Locking](../runtime/status-locking.md)
