# Spec 00: Docs Rebuild Master Spec

## Purpose

Define the documentation rewrite scope, writing standard, terminology, and definition of
done for the architecture-first docs set.

## Scope

In scope:

- Replace scattered top-level docs with a shallow, architecture-first information architecture.
- Preserve legacy docs under `docs/archive/` during migration.
- Use code as source of truth.
- Explain boundaries, contracts, failure modes, observability, and change safety.

Out of scope:

- Generated API reference.
- Class-by-class source commentary.
- Product marketing pages.
- Deep tutorials for every workflow definition feature.

## Information Architecture

```text
docs/
  README.md
  archive/
  architecture/
  domain/
  runtime/
  contracts/
  specs/
```

## Writing Template

Each durable page should include:

1. Purpose
2. Boundaries
3. Architecture/Flow
4. Contracts
5. Failure Modes
6. Observability
7. Change Safety
8. References

## Terminology

| Term | Meaning |
| --- | --- |
| Pipeline step | Ordered lifecycle unit in transition execution. |
| Pipeline profile | Trigger-specific filter that excludes irrelevant steps. |
| Directive | Typed instruction accumulated during a pipeline run. |
| Executor | Orchestration-side task runner that understands workflow context. |
| Invoker | Execution-side stateless binding runner. |
| Function handler | Specialized handler behind backend-driven function routes. |
| Domain cache context | Typed Redis-first cache boundary for workflow definition components. |
| Schema context | Current PostgreSQL schema selected for the domain/flow operation. |
| Remote app service | HTTP adapter for calling another vNext runtime. |

## Definition of Done

- New docs entry point exists.
- Legacy docs are preserved under `docs/archive/`.
- Architecture, domain, runtime, and contract pages exist.
- Each page lists code references.
- No page becomes a class inventory.
- Link check passes for local markdown links.
