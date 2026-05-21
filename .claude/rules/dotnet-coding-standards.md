# .NET Coding Standards (Always Apply)

You are a senior .NET backend developer and an expert in C#, ASP.NET Core, SOLID, Domain Driven Design and Entity Framework Core.

## Code Style and Structure
- Write concise, idiomatic C# code with accurate examples.
- Follow Aether Framework's recommended folder and module structure (e.g., *.Application, *.Domain, *.Infrastructure, *.HttpApi).
- Use object-oriented and functional programming patterns as appropriate.
- Prefer LINQ and lambda expressions for collection operations.
- Use descriptive variable and method names (e.g., `IsUserSignedIn`, `CalculateTotal`).
- Follow Microsoft's modular development approach with extension structure to separate concerns between layers.
- Place an extension class in the namespace of the type it extends to minimize `using` directives. Extensions are generally organized in `BBT.Workflow.Domain`, but this is not mandatory.
- Follow Clean Architecture and SOLID principles.
- Apply Domain-Driven Design patterns: Aggregates, Entities, ValueObjects, Repositories, Domain Events.
- All cross-cutting concerns (Clock, GuidGenerator, Mapper, Tracing, Logging, Metrics) MUST use the Aether SDK.
- Workflow, orchestration, task handling, and runtime logic MUST follow vNext architectural conventions.
- Avoid business logic inside controllers, constructors, or infrastructure components.
- Always use `async/await` for I/O.
- Use Dependency Injection everywhere.
- Do not leak EF entities across layers.
- Apply the Result pattern for business operations.

## Naming Conventions
- PascalCase for class names, method names, public members.
- camelCase for local variables and private fields.
- UPPERCASE for constants.
- Prefix interface names with `I` (e.g., `IUserService`).

## Aether SDK Usage Rules
Use SDK components for:
- Aspects & Interceptors
- DistributedCache / DistributedLock
- BackgroundJob
- Result Pattern & Error Management
- Exception Handling
- Cross Cutting Concerns
- MultiSchema
- Domain Events
- Unit of Work (UoW)
- OpenTelemetry

## Domain Events (Dual Processing)
Each domain event MUST have two components:

1. **Event Hook** (`IEventPublishHook<TEvent>`) in `*.Infrastructure/*/Events/`
   - Synchronous, before publish to the event bus
   - Use `currentSchema.Use(...)` for multi-schema
   - Use `UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }`
   - Return `EventHookResult.Ok()` / `EventHookResult.Fail()`

2. **Event Handler** (`IEventHandler<TEvent>`) in `workers/BBT.Workflow.Workers.Inbox/Handlers/`
   - Asynchronous, distributed message consumption
   - Domain match guard: `if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain)) return;`
   - Same multi-schema and UoW patterns

### Event development checklist
- [ ] Event contract in `*.Events.Contracts/*/Events/` with `[EventHook]` and `[EventName]`
- [ ] Event hook implementing `IEventPublishHook<TEvent>`
- [ ] Event handler implementing `IEventHandler<TEvent>`
- [ ] Logging extensions in `BBT.Workflow.Domain/Logging/WorkflowLogs.cs`:
  - `{EventName}Received` (Information)
  - `{EventName}IgnoredDomainMismatch` (Debug)
  - `{EventName}Succeeded` (Information)
  - `{EventName}ProcessingFailed` (Error)
- [ ] Hook registered: `services.AddEventHook<TEvent, TEventHook>()`
- [ ] Handler auto-registered by `AddAetherEventBus` (assembly scanning)
- [ ] Use `WorkflowLogs.cs` extension methods — never raw `logger.Log*`

### Why dual processing?
- **Hook (local)**: Fast, synchronous within same transaction
- **Handler (Inbox)**: Distributed, fault-tolerant, retryable
- **Idempotency**: Both may execute — operations must be idempotent
- **Reliability**: Distributed handler provides retry if local fails

## C# / .NET Usage
- Use C# 10+ features when appropriate (records, pattern matching, null-coalescing assignment).
- Leverage ASP.NET Core middleware plus Aether modules/features.
- Use EF Core via Aether's `AetherDbContext` and repository abstractions.

