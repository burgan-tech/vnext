# Unit of Work Pattern

## Overview

A Unit of Work (UoW) groups all database work for one logical operation so it commits or
rolls back together. In Aether the UoW is backed by a **single shared `DbConnection` and a
single `DbTransaction`**: every `DbContext` it hands out enlists on that one transaction.
This is what makes multi-schema work atomic — writes to several schemas in one UoW commit as a
unit. See [Multi-Schema Support](../multi-schema/README.md) for the schema-isolation details.

### Database providers

`BBT.Aether.Infrastructure` is **provider-agnostic** — it has no `Npgsql` dependency. The UoW
talks to an
[`IAetherDatabaseProvider`](../../src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/IAetherDatabaseProvider.cs)
seam that owns connection creation, binding options to the shared connection, and the
per-schema strategy. Pick a provider package and register it (see [Registration](#registration)):

- **`BBT.Aether.Npgsql`** — PostgreSQL, full multi-schema. `AddAetherNpgsql<T>(connectionString)`.
- **`BBT.Aether.SqlServer`** — SQL Server, single-schema. `AddAetherSqlServer<T>(connectionString)`.

A custom provider can be registered through the core overload
`AddAetherDbContext<T>(provider, connectionString)`. Full runtime cross-schema transactions and
Outbox/Inbox processing are **PostgreSQL-only** today — see the multi-schema doc's
[Provider support](../multi-schema/README.md#provider-support) (SQL Server is single-schema;
`ICurrentSchema.Change(...)` is a no-op for schema resolution there) and
[SQL Server limitations](../multi-schema/README.md#sql-server-limitations).

## Getting a DbContext

You do **not** inject a DI-scoped `DbContext`. Contexts come from the active UoW, bound to the
current schema, on the shared connection. Resolve them through
`IAetherDbContextProvider<TDbContext>` (repositories use this internally):

```csharp
public interface IAetherDbContextProvider<TDbContext> where TDbContext : DbContext
{
    Task<TDbContext> GetDbContextAsync(CancellationToken cancellationToken = default);
}
```

`GetDbContextAsync` reads the active schema from `ICurrentSchema`, finds the ambient UoW, and
asks it to materialize (and cache) the schema-bound context. Multi-schema usage:

```csharp
using (currentSchema.Change("flow_a"))
await using (var uow = unitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
{
    var db = await dbContextProvider.GetDbContextAsync();   // bound to flow_a on the shared tx

    // Additional schemas in the SAME transaction via further Change(...) scopes:
    using (currentSchema.Change("flow_b"))
    {
        var dbB = await dbContextProvider.GetDbContextAsync(); // bound to flow_b, same tx
        // ... work ...
    }

    await uow.CommitAsync();   // flow_a + flow_b commit atomically
}
```

Contexts are cached by `(DbContextType, Schema)`, so requesting the same pair twice returns
the same instance.

## `Begin` (synchronous) vs `BeginAsync`

```csharp
IUnitOfWork        Begin(UnitOfWorkOptions? options = null);
Task<IUnitOfWork>  BeginAsync(UnitOfWorkOptions? options = null, CancellationToken ct = default);
```

Both create (or participate in) a UoW. The difference is **ambient (AsyncLocal) propagation**:

- **`Begin` (synchronous)** establishes the UoW in the **caller's own execution frame**, so the
  ambient assignment flows into the caller's subsequent continuations. Provider-backed stores
  and repositories that resolve their `DbContext` via the ambient UoW will see it. **Use `Begin`
  for programmatic / background code** (background jobs, inbox/outbox processors, dispatchers)
  that then resolves repositories or contexts in the same method. Initialization does no real
  async work (the connection/transaction open lazily on first context creation), so nothing is
  lost by going synchronous.

- **`BeginAsync`** performs its ambient assignment *inside an async state machine*, which does
  **not** propagate back to the caller's continuation. This is an AsyncLocal-in-async
  limitation, not a bug. Use it where you control the work within the awaited call (e.g.
  `ExecuteInUowAsync`), not where you need the UoW ambient for later calls in the same method.

- **HTTP requests are unaffected.** The request path uses the UoW middleware `Prepare` +
  `[UnitOfWork]` aspect, which establishes the ambient UoW correctly for the request pipeline.

`BeginRequiresNew(...)` is a convenience that begins a transactional `RequiresNew` UoW; it has
both a synchronous overload (`Begin`) and an async overload (`BeginAsync(ct)`).

### Why disposal is async-only

The unit of work is `IAsyncDisposable` (there is intentionally no synchronous `IDisposable`).
Creation — `Begin` — is synchronous and does no I/O: it only allocates the scope and sets the
ambient `AsyncLocal` (the database connection opens lazily on the first `GetDbContextAsync`).
Disposal, however, performs real network I/O: it rolls back an uncommitted transaction and
closes the shared `DbConnection`/`DbTransaction`. A synchronous `Dispose` would block the
calling thread on that teardown — harmful under async request pipelines and PgBouncer
transaction pooling. So the correct shape is **synchronous `Begin` + asynchronous
`Commit`/`Rollback`/`Dispose`**: always `await using var uow = unitOfWorkManager.Begin(...)`.

```csharp
// Background / programmatic: ambient must propagate to later calls -> Begin (sync)
await using var uow = uowManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
var db = await dbContextProvider.GetDbContextAsync();   // sees the ambient UoW
await uow.CommitAsync();
```

## UnitOfWorkOptions

```csharp
public class UnitOfWorkOptions
{
    public bool             IsTransactional  { get; set; } = false;
    public IsolationLevel?  IsolationLevel   { get; set; }              // default ReadCommitted
    public UnitOfWorkScopeOption Scope        { get; set; } = UnitOfWorkScopeOption.Required;
    public int              MaxDbContextCount { get; set; } = 16;
}
```

- **`IsTransactional`** — `true` opens a `DbTransaction` on the shared connection before the
  first context is handed out; `false` runs without a transaction. The default is `false` on
  the struct, but the **HTTP middleware default is `true`** (see [HTTP middleware](#http-middleware)).
  This flag is meaningful: a non-transactional UoW never calls `BeginTransaction`, which is
  required for `SessionSearchPath` mode and lighter-weight read-only flows.
- **`Scope`** — `Required` (join an existing UoW or create one), `RequiresNew` (always an
  independent UoW), or `Suppress` (non-transactional).
- **`IsolationLevel`** — applied when the shared transaction is opened (defaults to
  `ReadCommitted`). Ignored when `IsTransactional = false`.
- **`MaxDbContextCount`** — guardrail on the number of distinct `(DbContextType, Schema)`
  contexts materialized in one UoW (default 16). Exceeding it throws.

### Scope options with `[UnitOfWork]`

```csharp
[UnitOfWork(Scope = UnitOfWorkScopeOption.Required)]     // join or create (default)
[UnitOfWork(Scope = UnitOfWorkScopeOption.RequiresNew)]  // independent transaction
[UnitOfWork(Scope = UnitOfWorkScopeOption.Suppress)]     // non-transactional
```

## Registration

```csharp
// PostgreSQL (BBT.Aether.Npgsql) — full multi-schema, TransactionLocal mode (default)
services.AddAetherNpgsql<MyDbContext>(connectionString);

// PostgreSQL — non-transactional SessionSearchPath mode (native Npgsql pool, no PgBouncer)
services.AddAetherNpgsql<MyDbContext>(connectionString, SchemaSwitchingMode.SessionSearchPath);

// SQL Server (BBT.Aether.SqlServer) — single-schema
services.AddAetherSqlServer<MyDbContext>(connectionString);
```

`AddAetherNpgsql` accepts an optional `SchemaSwitchingMode` (second parameter, default
`TransactionLocal`). See [Multi-Schema Support](../multi-schema/README.md#schema-switching-modes)
for when to choose each mode.

Both Npgsql/SqlServer overloads wrap the core overload, which selects the provider explicitly:

```csharp
services.AddAetherDbContext<MyDbContext>(
    new NpgsqlAetherProvider(SchemaSwitchingMode.TransactionLocal),   // or SqlServerAetherProvider()
    connectionString,
    (sp, options) => { /* optional extra EF Core configuration */ });
```

The **connection string is captured** so the UoW can open the single shared connection it
hands contexts out from; the optional `configure` delegate
(`Action<IServiceProvider, DbContextOptionsBuilder>`) is captured (and re-applied with the
shared connection bound) for each schema-bound context.

`AddAetherDbContext` calls `AddAetherUnitOfWork<TDbContext>()`, which registers the ambient
accessor, `IUnitOfWorkManager`, the domain-event sink, and `IAetherDbContextProvider<>`. Call
`AddAetherUnitOfWork<TDbContext>()` directly only if you register the DbContext another way.

### HTTP middleware

The `UnitOfWorkMiddleware` opens a UoW for every non-excluded HTTP request. Configure it via
`UnitOfWorkMiddlewareOptions`:

```csharp
builder.Services.Configure<UnitOfWorkMiddlewareOptions>(options =>
{
    // Default UoW options applied to every request (IsTransactional defaults to true here):
    options.DefaultOptions = new UnitOfWorkOptions
    {
        IsTransactional = true,
        Scope = UnitOfWorkScopeOption.Required,
        IsolationLevel = IsolationLevel.ReadCommitted,
    };

    // HTTP methods that skip the UoW:
    options.ExcludedMethods.Add("GET");

    // Path prefixes that skip the UoW (wildcards supported):
    options.ExcludedPathPrefixes.Add("/health*");

    // Dynamic exclusion predicate:
    options.ExcludeWhen = ctx => ctx.Request.Path.StartsWithSegments("/hangfire");
});

app.UseUnitOfWorkMiddleware();
```

> **Default:** `IsTransactional = true`, `Scope = Required`. The middleware default is
> explicitly transactional so that `TransactionLocal` schema switching (which requires an open
> transaction) works without further configuration.

## Commit pipeline

`CommitAsync` runs the following on the single shared transaction:

1. **SaveChanges** across every materialized context that has pending changes.
2. **Domain-event dispatch.** With the default `AlwaysUseOutbox` strategy, raised domain events
   are written to the **outbox table inside the same shared transaction** (then a second
   SaveChanges persists those rows). With the direct-publish strategy, the transaction commits
   first and events are published after, falling back to the outbox in a new scope on failure.
3. **Commit** the single transaction (business data + outbox rows together under
   `AlwaysUseOutbox`).
4. **`OnCompleted` hooks** run after a successful commit.

On rollback (or dispose without commit) the transaction is rolled back and `OnFailed` hooks
run. `OnCompleted`, `OnFailed`, and `OnDisposed` hooks are all still available:

```csharp
uow.OnCompleted(async u => { /* after successful commit */ });
uow.OnFailed(async (u, ex) => { /* after rollback / failed commit */ });
uow.OnDisposed(u => { /* during disposal */ });
```

This is exercised end-to-end by
[`OutboxWithinSharedTransactionTests`](../../test/BBT.Aether.Postgres.Tests/OutboxWithinSharedTransactionTests.cs):
an event raised by an aggregate lands in the outbox in the same transaction as the business
data, and a rollback discards both.

## Guardrail errors

| Message | When |
|---------|------|
| `Current schema is not set.` | A context is requested with no active `currentSchema.Change(...)` scope. |
| `No active UnitOfWork.` | A context is requested with no ambient UoW (common when using `BeginAsync` where the ambient does not propagate to the caller — use `Begin`). |
| `UnitOfWork DbContext limit exceeded. Limit: N` | More than `MaxDbContextCount` distinct `(Type, Schema)` contexts in one UoW. |
| `Invalid PostgreSQL schema name: X` | The active schema name fails the PostgreSQL identifier check before a `SET` command. |
| `TransactionLocal mode requires IsTransactional = true.` | `SchemaSwitchingMode.TransactionLocal` was used with `IsTransactional = false`. Either set `IsTransactional = true` or switch to `SessionSearchPath` mode. |

## Programmatic helpers

```csharp
// Commit on success, roll back on exception (async work owned inside the lambda):
await uowManager.ExecuteInUowAsync(async ct =>
{
    var db = await dbContextProvider.GetDbContextAsync(ct);
    // ... work ...
}, new UnitOfWorkOptions { IsTransactional = true });
```

## Domain Events Integration

Domain events raised on aggregates are dispatched at commit (see the commit pipeline above):

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void PlaceOrder()
    {
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id));
    }
}
```

## Best Practices

1. **Use `Begin` (sync) for background/programmatic code** so the UoW is ambient for the
   repository/context calls that follow in the same method.
2. **Keep transactions short** and make **no external service calls** inside an open
   transaction (required for PgBouncer transaction pooling — see Multi-Schema docs).
3. **Use `RequiresNew` deliberately** for independent operations (e.g. lease/audit writes).
4. **Don't qualify tables with a schema** in EF mappings — schema is resolved at runtime.

## Related Features

- [Multi-Schema Support](../multi-schema/README.md) — schema isolation on the shared connection
- [Aspects](../aspects/README.md) — `[UnitOfWork]` attribute details
- [Repository Pattern](../repository-pattern/README.md) — data access with UoW
- [Domain Events](../domain-events/README.md) — outbox dispatch within the shared transaction
