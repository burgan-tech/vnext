# BBT Workflow Engine - Developer Documentation

## Overview

The BBT Workflow Engine is a comprehensive, domain-driven workflow management system built with .NET 8 and ASP.NET Core. It follows Clean Architecture principles and Domain-Driven Design (DDD) patterns to provide a scalable, maintainable, and extensible workflow orchestration platform.

## Table of Contents

### Getting Started
- [Getting Started](./getting-started.md) - Setup and initial configuration

### Architecture
- [Architecture Overview](./architecture/overview.md) - System architecture, microservices design, and transition pipeline
- [Transition Pipeline](./architecture/transition-pipeline.md) - Deterministic transition lifecycle execution system
- [Domain Models](./architecture/domain-models.md) - Core business entities and aggregates
- [Task Invoker](./architecture/task-invoker.md) - Execution Service invokers and binding types
- [Multi-Schema](./architecture/multi-schema.md) - Multi-tenant schema management

### SDK & Cross-Cutting
- [Aether SDK Aspects](./sdk/aspects.md) - UnitOfWork, Log, Trace
- [Result Pattern](./sdk/result-pattern.md) - Exception-free error handling with Aether SDK
- [EventBus Hooks](./sdk/eventbus-hooks.md) - Pre-publish hooks for domain events

### Implementation
- [Application Services](./implementation/application-services.md) - Application layer services and interfaces
- [Task Executors](./implementation/task-executors.md) - Task execution system and custom executors
- [Task Factory & Pooling](./implementation/task-factory-pooling.md) - High-performance task instance creation and memory optimization
- [Strategy Pattern](./implementation/strategy-pattern.md) - Strategy pattern usage across the system
- [Remote Routing and Discovery](./implementation/remote-routing-and-discovery.md) - Gateways, remote clients, and service discovery
- [Infrastructure Layer](./implementation/infrastructure-layer.md) - Data access and external integrations

### Features
- [Scripting Engine](./features/scripting-engine.md) - Dynamic C# script execution, logging, and configuration access
- [Auto Transition](./features/auto-transition.md) - Automatic transition evaluation
- [Timer Execution](./features/timer-execution.md) - Enhanced timer execution and scheduling
- [Instance Filtering](./features/instance-filtering.md) - Advanced filtering capabilities for workflow instances
- [Caching Strategy](./features/caching-strategy.md) - Distributed caching implementation and patterns

### Infrastructure & Operations
- [Background Jobs](./infrastructure/background-jobs.md) - Asynchronous job processing with Dapr
- [Inbox/Outbox Workers](./infrastructure/inbox-outbox-workers.md) - Transactional outbox and inbox event processing
- [ClickHouse Integration](./infrastructure/clickhouse-integration.md) - High-performance analytics with ClickHouse
- [Embedded Scripts and Dapr](./infrastructure/embedded-scripts-and-dapr.md) - Embedded scripts and Dapr metadata/bindings
- [OpenTelemetry Logging](./infrastructure/opentelemetry-logging.md) - Distributed tracing, structured logging, and telemetry

### Metrics
- [Cache Metrics](./infrastructure/metrics/cache-metrics.md) - Cache performance monitoring
- [Persistent Metrics](./infrastructure/metrics/persistent-metrics.md) - Long-term metrics storage
- [Database Metrics](./infrastructure/metrics/database-metrics.md) - Database performance monitoring

### Guides & Examples
- [Instance Filtering Guide (EN)](./guides/instance-filtering-guide-en.md)
- [Instance Filtering Guide (TR)](./guides/instance-filtering-guide-tr.md)
- [Dapr Timer Examples](./guides/examples/dapr-timer-examples.md)
- [Timer Mapping Examples](./guides/examples/timer-mapping-examples.md)

### Security
- [QueryExtensions Security](./security/queryextensions-security.md)

## Key Features

### Core Capabilities
- **Workflow Definition & Execution**: Define complex business workflows with states, transitions, and tasks
- **Hierarchical Workflows**: Support for sub-flows and sub-processes with parent-child relationships
- **Multi-Task Support**: HTTP, DAPR service calls, script execution, human tasks, and more
- **State Management**: Sophisticated state machine with conditional transitions
- **Script Engine**: Dynamic script execution for business logic and data transformation
- **Real-time Execution**: Synchronous and asynchronous workflow execution modes

