---
product: vnext-docs
milestone: v0.0.102
status: pending
---

# Row security (`attributes.rowSecurity`) — Handoff for vnext-docs

## Context

- vNext PR: the row-security PR of milestone v0.0.102 (burgan-tech/vnext#1107 is the issue)
- Milestone: v0.0.102
- Issue: <https://github.com/burgan-tech/vnext/issues/1107>

## What changed in the runtime

Flows can restrict which instances a caller sees with role-keyed row rules
(`attributes.rowSecurity`). Covered reads: the instance list (with `groupBy` and aggregations), the single
instance GET and the GetInstances / GetInstance tasks. Every other read is unfiltered (documented known
gap). Behaviour change for every flow: these reads now fail when the flow definition cannot be loaded
(`Instance:100052` 409, `Instance:100053` 503, `Instance:100051`). Source of truth:
[docs/domain/row-security.md](../../domain/row-security.md); vnext-meta fragment `row-security`.

## What this product must do

- A technical page for domain authors: contract, parameters, identity rules, deny-by-default, evaluation
  model (OR of matched rules, AND with the caller's filter, failures never widen).
- A client-consumption note: an excluded instance answers the same `404` as a missing one on GET; list
  counts cover visible rows only.
- A security note: the gateway must strip or overwrite the role header and every header a rule reads;
  query parameters are client-controlled; the unfiltered surfaces (state/data/view functions, history,
  transitions) are not an access boundary.
- A release/upgrade note for the lookup-failure behaviour change and the operator action
  (default-version pointers must resolve, version registry reachable, watch warning 20509).

## Contract and examples

See [row-security § Contract](../../domain/row-security.md#contract) and
[§ Lookup failure](../../domain/row-security.md#lookup-failure).

## Acceptance

- The portal page links the contract, the known gap and the gateway precondition, and matches the
  runtime doc on deny-by-default and on the lookup-failure errors.

## Out of scope

- Phase 1b (row security on instance-addressed functions) — not scheduled.
