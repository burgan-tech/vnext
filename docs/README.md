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
| [Integration](integration/forge-fanout-task-implementation.md) | Implementation specs for consumer products (Forge Studio, CLI, SDKs) that build against runtime features. |
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
22. Read [FanOut Task](domain/fan-out-task.md) before authoring or changing a task type `21` (`FanOutTask`) — the config schema, the four join policies (including the empty-batch rule), the `IFanOutMapping` contract, the zero-script default-binding limitation, the single-write invariant, error codes for partial-failure branching, and the two-level concurrency bulkhead.
23. Read [Forge Studio — FanOutTask Implementation Spec](integration/forge-fanout-task-implementation.md) before changing how a designer tool (Forge Studio) authors, validates or renders a task type `21`. Covers the config form field-by-field, the designer-side rules the runtime can only catch at parse/execution time, canvas guidance, and the `vnext-meta` / `vnext-schema` gaps consumers depend on.
24. Read [End-to-End Trace/Span Tree](runtime/trace-span-tree.md) before adding, renaming, or gating a span, or before registering a new `ActivitySource`. Covers the full span-name → source → tags reference, the `AdditionalSources` same-commit registration rule, and the 2026-08-25 reversal of the "no compile span" decision.
25. Read [Dapr Invocation Transport](runtime/dapr-invocation-transport.md) before assuming a Dapr-facing task or client should move to gRPC. Covers the three hops of a Dapr service invocation call and why `dapr.io/app-protocol` never controlled `DaprServiceTask`'s transport, the `InvokeService` deprecation evidence behind the decision to keep `DaprServiceTask` on HTTP, the Orchestration → Execution gRPC proxy-mode design, and the cross-domain `Remote*` services' `DaprClient` path (HTTP to the sidecar; why the SDK's gRPC invoke family cannot serve HTTP/JSON callees; the query-encoding and `ERR_DIRECT_INVOKE` behaviours the transport shell compensates for). Pair it with [Remote App Service Architecture](runtime/remote-app-service-architecture.md) before adding a remote method or changing `AddRemoteService`: the `IRemoteTransport<TClient>` shell, the Kind-based router, the `RemoteServiceProfile` retry split, and the `ServiceDiscovery:Provider` switch that selects HTTP vs. Dapr per domain.
26. Read [Extensions](domain/extensions.md) before assuming two Extensions cannot reference the same task — they can and are expected to when their `Mapping`/`ErrorBoundary` differ. Covers the per-extension `ResponseVariableKey` output key, why the duplicate-task-key warning does not fire for the Extension hook, and the Preprod fault (parallel-merge crash) this shape once caused.
27. Read [Event Publish Modes](runtime/event-publish-modes.md) before adding or changing a distributed event, or before touching subflow terminal delivery. Covers the two-mode publish taxonomy (Outbox vs Outbox + TerminalRelay), why the EventHook infrastructure no longer exists, the `SubflowTerminalRelay` / Inbox-backup split for the three subflow terminal events, the re-arm-on-revert mechanism, and the Aether wakeup signal (`Aether:Outbox:WakeupSignalEnabled`).
28. Read [Python Task](runtime/python-task.md) before authoring Python tasks or changing the Python.NET, process, container, package-lock, or container-driver contracts.

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
