# Example Session: Transition Throughput Change

## Task

- Title: Evaluate a transition persistence optimization
- Risk: High
- Problem: Reduce transition p95 latency without changing workflow semantics.
- Acceptance criteria: p95 target is met; no duplicate or lost state transitions; rollback is verified.

## Council

- Solution Architect
- Pragmatic Engineer
- Test & Evidence Agent
- Performance Engineer
- Data Architect
- Reliability Engineer
- Devil's Advocate

## Independent Proposals

- Architect: measure the persistence boundary and preserve aggregate ownership.
- Pragmatist: add the smallest reversible optimization behind a controlled rollout.
- Performance: establish p50/p95/p99 and database lock-wait baselines first.
- Data: verify transaction isolation, optimistic concurrency and migration compatibility.
- Reliability: require rollback, queue health and recovery evidence.
- Test: map every claim to a reproducible test or measurement.
- Devil's Advocate: challenge whether persistence is the measured bottleneck.

## Objection

- Claim: batching writes will reduce latency.
- Evidence gap: lock waits and serialization cost were not separated.
- Impact: batching could increase contention or change failure semantics.
- Response: `DEFER_TO_EXPERIMENT`.

## Decision

Run a representative benchmark and failure test first. Implement only if the measured bottleneck is the persistence boundary and the change preserves transition ordering, idempotency, observability, and rollback.

## Chair Decision

- Decision: `APPROVE_WITH_CONDITIONS`
- Conditions: before/after p95/p99, concurrency regression test, rollback rehearsal, and dashboard review.
