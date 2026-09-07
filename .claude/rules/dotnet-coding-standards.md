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

## Domain Events (Outbox Delivery)

**The EventHook infrastructure no longer exists** (`IEventPublishHook<TEvent>`, `IEventHookInvoker`,
`EventHookAttribute`, `EventHookMode` are deleted). Every distributed event publishes plainly
through the transactional outbox — there is no synchronous, pre-commit hook path anymore. Each
event MUST have:

1. **Contract** in `*.Events.Contracts/*/Events/` with `[EventName]` — no `[EventHook]` attribute,
   there is nothing left for it to configure.
2. **Event Handler** (`IEventHandler<TEvent>`) in `workers/BBT.Workflow.Workers.Inbox/Handlers/`
   - Asynchronous, distributed message consumption
   - Domain match guard: `if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain)) return;`
   - Standard multi-schema and UoW patterns
3. **Logging extensions** in `BBT.Workflow.Domain/Logging/WorkflowLogs.cs` — never raw `logger.Log*`

**Subflow terminal events are the one exception carrying extra behavior.** `InstanceSubCompletedEvent`,
`InstanceSubFaultedEvent`, and `InstanceSubCanceledEvent` additionally implement
`ISubflowTerminalEvent`, which opts them into the **Outbox + TerminalRelay** mode: after commit,
`SubflowTerminalRelay` relays the event as an immediate command via `IInstanceCommandGateway`
(local in-process, or Dapr service invocation cross-domain), and their Inbox handler is a durable
**backup**, deduplicated via `ISubItemTerminalGuard`. This is the only event category where a
second delivery path exists by design — every other event has exactly one handler. Full contract,
relay semantics, and the wakeup signal that makes the outbox path near-instant:
`docs/runtime/event-publish-modes.md`.

### Event development checklist
- [ ] Event contract in `*.Events.Contracts/*/Events/` with `[EventName]` (add `ISubflowTerminalEvent`
      only for a new subflow-terminal-class event)
- [ ] Event handler implementing `IEventHandler<TEvent>`
- [ ] Logging extensions in `BBT.Workflow.Domain/Logging/WorkflowLogs.cs`:
  - `{EventName}Received` (Information)
  - `{EventName}IgnoredDomainMismatch` (Debug)
  - `{EventName}Succeeded` (Information)
  - `{EventName}ProcessingFailed` (Error)
- [ ] Handler auto-registered by `AddAetherEventBus` (assembly scanning) — no manual hook registration
- [ ] Use `WorkflowLogs.cs` extension methods — never raw `logger.Log*`
- [ ] If the event implements `ISubflowTerminalEvent`: wire it into `SubflowTerminalRelay`'s
      dispatch switch and tag its Inbox handler's activity `vnext.delivery.role = backup`

### Why the Inbox handler alone is enough
- **Outbox-first**: the outbox row is written before commit succeeds, so a handler always has
  durable work to consume — no in-process shortcut is needed for correctness.
- **Wakeup-assisted**: a loss-tolerant Dapr nudge wakes the Outbox/Inbox poll loops immediately
  after a commit stores a row, so the common case does not wait out the idle poll interval.
- **Idempotency still required**: for the three subflow-terminal events, the relay and the Inbox
  backup may both settle the same terminal outcome — handlers must stay idempotent regardless of
  event category.

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
- Developer-focused implementation docs → `/docs` (index: `docs/README.md`; agent map: `docs/agent-onboarding.md`)
- `/ai-docs` is gitignored local scratch for generated dumps (e.g. vnext-docs staging). It is not committed and is not a source of truth.
- When the user says "add to document", update English docs and ensure Navigation/Overview grouping in `docs/README.md`.

## Context7 Sources
For platform/domain knowledge beyond the code:
- vNext domain: `burgan-tech/vnext-runtime` (tag `vnext-runtime`)
- Aether SDK: `burgan-tech/aether` (tag `aether`)
- Examples: tag `vnext-example`

## File Structure Expectation
```
root/
 ├─ docs/
 ├─ src/
 ├─ test/
 ├─ tools/
 └─ README.md
 (`ai-docs/` is gitignored local scratch; test projects live under `test/` not `tests/`)
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