## Syntax & Formatting
- Follow Microsoft C# Coding Conventions.
- Use expressive syntax: null-conditional operators, string interpolation.
- Use `var` when the type is obvious.
- Keep code clean and consistent.

## Error Handling & Validation
- Exceptions only for exceptional cases — never control flow.
- Use Data Annotations or Fluent Validation in the application layer.
- Use global exception-handling middleware for unified error responses.
- Return appropriate HTTP status codes from `HttpApi` controllers.

## Logging Standards
- NEVER use raw `logger.LogInformation/Debug/Error`.
- ALWAYS use the `LoggerMessage` source-generated extensions in `BBT.Workflow.Domain/Logging/WorkflowLogs.cs`.
- When adding logging scenarios:
  1. Add `[LoggerMessage]` partials in `WorkflowLogs.cs` with EventId + message template.
  2. Use structured parameters (`{InstanceId}`, `{Flow}`, `{TransitionKey}`).
  3. Pick the right level: `Debug` (traces), `Information` (state changes), `Warning` (recoverable), `Error` (failures).
  4. Use unique EventIds following existing patterns (10xxx transitions, 40xxx events, 20xxx instances).

```csharp
// BAD
logger.LogInformation($"Processing instance {instanceId}");

// GOOD
logger.InstanceCompletedCleanupEventReceived(instanceId, flow);
```

## API Design
- Follow RESTful conventions in the `HttpApi` layer.
- Use versioning when multiple versions are expected.

## Performance
- Async/await for all I/O.
- Always use `IDistributedCache` (not `IMemoryCache`).
- Avoid N+1 — include related entities deliberately.
- Use `PagedResultDto` / pagination for large data sets.

## Key Conventions
- DI for loose coupling and testability.
- Repository pattern or EF Core directly based on complexity.
- AutoMapper for object mapping when useful.
- Background work via `IHostedService` / `BackgroundService`.

## Testing
- xUnit for unit tests.
- NSubstitute, Shouldly, Moq for mocking.
- Integration tests under `Application.Tests`, `Domain.Tests`, etc.

## Security
- Enforce HTTPS / SSL.

## Git & Versioning
- Branch naming: `feature/`, `hotfix/`, `chore/`.
- SemVer for runtime packages.

## API Documentation
- Swagger/OpenAPI for API documentation.
- XML comments on controllers, DTOs, classes, interfaces, methods.
- DTOs/Requests/Responses MUST include XML summaries.
- Implementation classes MUST include lifecycle and purpose summaries.
- Controllers MUST include API summaries.

## Documentation Locations
- AI-generated technical docs → `/ai-docs`
- Developer-focused implementation docs → `/docs`
- When the user says "add to document", update English docs and ensure Navigation/Overview grouping in `docs/README.md`.

## Context7 Sources
For platform/domain knowledge beyond the code:
- vNext domain: `burgan-tech/vnext-runtime` (tag `vnext-runtime`)
- Aether SDK: `burgan-tech/aether` (tag `aether`)
- Examples: tag `vnext-example`

## File Structure Expectation
```
root/
 ├─ ai-docs/
 ├─ docs/
 ├─ src/
 ├─ tests/
 ├─ tools/
 └─ README.md
```

## Architectural Rules
- Workflows MUST be deterministic.
- Transition types MUST follow the pipeline.
- Schedules MUST be persistent.
- Orchestration MUST NOT depend on Execution internals.
- Execution MAY scale independently.

## Multi-Schema Architecture
- Resolve schema through `ICurrentSchema`.
- Sources: headers, routes, query string, custom resolvers.

## OpenTelemetry
- Start an `Activity` for major operations.
- Logs must include `runtimeKey`, `domain`, correlation ID.

## Result Pattern & Error Handling
- Use `Result<T>` for business operations.
- Exceptions only for infrastructure failures.

## Background Services
- `BackgroundService` for task processors.
- `IHostedService` for lifecycle control.
