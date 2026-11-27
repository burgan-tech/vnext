---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# A Faulted instance can be retried after a failed mapping and closed with the reserved cancel — Handoff for vnext-docs

## Context

- vNext PR: burgan-tech/vnext — `fix(instances): make mapping- and schema-faulted instances recoverable`
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1252 (source: https://github.com/burgan-tech/vnext-client-sdk-core/issues/136 and #119)

## What changed in the runtime

- A transition that fails in its **first step** — its mapping throws, the data it produces fails the
  master schema, or the instance key it sets is already taken (`Instance:100019`) — still faults the
  instance, but now keeps an **unfinished record** of that hop. Before, it left none.
- `POST …/instances/{instance}/retry` re-runs such a hop (`200`, the instance continues). Before it
  answered `400 Instance:100028`. The re-run uses the **retry's** body; the failed request's body is
  not replayed. Covered: manual transitions, shared transitions, `updateData` and the reserved
  `cancel`. Not covered: automatic, scheduled and event hops (burgan-tech/vnext#1338) — an automatic or
  scheduled transition cannot declare a mapping anyway.
- The reserved `cancel` (`PATCH …/instances/{instance}/transitions/{cancel key}`) is **accepted on a
  Faulted instance**. Before it answered `400 Instance:100018`. It resolves the instance's incidents,
  closes the failed hop's record, cancels the instance (`C`) and its Faulted SubFlow children. It always
  runs inline: `200` with the final status, even with `sync=false`.
- Every other key — `exit`, `updateData`, normal and shared transitions — still answers
  `400 Instance:100018` on a Faulted instance. A cancel whose `availableIn` excludes the current state
  is still refused (`Transition:100024`).
- The sync answer of a failed `updateData` or `cancel` says `"status": "F"`. Before it said `A` while
  the instance was Faulted.
- Up to v0.0.106 such an instance could only be recovered with a database edit. (Builds before v0.0.104
  happened to leave a failed manual transition Busy, which the reserved cancel could close; v0.0.104
  removed that accident.)

Source of truth: `vnext-meta` migration `faulted-instance-retry-and-cancel` and known issue
`parent-faulted-by-subflow-child-not-retryable`; runtime docs `docs/contracts/api-and-service-contracts.md`,
`docs/domain/well-known-transitions.md` and `docs/runtime/instance-incident-persistence.md`.

## What this product must do

- `docs/api-reference/rest-api.md` § `POST …/retry`: retry also re-runs a transition that failed in its
  mapping, master-schema check or key check; the retry's body is used; the list of covered transition
  kinds and the #1338 exclusion.
- `docs/concepts/incidents.md` § Retry semantiği: the same, plus "the reserved `cancel` closes a Faulted
  instance and resolves its incidents; the incidents stay listed as resolved".
- `docs/components/workflow.md` § Cancel Transition (and `i18n/en` § Cancel): the reserved cancel is
  accepted on a Faulted instance, runs inline, cancels Faulted SubFlow children; no other key is.
- `docs/components/workflow.md` § updateData (and `i18n/en`): a failed `updateData` answers
  `"status": "F"`.
- Release notes for v0.0.107: name the change and that it is the first release with an API exit for
  these instances.
- Known limit (from the known issue): a SubFlow parent faulted by its child's own fault cannot be
  retried (`400 Instance:100028`); cancel it, and cancel the child too.

## Contract and examples

Retry a transition whose mapping failed, with a corrected body:

```http
POST /api/v1/{domain}/workflows/{workflow}/instances/{instance}/retry?sync=true
{ "attributes": { "note": "corrected" } }

200 { "id": "…", "status": "A", "retriedTransitionId": "…" }
```

Retry an `updateData` that set a key another instance holds, with a free key:

```http
POST /api/v1/{domain}/workflows/{workflow}/instances/{instance}/retry?sync=true
{ "key": "<free key>", "attributes": {} }
```

Close a Faulted instance:

```http
PATCH /api/v1/{domain}/workflows/{workflow}/instances/{instance}/transitions/cancel?sync=false
{ "attributes": {} }

200 { "id": "…", "status": "C" }
```

| Case | Up to v0.0.106 | v0.0.107 |
|---|---|---|
| `retry` after a mapping / schema / key failure | `400 Instance:100028` | `200`, the hop re-runs |
| reserved `cancel` on a Faulted instance | `400 Instance:100018` | `200`, `status: C` |
| any other key on a Faulted instance | `400 Instance:100018` | unchanged |
| sync answer of a failed `updateData` / `cancel` | `status: A` | `status: F` |

## Acceptance

- The retry section names the three first-step failures and says that the retry's body is used.
- The cancel section says that it is the only transition a Faulted instance accepts.
- No page says that a Faulted instance cannot be cancelled.

## Out of scope

- Retry of automatic, scheduled and event hops (burgan-tech/vnext#1338).
