# vNext Platform Pain Points

This document outlines the identified pain points, technical debt, and areas for improvement in the vNext Platform repository.

## 1. Developer Experience (DX)

### .NET 10 & PostSharp Setup Hurdles
*   **Targeting Pack Dependency**: New developers must run a manual setup script (`setup-netstandard-ref.sh`) to install `NETStandard.Library.Ref 2.1.0`. This is a non-standard requirement for .NET development and can cause build failures in environments where scripts cannot be easily run.
*   **PostSharp Build Overhead**: The use of PostSharp for AOP (Aspect-Oriented Programming) adds significant time to the build process and requires specific IDE configurations for full support.

### Local Development Complexity
*   **Heavy Infrastructure Dependency**: Running the system locally requires Docker for PostgreSQL, Redis, Dapr, and Jaeger. While recommended, it creates a high barrier to entry for simple code changes.
*   **Dapr Dependency**: The tight coupling with Dapr for service-to-service communication makes it difficult to run or debug individual services in isolation without a full sidecar environment.

### Project Proliferation
*   **Granular Project Structure**: The solution contains a large number of projects (20+). While this supports Clean Architecture, it increases cognitive load for navigation and results in slower IDE performance and longer restore/build cycles.

## 2. Security & Reliability

### EF1002 SQL Injection Risks
*   **Raw SQL Usage**: Multiple locations in `EfCoreInstanceRepository.cs` and `MultiSchemaMigrator.cs` use `FromSqlRaw` with string interpolation for schema names, table names, and ORDER BY clauses.
*   **Validator Reliance**: Although `ISchemaValidator` is used to mitigate risks, the pattern itself triggers security warnings (EF1002) and requires constant vigilance during audits. A more structured approach to dynamic schema/table selection would be safer.

### Time-Dependent Logic Testing
*   **Direct `DateTime.UtcNow` Usage**: The codebase frequently uses `DateTime.UtcNow` directly instead of a clock abstraction (e.g., `ISystemClock` or `TimeProvider`). This makes testing time-sensitive features (like timers or ETag expiration) fragile and dependent on `Thread.Sleep`.

### Secret Management
*   **Vault Integration**: While HashiCorp Vault is supported, it is disabled by default in configuration. Local development relies on cleartext connection strings in `appsettings.json`.

## 3. Architecture & Maintainability

### Multi-Schema Scale Concerns
*   **Schema Management**: Each workflow "flow" gets its own PostgreSQL schema. At scale (thousands of flows), this can lead to:
    *   Significant overhead in database migrations.
    *   Connection pooling issues if not managed carefully by the Aether SDK.
    *   Increased complexity in cross-flow analytics.

### Event Handling Boilerplate
*   **Dual-Processing Pattern**: Every domain event requires both an `IEventPublishHook` (synchronous/local) and an `IEventHandler` (asynchronous/distributed). This "dual-write" mitigation adds significant boilerplate and increases the chance of developers forgetting one of the components.

### Dynamic Filter Complexity
*   **`GraphQLJsonFilterService`**: Building native PostgreSQL JSONB queries from GraphQL-style JSON is powerful but highly complex. The current implementation relies on string building and manual parameter indexing, which is difficult to maintain and extend.

### Service Discovery & Routing
*   **Static Configuration**: Many service URLs are hardcoded in `appsettings.json`. While `IDomainDiscoveryResolver` exists, it is disabled by default, leading to a "brittle" configuration in multi-environment setups.

## 4. Quality Assurance

### Brittle Tests
*   **`Thread.Sleep` in Unit Tests**: Several tests (e.g., `CacheItemTests.cs`, `InstanceTaskTests.cs`) use `Thread.Sleep` to wait for asynchronous operations or expiration. This makes the test suite slower and prone to intermittent "flaky" failures in CI/CD environments with varying performance.

### Integration Test Environment
*   **Resource Intensity**: Tests rely on `Testcontainers` (PostgreSQL/Redis), which requires a Docker-enabled environment. This increases the resources required for running the full test suite and complicates CI pipeline configuration.
*   **Environmental Fragility**: As seen during analysis, tests using Testcontainers can fail with `DockerApiException` (InternalServerError) if the host Docker environment has specific overlay mount issues or permission constraints. This makes the test suite "fragile" across different developer machines and CI agents.
