# Multi-Schema Support

## Overview

Aether supports running a single application against many PostgreSQL **schemas** (one per
tenant, module, or data partition) without baking the schema into the EF Core model. The
active schema is an **immutable working context** selected via a nested, auto-restoring
scope, and a Unit of Work isolates each schema-bound `DbContext` on a single shared
connection by applying a `search_path` strategy before every command.

The strategy is explicit and pool-topology-aware: pick a `SchemaSwitchingMode` that matches
how your connection pool is configured (see [Schema switching modes](#schema-switching-modes)).

## Provider support

Multi-schema (per-request schema switching via `ICurrentSchema.Change`) is supported **only on the
PostgreSQL provider** (`BBT.Aether.Npgsql`), which applies `SET LOCAL search_path` per command on the
shared transaction.

**SQL Server (`BBT.Aether.SqlServer`) is single-schema.** It always uses the schema fixed in the EF
model (`HasDefaultSchema` / schema-qualified `ToTable`). On SQL Server, `ICurrentSchema.Change(...)`
does not change the schema used for queries — the call is effectively a no-op for schema resolution.
Applications requiring true multi-schema isolation must use PostgreSQL.

## Database providers

`BBT.Aether.Infrastructure` is **provider-agnostic** — it has no `Npgsql` dependency. The Unit
of Work owns a single shared `DbConnection` / `DbTransaction` and talks to an
[`IAetherDatabaseProvider`](../../src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/IAetherDatabaseProvider.cs)
seam (connection creation, binding options to the shared connection, and the per-schema
strategy). Pick a provider package:

- **`BBT.Aether.Npgsql`** — PostgreSQL, **full multi-schema** (`NpgsqlAetherProvider` +
  `SearchPathCommandInterceptor` + `PostgreSqlIdentifier`). Register with
  `services.AddAetherNpgsql<MyDbContext>(connectionString);` (optionally pass a `SchemaSwitchingMode`).
- **`BBT.Aether.SqlServer`** — SQL Server, **single-schema** (`SqlServerAetherProvider`).
  Register with `services.AddAetherSqlServer<MyDbContext>(connectionString);`.

Both wrap the core registration
`services.AddAetherDbContext<MyDbContext>(provider, connectionString, configure?)`; a custom
provider can be registered through that overload directly.

The multi-schema model below is **PostgreSQL-only** and lives in `BBT.Aether.Npgsql`. See
[SQL Server limitations](#sql-server-limitations).

## The current schema is a scope, not a setting

`ICurrentSchema` exposes the active schema as a stack of scopes backed by `AsyncLocal`. You
do not *set* a schema; you *enter* one with `Change(...)`, which returns an `IDisposable`
that restores the previous schema on dispose:

```csharp
public interface ICurrentSchema
{
    // Top-of-stack schema name, or null if no scope is active.
    string? Name { get; }

    // Push a schema; dispose the returned token to pop it.
    IDisposable Change(string schema);
}
```

```csharp
using (currentSchema.Change("flow_a"))
{
    // currentSchema.Name == "flow_a"  (after formatting)
    using (currentSchema.Change("flow_b"))
    {
        // currentSchema.Name == "flow_b"
    }
    // back to "flow_a"
}
// back to null
```

The scope flows across `await` boundaries (`AsyncLocal`) and restores the previous value on
dispose. Out-of-order disposal throws `InvalidOperationException` ("Schema scope corrupted").

> There is no `ICurrentSchema.Set()`, no `IsResolved` flag, no session-level
> `SET search_path`, and no `NpgsqlSchemaConnectionInterceptor`. Earlier designs used those;
> they have been removed. Schema is applied per command on the shared transaction (see below).

### Schema-name formatting and validation

`Change(schema)` runs the raw name through `ISchemaNameFormatter` first
(`DefaultSchemaNameFormatter` lowercases, replaces spaces/hyphens with `_`, strips other
characters, ensures a leading letter/underscore, and trims to 63 chars). The *formatted*
name is what `Name` returns and what ends up on the connection.

Before a name is interpolated into `SET LOCAL search_path`, it is validated and quoted by
`PostgreSqlIdentifier.QuoteSchema(...)` (regex `^[a-zA-Z_][a-zA-Z0-9_]*$`). An invalid name
throws `InvalidOperationException: Invalid PostgreSQL schema name: <name>`. Schema names
cannot be passed as SQL parameters, so this validate-then-quote step is the injection guard.

## Schema switching modes

Schema isolation strategy is configured explicitly via `SchemaSwitchingMode`. Pass it as the
second argument to `AddAetherNpgsql` (default `TransactionLocal`):

```csharp
services.AddAetherNpgsql<MyDbContext>(connectionString, SchemaSwitchingMode.TransactionLocal);
services.AddAetherNpgsql<MyDbContext>(connectionString, SchemaSwitchingMode.SessionSearchPath);
```

| Mode | Command issued | Requires transaction | Pool topology |
|------|---------------|----------------------|---------------|
| `TransactionLocal` | `SET LOCAL search_path TO "<schema>", public` before every command | **Yes** (`IsTransactional = true`) | PgBouncer transaction pooling ✅, native pool ✅ |
| `SessionSearchPath` | `SET search_path TO "<schema>", public` once + `RESET search_path` at UoW dispose | No (`IsTransactional = false`) | Native Npgsql pool only ✅ |
| `QualifiedNames` | _(not yet implemented — throws `NotSupportedException`)_ | No | PgBouncer session pooling (future) |

**Choosing a mode:**

- **`TransactionLocal` (default):** Use when `IsTransactional = true`. `SET LOCAL` is
  transaction-scoped, so it never leaks to the pool even under PgBouncer transaction pooling.
  This is the safe default for any pool topology.

- **`SessionSearchPath`:** Use for non-transactional, read-heavy flows (e.g. query-only
  services) with the **native Npgsql connection pool** (no PgBouncer). The `search_path` is
  set once per UoW (skipped on subsequent commands to the same schema) and reset with
  `RESET search_path` before the connection is returned to the pool, preventing leakage.
  Round-trip overhead: 1 SET + N queries + 1 RESET — lower than opening a transaction.
  **Do not use with PgBouncer transaction pooling** — the backend may switch between commands
  and the session-level `SET` would apply to a different tenant's queries.

- **`QualifiedNames`:** Planned for PgBouncer transaction pooling without a per-command
  transaction. Not yet implemented.

## How schema isolation works

Within one Unit of Work, **all** schema-bound `DbContext` instances share **one**
`NpgsqlConnection` (see
[`CompositeUnitOfWork`](../../src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs)).
When `IsTransactional = true`, a single `NpgsqlTransaction` is also shared. Contexts are
created lazily and cached by `(DbContextType, Schema)`; the connection (and optionally
transaction) are opened on the first context request.

Isolation comes from `SearchPathCommandInterceptor`, which runs before every EF command.
Behavior depends on the configured `SchemaSwitchingMode`:

### `TransactionLocal` (default)

Prepends `SET LOCAL search_path TO "<schema>", public` to every command. `SET LOCAL` is
**transaction-scoped** — it auto-reverts when the transaction ends, so it never leaks to the
pool. This is why `IsTransactional = true` is required: without an open transaction `SET LOCAL`
would be silently ignored, and the interceptor throws `InvalidOperationException` to guard
against it.

If the same schema is active for consecutive commands, the redundant `SET LOCAL` is skipped
(tracked by `SchemaScopeState.Current`) — a cross-schema switch re-applies it.

### `SessionSearchPath`

Issues `SET search_path TO "<schema>", public` (session-scoped) once per UoW, skipping
subsequent commands that target the same schema. At UoW dispose, `RESET search_path` is
executed on the shared connection before it is returned to the pool, preventing leakage.

No transaction is required. Use with `IsTransactional = false` and the native Npgsql pool.

The `SET` is always run as its own command (same connection) rather than concatenated onto
the command text, because concatenation would add an extra result set and break EF's
rows-affected accounting for INSERT/UPDATE batches.

> Result buffering: a single Npgsql connection cannot have two active readers. Do not stream
> (e.g. `AsAsyncEnumerable` without materializing) across interleaved schema-bound contexts
> within one Unit of Work — EF Core buffers by default, so the normal case is fine.

## EF mappings are schema-agnostic

Map tables with **no schema argument** — schema is resolved at runtime via search_path:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    modelBuilder.Entity<Order>(e =>
    {
        e.ToTable("orders");            // NOT ToTable("orders", "flow_a")
        e.HasKey(o => o.Id);
        e.Property(o => o.Customer).IsRequired();
    });
}
```

**Why:** EF Core caches one compiled model per `DbContext` type. If the schema were part of
the mapping, you would need a distinct model (and cache entry) per schema, and a context
could only ever talk to one schema. Leaving tables unqualified means the same compiled model
serves every schema, and `search_path` selects the schema at execution time.

## Usage

Resolve the schema-bound context from the active Unit of Work via
`IAetherDbContextProvider<TDbContext>` (repositories do this internally). It reads
`currentSchema.Name` and asks the active UoW to materialize the context bound to that schema.

### Transactional (`TransactionLocal` mode — default)

```csharp
using (currentSchema.Change("flow_a"))
await using (var uow = unitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
{
    var db = await dbContextProvider.GetDbContextAsync();   // bound to flow_a on the shared tx
    db.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a" });

    // Cross-schema work in the SAME transaction: enter another scope, resolve again.
    using (currentSchema.Change("flow_b"))
    {
        var dbB = await dbContextProvider.GetDbContextAsync();  // bound to flow_b, same tx
        dbB.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "b" });
    }

    await uow.CommitAsync();   // flow_a + flow_b commit atomically (single transaction)
}
```

### Non-transactional (`SessionSearchPath` mode)

Register with `SchemaSwitchingMode.SessionSearchPath`, then use `IsTransactional = false`:

```csharp
// Registration (Startup / Program.cs):
services.AddAetherNpgsql<MyDbContext>(connectionString, SchemaSwitchingMode.SessionSearchPath);

// Usage:
using (currentSchema.Change("flow_a"))
await using (var uow = unitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false }))
{
    var db = await dbContextProvider.GetDbContextAsync();  // SET search_path TO flow_a, public
    var items = await db.Set<Thing>().ToListAsync();       // no SET repeated (same schema)

    // Switch schema within same UoW:
    using (currentSchema.Change("flow_b"))
    {
        var dbB = await dbContextProvider.GetDbContextAsync();  // SET search_path TO flow_b, public
        var others = await dbB.Set<Thing>().ToListAsync();
    }
    // UoW dispose issues RESET search_path — pool gets clean session
}
```

If `currentSchema.Name` is null when a context is requested, the provider throws
`InvalidOperationException: Current schema is not set.`

### HTTP request path

For ASP.NET Core, register schema resolution and the middleware. `SchemaResolutionMiddleware`
resolves the schema from the request and wraps the rest of the pipeline in
`currentSchema.Change(schema)`; the `[UnitOfWork]` aspect / UoW middleware opens the Unit of
Work. Your controllers/services simply resolve repositories or `IAetherDbContextProvider<T>`.

```csharp
builder.Services.AddSchemaResolution(options =>
{
    options.HeaderKey = "X-Schema";      // from header
    options.QueryStringKey = "schema";   // from query string
    options.RouteValueKey = "schema";    // from route
    options.ThrowIfNotFound = true;      // 400 if missing
});

