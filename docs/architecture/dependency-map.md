# Dependency Map

## Purpose

This page describes allowed dependency direction. Use it before adding a project
reference, moving a type, or introducing a new runtime integration point.

## Boundaries

| Layer | May depend on | Must not depend on |
| --- | --- | --- |
| Domain | Aether primitives, shared contracts inside Domain | EF Core, HTTP, Dapr clients, application services. |
| Application | Domain, abstraction projects, DTO contracts | EF Core DbContext, concrete remote clients, host projects. |
| Infrastructure | Domain, Application abstractions, EF Core, Dapr, external integrations | API controller behavior. |
| Orchestration host | Application and Infrastructure registration | Execution implementation details. |
| Execution host | Execution services and task invokers | Orchestration repositories or instance persistence. |
| Workers | Application and Infrastructure services for async workloads | Request-only controller assumptions. |

## Architecture Flow

Core rule: dependencies point inward toward domain concepts and outward only through
interfaces owned by the inner layer.

```text
Hosts / Workers
  -> Infrastructure
  -> Application
  -> Domain
```

Execution is intentionally separate from Orchestration. Orchestration prepares task
requests from workflow definitions and instance context. Execution handles the typed
binding and returns a result; it does not own workflow aggregates.

## Contracts

| Contract | Defined in | Implemented in |
| --- | --- | --- |
| Repositories | Domain / Application abstractions | Infrastructure EF Core repositories. |
| Task executors | Domain task executor contracts | Application task executors. |
| Task invokers | Execution abstractions / Execution services | Execution project invokers. |
| Remote app services | Application remote interfaces | Infrastructure HTTP clients. |
| Event hooks | Events contracts / Infrastructure registrations | Infrastructure hook classes. |
| Inbox handlers | Events contracts | Inbox worker handlers. |

## Failure Modes

- Infrastructure dependency leaking into Domain makes domain tests slow and couples rules to deployment.
- Host-specific logic in Application makes workers and tests hard to reuse.
- Execution reading Orchestration persistence bypasses the task envelope contract and breaks service isolation.
- Remote integration code without an interface prevents local-vs-remote routing.

## Observability

Dependency boundaries should preserve telemetry tags. When adding a boundary, pass
correlation id, causation id, domain, flow, instance id, and current-user context where
the call has user semantics.

## Change Safety

- Add interfaces at the layer that owns the use case, not at the layer that happens to implement it.
- Keep DTOs used across service boundaries stable and versioned.
- Prefer gateway interfaces for local/remote choices instead of branching inside controllers.

## References

- `src/BBT.Workflow.Domain/`
- `src/BBT.Workflow.Application/`
- `src/BBT.Workflow.Infrastructure/`
- `src/BBT.Workflow.Tasks.Abstractions/`
- `src/BBT.Workflow.Execution.Abstractions/`
- `src/BBT.Workflow.Events.Contracts/`

