---
paths:
  - "src/**"
  - "orchestration/**"
  - "execution/**"
  - "workers/**"
  - "modules/**"
  - "tools/**"
  - "test/**"
  - "**/*.cs"
  - "**/*.csproj"
---

# .NET Coding Standards (path-scoped: loads with C# code)

Repo-specific rules only — general C#/SOLID/DDD practice is assumed. Layout, hosts and layers:
`AGENTS.md` § Architecture Overview.

## Structure and layering
- Clean Architecture + DDD: aggregates, entities, value objects, repositories, domain events. No
  business logic in controllers, constructors or infrastructure components; never leak EF entities
  across layers. Orchestration must not depend on Execution internals.
- Put an extension class in the namespace of the type it extends (fewer `using`s); extensions usually
  live in `BBT.Workflow.Domain`, not mandatory.
- DI everywhere; `async/await` for all I/O; `BackgroundService` / `IHostedService` for background work.
- Workflows are deterministic; schedules are persistent; transitions follow the pipeline.

## Aether SDK — use it, do not hand-roll
All cross-cutting concerns go through Aether: Clock, GuidGenerator, Mapper, tracing, logging, metrics,
aspects/interceptors, `DistributedCache` / `DistributedLock`, BackgroundJob, Result pattern and error
management, exception handling, MultiSchema, domain events, Unit of Work, OpenTelemetry. Aether lives
in a sibling repo — propose SDK changes, never edit it (`AGENTS.md` § Platform repositories).

## Result pattern and errors
- Business operations return `Result<T>`; exceptions only for infrastructure failures, never control flow.
- Unified error responses come from the global exception-handling middleware; controllers return the
  right HTTP status codes.

## Caching
- Shared state goes through `IDistributedCache` (Redis). An in-process `MemoryCache` is allowed only as
  a deliberate L1 tier in front of it (`ComponentL1Cache`, `DiscoveryL1Cache`, the discovery provider) —
  do not add a new one without the same invalidation story.
- Avoid N+1; include related entities deliberately (include strategy: workflow card).

## Multi-schema
Resolve the schema through `ICurrentSchema` (headers, routes, query string, custom resolvers) and wrap
infrastructure operations in `currentSchema.Use(flow)`.

## Distributed events
How delivery works (outbox, wake-up signal, relay opt-in by DI registration) is `AGENTS.md` § Domain
Events and `docs/runtime/event-publish-modes.md`. EventHook is deleted — do not reintroduce
`IEventPublishHook`, `[EventHook]` or a pre-commit path. Checklist for a new event:
- [ ] Contract in `*.Events.Contracts/*/Events/` with `[EventName]` (no marker interface)
- [ ] `IEventHandler<TEvent>` in `workers/BBT.Workflow.Workers.Inbox/Handlers/` with the domain guard
      `if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain)) return;` plus the standard multi-schema
      and UoW patterns; auto-registered by `AddAetherEventBus`
- [ ] `WorkflowLogs` entries: `{EventName}Received` (Information), `{EventName}IgnoredDomainMismatch`
      (Debug), `{EventName}Succeeded` (Information), `{EventName}ProcessingFailed` (Error)
- [ ] Handler is idempotent (relay and Inbox backup may both deliver)
- [ ] Immediate path needed? Add an `IPostCommitEventRelay<TEvent>`, register it in
      `AddPipelineServices`, tag the Inbox handler's activity `vnext.delivery.role = backup`. Needs a
      durable backup, an order-safe receiver guard, measured latency evidence and a council row.

## Logging — `WorkflowLogs.cs` only
Never call `logger.Log*` directly. Add a `[LoggerMessage]` partial in
`BBT.Workflow.Domain/Logging/WorkflowLogs.cs` with an EventId, a template with structured parameters
(`{InstanceId}`, `{Flow}`, `{TransitionKey}`) and the right level (Debug traces, Information state
changes, Warning recoverable, Error failures). EventId regions: 10xxx transitions, 20xxx instances,
40xxx events, 50xxx discovery. Logs carry `runtimeKey`, `domain` and the correlation id.

```csharp
// BAD
logger.LogInformation($"Processing instance {instanceId}");

// GOOD
logger.InstanceCompletedCleanupEventReceived(instanceId, flow);
```

**The EventId must be unique, and nothing but a test enforces it.** The source generator accepts a
duplicate, Debug and Release both build clean, and the collision only surfaces in a log pipeline —
where a dashboard or alert keyed to that number silently matches two unrelated events. Eighteen such
collisions had accumulated before `WorkflowLogEventIdUniquenessTests` was added; it now fails the build
instead. Two **overloads of the same event** may share an id (today only `JobFailed`); that allowance is
an explicit list in the test, so widening it is a decision someone makes on purpose. Take the next free
id **above your region's own block** rather than filling a gap — a gap may be a retired id that a saved
query still references.

## Telemetry
Start an `Activity` for major operations (span conventions: `docs/runtime/trace-span-tree.md`).

## API documentation
XML summaries are mandatory on controllers, DTOs/requests/responses, interfaces and their methods;
implementation classes also describe their lifecycle and purpose. Controllers follow REST conventions.

## Tests
- `test/` holds **unit tests only** (xUnit). New tests use **NSubstitute** for mocks and **Shouldly**
  for assertions; Moq remains only in existing tests — do not add it to new ones.
- Integration tests live in the sibling vnext-example repo and run against the locally built runtime:
  `AGENTS.md` § Testing and `docs/testing/integration-testing.md`.

## Documentation
Implementation docs go in `/docs` (index `docs/README.md`, agent map `docs/agent-onboarding.md`); "add
to document" means English docs plus the index entry. `ai-docs/` is git-ignored scratch, never a source
of truth. Branches: `feature/`, `hotfix/`, `chore/`.
