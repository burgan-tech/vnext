# vNext Runtime Documentation

This directory is the architecture-first documentation set for the vNext workflow runtime.
It explains why the runtime is structured the way it is, how the main domain flows behave,
and which contracts consumer teams can rely on.

The source code remains the canonical reference for implementation details. These pages
describe the stable mental model, boundaries, failure modes, and change-safety rules.

## Start Here

| Area | Purpose |
| --- | --- |
| [Architecture](architecture/system-overview.md) | Runtime shape, service boundaries, dependency direction, routing. |
| [Domain](domain/instance-data-merge-concept.md) | Instance lifecycle, data versioning, cache context, function-handler behavior. |
| [Runtime](runtime/task-executors-and-invokers.md) | Task execution, invokers, scripting, remote runtime integration. |
| [Contracts](contracts/api-and-service-contracts.md) | API shapes, validation, compatibility, error behavior. |
| [Specs](specs/00-docs-rebuild-master-spec.md) | Rewrite scope, rollout specs, migration and deprecation plan. |
| [Archive](archive/README.md) | Legacy docs moved aside during the documentation rebuild. |

## Reading Path

1. Read [System Overview](architecture/system-overview.md) to understand the two-host runtime.
2. Read [Workflow Execution Pipeline](architecture/workflow-execution-pipeline.md) before changing transition behavior.
3. Read [Async Transition Execution Modes](architecture/async-transition-execution-modes.md) before changing the `WorkflowExecution` flags (outbox / transition-per-job / chain-token gate / reaper).
4. Read [Domain Cache Context](domain/domain-cache-context.md) before changing definition cache behavior, and [Component Cache Generation Memo](runtime/component-cache-generation-memo.md) before enabling `GenerationMemoSeconds` or editing the CD propagation window.
5. Read [Task Executors and Invokers](runtime/task-executors-and-invokers.md) before adding a task type.
6. Read [API and Service Contracts](contracts/api-and-service-contracts.md) before changing HTTP or Dapr-facing contracts.
7. Read [JSON Validation](contracts/json-validation.md) before changing schema validation errors.
8. Read [Long-Poll Termination on State Entry](domain/long-poll-termination.md) before changing State-function long-poll behavior or the pipeline pause/resume path.
9. Read [Instance Function Cache and Fingerprint ETag](runtime/state-function-cache-and-etag.md) before changing the state/data/master/schema functions' ETag, caching, or 304 behavior (includes the workflow-level `functionCache.ttlSeconds` contract).
10. Read [Event-Driven Workflows](domain/event-driven-workflows.md) before wiring external events into workflows or transitions (event mappings, Dapr subscriptions, correlation).
11. Read [Instance Filtering and Queries](runtime/instance-filtering-and-queries.md) before writing instance queries in mapping scripts (fluent `InstanceQuery`, operator reference, `GetInstancesTask` vs `DaprServiceTask`, migration from hand-written GraphQL filters).
12. Read [GetInstance Task](runtime/get-instance-task.md) when a mapping needs a single instance's metadata **and** data in one call (task type `18`, local/remote response parity).
13. Read [Script Related Instance Access](runtime/script-related-instance-access.md) before using or changing `context.Related` in mapping scripts (parent/correlation reads, unfiltered `x-roles` behavior, internal endpoint security posture).
14. Read [View Display Modes](domain/view-display-modes.md) before changing a view's `display` declaration or how clients present views (SDI / MDI shapes, response `modes` contract).
15. Read [Function Handler Architecture](domain/function-handler-architecture.md) § Custom Function Contract before declaring function `verbs` / input-output schemas and views, or changing verb enforcement.
16. Read [Well-Known Transitions](domain/well-known-transitions.md) before changing `cancel` / `updateData` / `exit` behavior, what `availableTransitions` contains, or the `kind` discriminator clients switch on.
17. Read [Role Grant Authorization](domain/role-grant-authorization.md) before touching any role check — `roles`, `queryRoles`, schema `x-roles` — or adding an authorization decision point. Covers the single-evaluator rule, batching, and why discovery must pass the same request context as enforcement.
18. Read [Function Contract Resolution](runtime/function-contract-resolution.md) before authoring or changing a function's `inputSchema` / `outputSchema` / `inputView` / `outputView`, or the `/info` discovery endpoint. Covers the rule-based wire shapes, first-match-wins semantics, and why "no match" is not an error.
19. Read [Instance Query Validation Breaking Changes](contracts/instance-query-validation-breaking-changes.md) before upgrading past 0.0.79, or before changing instance-query `filter` / `sort` / `groupBy` / `aggregations` handling. Instance queries moved from fail-open to fail-closed: silently ignored parameters now return HTTP 400, and a `GetInstancesTask` carrying the old `-field` sort shorthand fails its transition.
20. Read [Correlation and Tracing](monitoring/correlation-and-tracing.md) before changing telemetry configuration, background-job trace restoration, `X-Request-Id` propagation, or task-binding header handling. Covers the gateway (APISIX) contract, trace-continuation semantics per job type, and the reserved-header rule for task authors.
21. Read [Trace Lanes](runtime/trace-lanes.md) before changing how transition, post-commit or subflow spans are parented, or before adding a span that represents a top-level operation. Covers the anchor-vs-predecessor split that keeps chained hops siblings, the one-lane-per-instance model, and why the lane never travels in a request header.

## Documentation Rules

- Prefer architecture and contract explanations over class lists.
- Keep one page focused on one concern.
- Include boundaries, failure modes, observability, and change-safety notes.
- Reference source files for implementation detail instead of duplicating code.
- If code and docs conflict, fix the docs or document the divergence.

## Local Development

Run first-time setup before building on macOS/Linux:

```bash
./scripts/setup-netstandard-ref.sh
```

Common commands:

```bash
dotnet restore
dotnet build
dotnet test
```

API hosts:

- Orchestration: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host` on port `4201`
- Execution: `execution/BBT.Workflow.Execution.HttpApi.Host` on port `4202`
