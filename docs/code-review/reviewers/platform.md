# Reviewer: platform

Owns everything that is true of **any** C# file in this repository regardless of which domain it
serves: Aether SDK usage, the Result pattern, EF Core efficiency, logging and observability,
dependency injection, async discipline, clean-architecture boundaries, and multi-schema handling.

Source of truth: [`.claude/rules/dotnet-coding-standards.md`](../../../.claude/rules/dotnet-coding-standards.md)
→ [Dependency Map](../../architecture/dependency-map.md) →
[Gateway Routing Strategy](../../architecture/gateway-routing-strategy.md). The `aether` sibling repo
is read-only for this repo: a finding that says "Aether should change" is a **proposal to the user**,
never a change request on this PR.

## 1. Aether SDK (`platform/aether-*`)

- [ ] `platform/aether-crosscutting` — Clock, GuidGenerator, Mapper, Tracing, Logging, Metrics, DistributedCache, DistributedLock, BackgroundJob, UnitOfWork, MultiSchema and Domain Events come from the Aether SDK. A hand-rolled equivalent (`DateTime.UtcNow`, `Guid.NewGuid()`, a bespoke lock) is a WARNING, CRITICAL when it is a lock or a clock inside the pipeline.
- [ ] `platform/aether-uow` — the UoW scope matches the intent: `RequiresNew` where the work must survive the caller's rollback, `IsTransactional = true` where an outbox row and a state write must commit together.
- [ ] `platform/aether-local-feed` — no `aether-local` NuGet source left uncommented in `nuget.config` and no `-local` `AetherPackageVersion` in `Directory.Build.props`. CI cannot restore either. (The evidence reviewer also guards this; duplicates merge.)

## 2. Result pattern (`platform/result-*`)

- [ ] `platform/result-no-throw` — business errors return `Result.Fail(error)`; exceptions are reserved for infrastructure failures and are never control flow.
- [ ] `platform/result-error-codes` — failures carry a structured error code, not only a message.
- [ ] `platform/result-railway` — chains use `Then` / `Map` rather than nested `if (result.IsSuccess)` ladders where the chain is more than two links.
- [ ] `platform/result-payload` — `Result.Ok()` carries a meaningful payload wherever the caller needs data.

## 3. EF Core and query efficiency (`platform/ef-*`)

- [ ] `platform/ef-unused-include` — no navigation property is included that the caller does not consume.
- [ ] `platform/ef-notracking` — read-only and monitoring paths use `AsNoTracking()`.
- [ ] `platform/ef-n-plus-one` — no repository or `DbContext` call inside a loop; batch instead.
- [ ] `platform/ef-detail-load` — `WithDetailsAsync()` is not widened with extra includes without a stated reason; a new `LatestData` reader is added to `PostCommitParentMutationService.NeedsLatestDataForSettle` rather than by re-widening the include.
- [ ] `platform/ef-paging` — large result sets are paged (`PagedResultDto`); `DataList` is loaded only when the response needs it.
- [ ] `platform/ef-tracked-graph` — a detached entity graph is not mixed with a tracked one in the same save, and rows read no-tracking are not attached to a navigation another context also tracks.

## 4. Logging and observability (`platform/logging-*`)

- [ ] `platform/logging-raw` — **no raw `logger.LogInformation/Debug/Warning/Error/Trace/Critical`**. Every log goes through a `LoggerMessage` extension in `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`. This is CRITICAL — it is the repo's most-stated standard.
- [ ] `platform/logging-eventid` — a new `[LoggerMessage]` has a unique EventId following the existing ranges (10xxx transitions, 20xxx instances, 40xxx events).
- [ ] `platform/logging-structured` — structured parameters include the correlation set the log needs: `instanceId`, `flow`, `transitionKey`, `domain`. No string interpolation into the message template.
- [ ] `platform/logging-level` — Debug traces, Information state changes, Warning recoverable, Error failures. An Error log passes the exception object, not only its message.
- [ ] `platform/logging-activity` — a major operation starts an `Activity`; a new `ActivitySource` is registered in `AdditionalSources` in the same commit (see [Trace/Span Tree](../../runtime/trace-span-tree.md)).

## 5. Dependency injection and async (`platform/di-*`, `platform/async-*`)

- [ ] `platform/di-registered` — a new interface implementation is registered, with the right lifetime, and nothing unused is injected.
- [ ] `platform/di-no-servicelocator` — no `IServiceProvider.GetService` inside domain or application logic where constructor injection works.
- [ ] `platform/async-all-the-way` — no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`; all I/O is `async`/`await`.
- [ ] `platform/async-cancellation` — a `CancellationToken` available at the call site is passed through.

## 6. Clean architecture (`platform/arch-*`)

- [ ] `platform/arch-layering` — Domain has no infrastructure dependency; Application depends on Domain only; Orchestration does not reach into Execution internals. A new project reference is checked against [Dependency Map](../../architecture/dependency-map.md).
- [ ] `platform/arch-no-logic-in-shell` — no business logic in controllers, constructors or infrastructure classes.
- [ ] `platform/arch-no-ef-leak` — DTOs do not expose EF entities or navigation properties across a layer boundary.
- [ ] `platform/arch-solid` — SRP (a type doing two jobs), OCP (a `switch` on type where polymorphism fits), DIP (a concrete dependency where an interface exists).
- [ ] `platform/arch-extension-namespace` — an extension class lives in the namespace of the type it extends.

## 7. Multi-schema (`platform/schema-*`)

- [ ] `platform/schema-use` — infrastructure operations that touch flow-scoped data are wrapped in `currentSchema.Use(flow)`; the schema is resolved through `ICurrentSchema`, never read from a static or passed as a bare string into a repository.
- [ ] `platform/schema-background` — a background job, post-commit relay or inbox handler establishes its own schema scope; it cannot inherit the request's.

## 8. Naming and style (`platform/style-*`) — INFO unless it hides a bug

- [ ] `platform/style-naming` — PascalCase types/methods/public members, camelCase locals and private fields, `I` prefix on interfaces, UPPERCASE constants.
- [ ] `platform/style-xmldoc` — public controllers, DTOs, requests/responses and implementation classes carry XML `<summary>`.
- [ ] `platform/style-var` — `var` where the type is obvious; explicit type where it is not.
