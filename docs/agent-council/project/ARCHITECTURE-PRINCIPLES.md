# Architecture Principles

1. Components have explicit responsibilities and data ownership.
2. API, orchestration, execution and monitoring boundaries remain explicit.
3. External contracts are versioned and observable.
4. Workflow/orchestrator state is the single source of truth for process state.
5. Agents reason and propose; the workflow system owns state and policy.
6. Retried operations are idempotent or have explicit compensation.
7. Timeout, retry, cancellation and compensation ownership is explicit.
8. Security uses default deny, least privilege and tenant isolation.
9. Performance decisions require a representative baseline and comparison.
10. New infrastructure requires an owner, operational plan and rollback.
11. Critical changes require rollback or controlled rollout.
12. Equivalent value should use the simpler reversible design.
13. Runtime changes follow current code and vNext pipeline conventions.

## Project-specific hard constraints

- Do not bypass Aether cross-cutting facilities.
- Do not introduce EventHook infrastructure; distributed events use the current outbox contracts.
- Do not add unnecessary repository includes or cross-layer dependencies.

## Approved technology preferences

- .NET, ASP.NET Core and Aether for runtime services.
- PostgreSQL for authoritative workflow persistence.
- Dapr for service invocation and pub/sub where the existing boundary requires it.

## Restricted approaches

- Unbounded retries or non-idempotent side effects.
- Runtime behavior changes without focused tests and required integration evidence.
- New infrastructure without ownership, observability and rollback.
