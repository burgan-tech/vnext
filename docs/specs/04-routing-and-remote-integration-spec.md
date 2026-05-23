# Spec 04: Routing and Remote Integration

## Purpose

Document how the runtime decides between local and remote execution paths, how endpoint
discovery works, and how remote calls remain observable and recoverable.

## Deliverables

- [Gateway Routing Strategy](../architecture/gateway-routing-strategy.md)
- [Remote App Service Architecture](../runtime/remote-app-service-architecture.md)

## Key Decisions

- Controllers call gateway interfaces rather than choosing local/remote behavior directly.
- Domain discovery resolves target domain endpoints.
- Remote services use public runtime routes and result mapping.
- Retry, timeout, and circuit breaker policies live in remote service registration.

## Acceptance Checklist

- Local and remote gateway responsibilities are documented.
- Discovery failure behavior is documented.
- Header forwarding and ETag propagation are documented.
- Retry/circuit breaker/timeout ownership is documented.

## Source Alignment

Review these files when the spec changes:

- `src/BBT.Workflow.Infrastructure/Gateway/`
- `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs`
- `src/BBT.Workflow.Infrastructure/Remote/`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/`
- `src/BBT.Workflow.Infrastructure/Authorization/Remote/`

