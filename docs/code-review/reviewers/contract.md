# Reviewer: contract

Owns everything a **consumer outside this process** can observe and therefore break on: HTTP and
Dapr-facing shapes, the state function's body and ETag, authorization surfaces, distributed event
contracts and their delivery mode, the `vnext-meta` package, and the documentation/rule files that
are themselves a contract with other agents and sibling repos.

Source of truth: [API and Service Contracts](../../contracts/api-and-service-contracts.md),
[Event Publish Modes](../../runtime/event-publish-modes.md),
[Instance Function Cache and Fingerprint ETag](../../runtime/state-function-cache-and-etag.md),
[Role Grant Authorization](../../domain/role-grant-authorization.md),
[Well-Known Transitions](../../domain/well-known-transitions.md),
and [`.claude/rules/vnext-workflow-developer.md`](../../../.claude/rules/vnext-workflow-developer.md).

## 1. Public API compatibility (`contract/api-*`)

- [ ] `contract/api-breaking` — a removed or renamed field, a narrowed type, a new required request field, or a changed default is a breaking change. It needs a `BREAKING CHANGE:` footer, a `⚠️ Breaking change` note in the PR body, and a `deprecations.json` + `migrations.json` entry.
- [ ] `contract/api-fail-closed` — validation that moves from fail-open to fail-closed (silently-ignored parameter → HTTP 400) is breaking even when it looks like a bug fix; see [Instance Query Validation Breaking Changes](../../contracts/instance-query-validation-breaking-changes.md).
- [ ] `contract/api-status-codes` — the endpoint returns the documented status code and structured error code; a new error code is added to the error catalogue, not invented inline.
- [ ] `contract/api-internal-only` — an internal-only endpoint (`internal/subflow-forward`, `internal/busy-release`, `related-data*`, `sub/instances/start`) is not reachable through a public route, and a trust flag such as `ChainReserved` or `SuppressResponseEnrichment` is never accepted from a caller-supplied body on a public endpoint.
- [ ] `contract/api-dapr-response` — an event-delivery endpoint answers with the Dapr pub/sub protocol body (`EventDeliveryResponse`), never an instance DTO. An `InstanceStatus` in the top-level `status` field causes endless redelivery.

## 2. State function, ETag, long-poll (`contract/state-*`)

- [ ] `contract/state-shape-version` — **any** change to what the state body carries bumps `StateFunctionCache.ResponseShapeVersion` in the same commit. Without the bump, a client polling a parked instance keeps getting 304 and never sees the new shape. CRITICAL.
- [ ] `contract/state-fingerprint` — a new field a client can act on is either a fingerprint member or the PR states why it deliberately is not (as the scheduled-transition entries do).
- [ ] `contract/state-etag-source` — ETag comes from `LatestData?.ETag` or `IRepresentationEtagService.Generate(output)`, never a hand-rolled hash; the `IfNoneMatch` / 304 path is preserved.
- [ ] `contract/state-readonly` — nothing in the state-function path mutates instance state.
- [ ] `contract/state-role-filter` — `availableTransitions` stays filtered through `ITransitionAuthorizationManager`; state **and** roles are enforced on both role-aware surfaces (state function, `authorize`), and no third evaluation path is introduced.
- [ ] `contract/state-incident-links` — the `incident` block carries links, not content. Re-embedding incident fields in the body puts the incident table back on the hottest path.
- [ ] `contract/state-subflow-window` — the subflow completion window is preserved: while the parent correlation is open, parent main-flow transitions are shown instead of the subflow terminal view.

## 3. Authorization surfaces (`contract/auth-*`)

- [ ] `contract/auth-single-evaluator` — instance-bound role evaluation goes through `IRoleGrantEvaluator`; no second matcher.
- [ ] `contract/auth-request-context` — every surface evaluating a grant set is given the same `AuthorizationRequestContext`. Omitting it does not fail closed — it silently empties `$.context.*` and the transition vanishes from `availableTransitions` while `authorize` still says allowed. CRITICAL.
- [ ] `contract/auth-prefetch-hint` — `grantsForPrefetchHint` covers every grant that will be evaluated, including per-state `availableIn` grants.
- [ ] `contract/auth-caller-roles` — `currentUser.ResolveCallerRoles(headers)` is used at decision points, never `currentUser.Roles` directly; the same role set feeds the decision, the field filtering and `CallerScopeHash`.
- [ ] `contract/auth-not-execution` — a PR that starts enforcing `transition.roles` at `POST .../transitions/{key}` is changing a deliberate decision and needs a council record, not a review nod.