### Workflow Orchestration
- **Transition Pipeline**: Deterministic, extensible pipeline-based transition execution
- **Dynamic Step Planning**: Runtime-configurable execution flow with directives
- **Trigger Handlers**: Specialized processing for Manual, Automatic, Scheduled, and Event-based transitions
- **Re-entry System**: Optimized handling of automatic and scheduled transitions
- **Sub-Flow Management**: Execute child workflows from parent workflows with correlation tracking
- **Blocking & Non-Blocking Execution**: SubFlows block parent execution, SubProcesses run in parallel
- **Instance Correlation**: Automatic tracking and management of parent-child workflow relationships
- **Cross-Schema Support**: Sub-flows can execute in different schemas and domains

### Technical Features
- **Clean Architecture**: Separation of concerns with Domain, Application, Infrastructure, and API layers
- **Microservices Architecture**: Separate Orchestration and Execution APIs for scalability
- **Transition Pipeline**: Step-based, deterministic workflow state management with dynamic planning
- **Multi-Schema Support**: Dynamic schema creation for multi-flow scenarios
- **Distributed Caching**: Redis-based caching with automatic invalidation
- **Background Processing**: Dapr-based job scheduling and execution
- **OpenTelemetry Integration**: Comprehensive distributed tracing and structured logging
- **Script Logging & Configuration**: Built-in logging and configuration access in scripts
- **Health Monitoring**: Comprehensive health checks and telemetry
- **API Versioning**: RESTful APIs with version management

### Integration Capabilities
- **Dapr Integration**: Service invocation, pub/sub, bindings, state management, and secrets
- **Entity Framework Core**: Advanced ORM with multi-schema support and performance interceptors
- **OpenTelemetry**: Distributed tracing, structured logging, and metrics collection with custom spans and events
- **PostgreSQL**: Primary database with advanced JSONB support and optimized queries
- **ClickHouse**: High-performance columnar database for analytics and metrics
- **Redis**: Distributed caching and session management

## Quick Start

```bash
# Clone the repository
git clone <repository-url>
cd workflowV2

# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run the application
./run-docker.sh 
```

## Technology Stack

- **.NET 9**: Latest .NET framework for high-performance applications
- **ASP.NET Core**: Web framework for building RESTful APIs
- **Entity Framework Core**: Object-relational mapping with multi-schema support
- **PostgreSQL**: Primary database with advanced JSONB support
- **ClickHouse**: High-performance columnar database for analytics
- **Redis**: Distributed caching and session management
- **Dapr**: Distributed application runtime for microservices (service invocation, pub/sub, secrets, state management)
- **OpenTelemetry**: Distributed tracing, structured logging, and metrics
- **Docker**: Containerization and orchestration
- **Roslyn**: Dynamic C# script compilation and execution

## Project Structure

```
vnext/
├── orchestration/
│   └── BBT.Workflow.Orchestration.HttpApi.Host/  # Public-facing API for workflow management
├── execution/
│   └── BBT.Workflow.Execution.HttpApi.Host/      # Internal API for task execution
├── workers/
│   ├── BBT.Workflow.Workers.Inbox/               # Inbox event processing worker
│   └── BBT.Workflow.Workers.Outbox/              # Outbox message publishing worker
├── src/
│   ├── BBT.Workflow.Domain/                      # Domain models and business logic
│   ├── BBT.Workflow.Application/                 # Application services, DTOs, and pipeline
│   ├── BBT.Workflow.Infrastructure/              # Data access, EF Core, and external integrations
│   ├── BBT.Workflow.Execution/                   # Task invokers for Execution Service
│   ├── BBT.Workflow.Execution.Abstractions/      # Bindings and contracts
│   └── BBT.Workflow.HttpApi.Shared/              # Shared middleware, telemetry, and utilities
├── modules/
│   └── BBT.Workflow.Modules.Scripting/           # Roslyn-based scripting module
├── test/
│   ├── BBT.Workflow.Domain.Tests/
│   ├── BBT.Workflow.Application.Tests/
│   └── BBT.Workflow.Infrastructure.Tests/
├── etc/
│   ├── docker/                                   # Docker Compose configuration
│   ├── execution/dapr/                           # Dapr configuration for Execution API
│   ├── orchestration/dapr/                       # Dapr configuration for Orchestration API
│   └── workers/dapr/                             # Dapr configuration for Workers
└── docs/                                         # Technical documentation
```

## Contributing

1. Read the architecture documentation to understand the system design
2. Follow the established patterns and conventions
3. Ensure all tests pass before submitting changes
4. Update documentation when adding new features

## Support

For technical questions and support:
- Review the documentation in this folder
- Check the guides and examples for usage patterns
- Refer to the test projects for implementation details 