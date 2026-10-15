# Accept-Time Chain Reserve (LEGACY)

**Retired as a sending behaviour.** Parents no longer reserve the SubFlow chain on an async accept;
they proxy forwardable transitions to the active SubFlow. Read
[SubFlow Transition Proxy](subflow-transition-proxy.md) for the current design.

This page remains because older-version runtimes still use the mechanism, and this runtime still
honours it on the receiving side (deprecation `subflow-chain-reserve-claim`, removal no earlier than 0.0.103).

## What the old reserve did

An async transition on a parent with an active SubFlow marked the whole active-correlation chain Busy
down to the leaf (`ReserveSubflowChainAsync` → `MarkBusyWithPropagationAsync`, not the `Try…` variant)
before the 202, so a client long-polling the parent never read a stale `Active`. The relay then claimed
that reserve through `TransitionInput.ChainReserved` → `IsPreReserved` → `AdmissionKind.OwnerReentry`
(`TransitionJobPayload.SubflowChainReserved` → `ForwardToSubflowJob.ChainReserved`); cross-domain it rode
the body of the internal-only `POST .../internal/subflow-forward`, never a public header. The sync path
never reserved. Compensation (`ReleaseSubflowChainAsync`, `PUT .../internal/busy-release`) released only
what the reserve flipped.

## What this runtime still does

- Accepts `ChainReserved` on a relay and treats it as an owner re-entry at the leaf; the relay is not
  proxied and gets the x-storage swap (`ChainReservedRelay`) but not the schema check.
- Keeps `PUT .../busy` and `PUT .../internal/busy-release` (LEGACY).
- Passes the inherited claim on for in-flight parent jobs from an older accept, and releases the chain on
  failure of such a job (`TransitionRunner`, E31). For new requests this is a no-op.
- Never reserves on its own accept: `ITransitionAdmissionService.ReserveSubflowChainAsync` has no caller.

## Related

- [SubFlow Transition Proxy](subflow-transition-proxy.md)
- [Subflow Execution](subflow-execution.md)
- [Status Locking](../runtime/status-locking.md)
