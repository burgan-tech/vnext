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
4. Read [Domain Cache Context](domain/domain-cache-context.md) before changing definition cache behavior.
5. Read [Task Executors and Invokers](runtime/task-executors-and-invokers.md) before adding a task type.
6. Read [API and Service Contracts](contracts/api-and-service-contracts.md) before changing HTTP or Dapr-facing contracts.
7. Read [JSON Validation](contracts/json-validation.md) before changing schema validation errors.
8. Read [Long-Poll Termination on State Entry](domain/long-poll-termination.md) before changing State-function long-poll behavior or the pipeline pause/resume path.
9. Read [Event-Driven Workflows](domain/event-driven-workflows.md) before wiring external events into workflows or transitions (event mappings, Dapr subscriptions, correlation).
10. Read [Instance Filtering and Queries](runtime/instance-filtering-and-queries.md) before writing instance queries in mapping scripts (fluent `InstanceQuery`, operator reference, `GetInstancesTask` vs `DaprServiceTask`, migration from hand-written GraphQL filters).

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
