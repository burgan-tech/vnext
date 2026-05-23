# vNext Documentation Rebuild Plan (Architecture & Domain Focus)

This document provides an actionable plan to rebuild the `docs/` directory from scratch with a **minimal over-engineering** approach.

> Goal: instead of generating class-by-class technical reference docs, create a clear documentation set that explains architectural decisions, domain behavior, contracts, and dependency relationships.

---

## 1) Guiding Principles

- **Code is source of truth**: XML summaries and source code are the primary references; docs provide context, not duplication.
- **Architecture-first**: Every document should answer, “why does this structure exist?”
- **Contract-oriented**: Clearly describe service boundaries, input/output contracts, and error semantics.
- **Shallow hierarchy**: Maximum 2-level folder depth; avoid deep category trees.
- **One page, one concern**: Each page should focus on a single core responsibility.
- **Versioned rollout**: Deliver the rewrite with spec-based iterative PRs instead of one large PR.

---

## 2) Target Information Architecture (New `docs/`)

Proposed structure:

```text
docs/
  README.md
  archive/
    ... (legacy docs moved here during migration)
  architecture/
    system-overview.md
    dependency-map.md
    workflow-execution-pipeline.md
    gateway-routing-strategy.md
  domain/
    instance-data-merge-concept.md
    function-handler-architecture.md
  runtime/
    task-executors-and-invokers.md
    script-context-and-engine.md
    remote-app-service-architecture.md
  contracts/
    json-validation.md
    api-and-service-contracts.md
  specs/
    00-docs-rebuild-master-spec.md
    01-pipeline-and-domain-flow-spec.md
    02-runtime-execution-spec.md
    03-contracts-and-validation-spec.md
    04-routing-and-remote-integration-spec.md
    05-migration-and-deprecation-spec.md
```

Notes:
- `architecture/domain/runtime/contracts` sections are sufficient; no extra taxonomy.
- During migration, existing docs will not be deleted; they are first moved under `docs/archive/`.
- Existing feature-scattered docs will be migrated into this new backbone.

---

## 3) Work Breakdown: Master Plan → Specs

This work is split into 6 specs. Each spec can be delivered in a separate PR.

## Spec 00 — Docs Rebuild Master Spec

**Purpose**
- Define scope, principles, terminology, writing standard, and definition of done.

**Outputs**
- Documentation writing template (purpose / boundaries / sequence / contracts / failure modes).
- Terminology glossary (pipeline step, trigger, invoker, executor, handler, schema, etc.).
- ADR-lite format (for architecture decisions when needed).

**Done Criteria**
- All subsequent specs can reference this standard.

## Spec 01 — Pipeline & Domain Flow

**Scope**
- Workflow Execution Pipeline architecture.
- Function Handler architecture (its role in domain flow).
- DomainCacheContext architecture (domain-aware context management in cache layer).
- Domain view of instance lifecycle and state transitions.

**Key questions to answer**
- In what order do pipeline steps execute, and with which invariants?
- In which scenarios do Stop/Skip/Finalize directives apply?
- Which parts are synchronous vs eventually consistent?

**Done Criteria**
- A new team member can explain pipeline behavior with high accuracy without opening code first.

## Spec 02 — Runtime Execution

**Scope**
- Task Executors and Invokers.
- Script Context and Script Engine architecture.
- Interaction flow between Execution host and Orchestration host.

**Key questions to answer**
- What is the boundary between Executor and Invoker?
- Which data is guaranteed/safe in script context?
- How do retry/timeout/error-boundary behaviors map into execution runtime?

**Done Criteria**
- End-to-end runtime execution of a task type is understandable in one coherent section.

## Spec 03 — Contracts & Validation

**Scope**
- JSON validation approach.
- API and inter-service contracts (request/response/error envelope).
- Backward-compatibility rules for schema evolution.

**Key questions to answer**
- Which layer performs validation, and where is fail-fast enforced?
- How is error format kept stable for consumers?
- Which changes are considered breaking?

**Done Criteria**
- Consumer teams have a single, clear contract guide.

## Spec 04 — Routing & Remote Integration

**Scope**
- Gateway Routing Strategy (Local vs Remote).
- Remote App Service architecture.
- Discovery / resolution / fallback mechanics.

**Key questions to answer**
- How does the local-vs-remote decision tree work?
- How are idempotency, correlation, and observability handled in remote calls?
- Where are failover/retry policies implemented?

**Done Criteria**
- Routing behavior becomes operationally troubleshootable.

## Spec 05 — Migration & Deprecation

**Scope**
- Controlled move of old `docs/` content under `docs/archive/`, then selective migration into new IA.
- Redirect/deprecation notes for legacy pages.
- Documentation ownership and update cadence.

**Done Criteria**
- Legacy links are retired in a controlled way; new structure is the default entry point.

---

## 4) Delivery Strategy (Iterative PR Plan)

### PR-1 (Foundation)
- `specs/00` + new `docs/README.md` + folder skeleton.

### PR-2 (Core Domain)
- `specs/01` outputs: pipeline + function handler + DomainCacheContext + domain flow.

### PR-3 (Execution Runtime)
- `specs/02` outputs: executors/invokers + script engine/context.

### PR-4 (Contracts)
- `specs/03` outputs: JSON validation + contract stability.

### PR-5 (Routing & Remote)
- `specs/04` outputs: gateway routing + remote app service.

### PR-6 (Cleanup)
- `specs/05` outputs: migration, deprecation, link cleanup.

---

## 5) Document Template (Short Form)

Every new document should use this short template:

1. **Purpose**
2. **Boundaries**
3. **Architecture/Flow** (text + optionally one diagram)
4. **Contracts** (input/output/error + invariants)
5. **Failure Modes**
6. **Observability** (logs/metrics/tracing guidance)
7. **Change Safety** (breaking/non-breaking notes)
8. **References** (main code entry points)

---

## 6) Content Depth Rules (Anti-Over-Engineering Guardrails)

- Do not copy class or method listings into docs.
- Maximum one primary flow diagram per page (optional).
- Move non-critical variants/edge cases to an appendix.
- Provide usage examples only for critical contracts.
- Keep code snippets short; reference source files as the canonical detail.

---

## 7) External Discovery Plan (`burgan-tech/vnext-docs`)

Recommended use of the vNext docs portal:

1. Extract canonical behavior from this repository’s code first.
2. Use `vnext-docs` for terminology alignment and narrative consistency.
3. If there is a conflict, treat code as source of truth and document a “known divergence”.
4. Run a “source alignment” checklist at the end of each spec.

---

## 8) Acceptance Checklist (Per Spec)

- [ ] The page clearly states purpose and boundaries.
- [ ] Interface/contract table exists.
- [ ] Dependencies and call direction are explicit.
- [ ] Failure modes + observability notes are present.
- [ ] Breaking-change impact is marked when relevant.
- [ ] Content does not degrade into class-level reference documentation.

---

## 9) Immediate Next Step

After approval of this plan:
- Start with `Spec 00 + new docs README skeleton`.
- Then complete the highest-priority topic, **Workflow Execution Pipeline Architecture** (`Spec 01`), in a separate PR.