## 4. Distributed events (`contract/event-*`)

- [ ] `contract/event-contract` — a new event has a contract in `*.Events.Contracts/*/Events/` with `[EventName]` and no marker interface.
- [ ] `contract/event-handler` — an Inbox `IEventHandler<T>` exists with the domain-match guard (`if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain)) return;`) and the standard multi-schema + UoW pattern.
- [ ] `contract/event-logs` — the four `WorkflowLogs` entries exist: `{EventName}Received`, `{EventName}IgnoredDomainMismatch`, `{EventName}Succeeded`, `{EventName}ProcessingFailed`.
- [ ] `contract/event-relay-opt-in` — an immediate delivery path is added **only** by registering `IPostCommitEventRelay<TEvent>` in `AddPipelineServices`. A new relay registration requires all four of: a durable outbox backup, an idempotent order-safe receiver guard, measured latency evidence, and a council row. Missing any one is CRITICAL.
- [ ] `contract/event-no-hook` — no reintroduction of `IEventPublishHook`, `EventHookAttribute` or any synchronous pre-commit publish.

## 5. `vnext-meta` and versioning (`contract/meta-*`)

- [ ] `contract/meta-feature` — a new capability adds an entry to `vnext-meta/features.json` with `since`; a new task/function/extension key adds a `component-registry.json` entry; a new limit adds a `performance-profiles.json` entry whose `sources` point at real C# constants; a new enforced rule adds a `security-policy.json` entry.
- [ ] `contract/meta-deprecation` — a removal or rename adds `deprecations.json` **and** `migrations.json` entries.
- [ ] `contract/meta-known-issue` — a shipped limitation is recorded in `known-issues.json` rather than only in the PR description.
- [ ] `contract/meta-validator` — any edit under `vnext-meta/` means the `vnext-meta-validator` skill must have been run; the PR says so.
- [ ] `contract/meta-version-bump` — `common.props` `<Version>` is **not** hand-edited in a feature PR; CI bumps it (`chore: bump version to … [skip ci]`). A manual bump is a WARNING with the reason requested.

## 6. Documentation and agent guidance (`contract/docs-*`)

- [ ] `contract/docs-owner-page` — a behaviour change updates the one `/docs` page that owns that concern. If code and docs now disagree, the docs are wrong and must be fixed in the same PR.
- [ ] `contract/docs-no-duplication` — a fact is not copied into a second file. Step tables, profile exclusions and delivery modes live in one place and are linked, never pasted into `AGENTS.md` or a skill.
- [ ] `contract/docs-cursor-pointer` — a **new** file under `.claude/rules/` has a matching 8-line `@`-pointer under `.cursor/rules/`.
- [ ] `contract/docs-readme-index` — a new `/docs` page is linked from `docs/README.md`.
- [ ] `contract/docs-council-row` — a decision recorded by the council has its one row in `docs/agent-council/sessions/README.md`; the session folder itself stays in git-ignored `ai-docs/`.
- [ ] `contract/docs-no-ai-docs` — nothing under `ai-docs/` is committed or cited as a source of truth.

## 7. Configuration and deployment (`contract/config-*`)

- [ ] `contract/config-helm` — a **mandatory** new config key or env var has a counterpart in the sibling `vnext-helm-charts` (`charts/vnext`, `appEnvConfig`/`extraEnvConfig`), or the PR explicitly flags the follow-up. Runtime options have no chart-side defaults and `WorkflowExecutionOptionsValidator` fails startup on bad values.
- [ ] `contract/config-defaults` — a new option has a safe default and a documented range; it does not change existing behaviour silently.
- [ ] `contract/config-dapr` — a new Dapr component, subscription or app-id is reflected in `etc/docker` and in [Dapr Component Footprint](../../runtime/dapr-component-footprint.md).
