# Project Context

## Purpose

Describe the product or runtime problem without proposing a solution.

## Users And Critical Workflows

-

## System Boundary

-

## Repository And Service Catalog

| Repository/Service | Responsibility | Data owner | Owner | Critical dependencies |
|---|---|---|---|---|
| vNext orchestration | Workflow definitions, instances and transitions | Orchestration domain | | Dapr, PostgreSQL, Redis |
| vNext execution | Task execution for transitions | Execution domain | | Dapr, task invokers |
| vNext monitor | Read-only operational queries | Monitor domain | | PostgreSQL |

## Technology Stack

- .NET / ASP.NET Core
- PostgreSQL with multi-schema support
- Dapr service invocation and pub/sub
- Redis distributed cache
- Aether SDK

## Integrations

| System | Protocol | Contract | Security | Timeout/retry owner |
|---|---|---|---|---|
| | | | | |

## Environments And Deployment

-

## Critical NFRs

- Performance:
- Availability:
- Recovery:
- Retention:

## Regulatory And Security Constraints

-

## Known Technical Debt

-

## Related ADRs And Documents

- `docs/agent-onboarding.md`
- `docs/architecture/system-overview.md`