var app = builder.Build();
app.UseRouting();
app.UseSchemaResolution();          // after UseRouting; wraps the request in Change(schema)
app.UseUnitOfWorkMiddleware();      // after schema is established
app.MapControllers();
```

## PgBouncer (transaction pooling)

**Only `TransactionLocal` mode is safe under PgBouncer transaction pooling.**

`SET LOCAL` is transaction-scoped: when the transaction ends and the connection returns to the
pool, the `search_path` is gone — it never leaks into a connection later handed to another
request. This is asserted directly by
[`PgBouncerSearchPathTests`](../../test/BBT.Aether.Postgres.Tests/PgBouncerSearchPathTests.cs)
("SET LOCAL stayed inside the UoW transaction and never mutated session/pooled state").

`SessionSearchPath` mode issues a session-level `SET`, which **cannot** be used with
PgBouncer transaction pooling — the backend may switch between commands and the schema set by
one request would be visible to another. Use `SessionSearchPath` only with the native Npgsql
pool (direct connections or PgBouncer session pooling).

Rules for `TransactionLocal` under PgBouncer transaction pooling:

1. **Always run inside an explicit transaction** (`IsTransactional = true`). The interceptor
   throws `InvalidOperationException` if no transaction is open — use this to catch
   misconfiguration early.
2. **Keep transactions short.** A connection is leased to the request only while the
   transaction is open.
3. **No external service calls inside an open transaction** (HTTP, broker publishes, etc.).
   Do that work before opening or after committing the Unit of Work.

## SQL Server limitations

SQL Server is supported via `BBT.Aether.SqlServer` (`SqlServerAetherProvider`), but only as a
**single-schema** provider. It supplies the shared connection/transaction and binds
`UseSqlServer`, but does **not** switch schema per command — SQL Server has no
transaction-scoped `SET LOCAL search_path` equivalent.

- **Single-schema only.** Bind the schema in the model — `modelBuilder.HasDefaultSchema("x")`
  or schema-qualified `ToTable("orders", "x")`. There is no runtime per-command schema switching.
- **Runtime cross-schema-in-one-transaction is PostgreSQL-only.** The multi-schema flow above
  (entering several `currentSchema.Change(...)` scopes and writing across schemas in one
  transaction) relies on the transaction-scoped `SET LOCAL search_path` that SQL Server lacks.
- **Outbox/Inbox is not yet supported on SQL Server.** Processing currently uses
  PostgreSQL-specific lease SQL (`FOR UPDATE SKIP LOCKED`, in `EfCoreOutboxStore` /
  `EfCoreInboxStore`). SQL Server support is a follow-up.

## Background pollers are single-schema

The outbox/inbox processors operate on **one configured schema per instance**. Set the
schema on the options:

```csharp
services.Configure<AetherOutboxOptions>(o => o.Schema = "flow_a");
services.Configure<AetherInboxOptions>(o => o.Schema = "flow_a");
```

`AetherOutboxOptions.Schema` / `AetherInboxOptions.Schema` (in `BBT.Aether.Events`) tells the
processor which schema's table to handle; it opens a UoW bound to that schema via
`currentSchema.Change(options.Schema)` on every run. There is no ambient schema in a
background worker, so if `Schema` is null/empty the processor logs a warning and does
nothing. For multi-schema deployments, **run one processor instance per schema**.

## Related Features

- [Unit of Work](../unit-of-work/README.md) — shared-connection transaction management
- [Repository Pattern](../repository-pattern/README.md) — data access
- [Domain Events](../domain-events/README.md) — outbox dispatch within the shared transaction
