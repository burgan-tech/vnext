# Multi-Schema EF Core Model

How `WorkflowDbContext` reaches a flow's PostgreSQL schema without `SET search_path`, and why the
runtime uses **one** EF Core model for every schema while migrations keep a model per schema.

## The two model forms

`WorkflowDbContext` builds its model in one of two forms. Which one depends only on whether its
options carry Aether's `AetherSchemaModelOptionsExtension` (`WorkflowDbContext.UsesSchemaPlaceholderModel`).

| Context | Built by | Extension | Table mapping | Model cache key |
|---|---|---|---|---|
| Runtime unit of work: every repository read and write | Aether `AetherDbContextConfigurator` (`ApplyOwned` / `ApplyShared`), decorated by `DomainAwareDbContextConfigurator` and `SchemaStatementTagDbContextConfigurator` | yes | Aether placeholder `__aether_schema__` | `(type, "__aether_schema__", designTime)`, one model |
| Runtime migration | `MultiSchemaMigrator` (own `UseNpgsql` options) | no | the schema being migrated | EF default, see below |
| `dotnet ef migrations` | `WorkflowDbContextDesignFactory` | no | `public` | EF default |
| Directly resolved (DbMigrator, Development `EnsureCreated`) | Aether `AddDbContext` registration | no | `ICurrentSchema.Name` | `(type, schema, designTime)` |

A runtime command reaches its schema through Aether's `QualifiedNamesCommandInterceptor`. The interceptor
replaces the quoted placeholder with the context's quoted schema just before the command runs. It skips
string literals and checks that the ambient schema still matches the schema the context was created
for. That check, and the interceptor itself, were already active before this model change: every
unit-of-work context has always been built with it. `MessagingDbContext` and `MetricsDbContext` have
used the placeholder model all along.

Raw SQL in the repositories never reads the schema from the model. It reads `ICurrentSchema.Name` or
`WorkflowDbContext.CurrentSchemaName`, so it is the same in both forms. When EF composes raw SQL with
model tables (for example `FromSqlRaw` plus a navigation), the composed tables carry the placeholder
and the interceptor qualifies them in the same command.

## Why one model at runtime

Until 2026-11 the runtime context mapped every table to the real schema, and
`SchemaAwareModelCacheKeyFactory` cached one model per schema. That was introduced on 2026-04-14 to
replace `SET search_path`, before Aether shipped the placeholder model (2026-07). It broke down because
of EF Core's internal cache. In EF Core 10.0.4 (decompiled):

| Item | Cost in the internal `MemoryCache` (`SizeLimit = 10240`) |
|---|---|
| One model | 150 (design-time) + 100 (runtime) |
| One compiled query | 10, compiled separately per model |

With one model per schema, the cache holds `10240 / (250 + 10 × query shapes)` schemas, about 29 for
ten query shapes. Real domains have far more (morph-idm: 98 active flow schemas). Beyond that point
the cache evicts continuously, and the model rebuild and query recompilation run on the request
path, under `ModelSource`'s single global lock. That work happens before the command, so it sits
outside the EF command span and shows up only as unexplained parent self-time. The trace that
surfaced it had 18 ms and 48 ms gaps before the first query on `sys_flows` and `sys_version_registry`.

Measured with a harness that does not use the database (model build + `ToQueryString` of 10 query
shapes per request, skewed traffic over N schemas, single thread):

| | Hit rate | p50 | p95 | p99 |
|---|---|---|---|---|
| Model per schema, 20 schemas | 100 % | 0.43 ms | 0.54 ms | 0.99 ms |
| Model per schema, 30 schemas | 67 % | 0.29 ms | 24 ms | 28 ms |
| Model per schema, 100 schemas | 7.9 % | 19.6 ms | 39 ms | 57 ms |
| **One model, 100 schemas** | **100 %** | **0.49 ms** | **1.25 ms** | **2.1 ms** |

## Migrations are unaffected

- `MultiSchemaMigrator` builds options without the extension, so its model is bound to the schema
  being migrated, as before.
- `MultiSchemaNpgsqlMigrationsSqlGenerator` rewrites the migration files' `public` schema to
  `WorkflowDbContext.CurrentSchemaName`. It reads the context, not the model.
- The design factory still produces `public`, so the model snapshot is unchanged:
  `dotnet ef migrations has-pending-model-changes` reports no changes.

`MultiSchemaMigrator` and the design factory do not replace `IModelCacheKeyFactory`, so EF's default
key `(type, designTime)` applies to them. Within one process, the first schema's model is reused for
every later schema. This was already true before the change and is harmless: the migration SQL comes
from the migration files plus `CurrentSchemaName`, never from the model.
`WorkflowDbContextSchemaModelTests.Migrator_CreatesTheTablesInEachFlowSchema` migrates two schemas in
one process and finds each set of tables in its own schema.

## Traces show the real schema

The OpenTelemetry EF Core instrumentation reads the command text when the command span starts, which is
before any interceptor runs. On the placeholder model it would record `__aether_schema__."Instances"`;
Npgsql leaves the all-lowercase placeholder unquoted. `SchemaStatementTagDbContextConfigurator`, registered by
`TagStatementsWithSchema<T>()`, therefore appends `SchemaPlaceholderStatementTagInterceptor` to every
unit-of-work context. The API hosts and the Outbox worker register it beside `RouteToDomainDatabase<T>()`; the
Inbox worker registers it alone, since it keeps the default database. The interceptor replaces the
placeholder (bare or quoted, in schema position only) in the span's `db.statement` / `db.query.text` tag
with the quoted schema. It leaves the instrumentation's sanitising (`?` literals) and
the command itself untouched. This also fixes the `MessagingDbContext` spans, which used to show the
placeholder.

## Tests

- `test/BBT.Workflow.Infrastructure.Tests/Data/WorkflowDbContextSchemaModelTests.cs` (Testcontainers,
  wired through the hosts' `AddDbContext`): migration per schema, one shared model, isolation on
  transactional and non-transactional roots, one compiled query serving every schema, raw SQL composed
  with the placeholder model, a span recorded by the real OpenTelemetry EF Core instrumentation that
  names the schema, and the misconfiguration case: the placeholder model without Aether's interceptor fails
  closed (`42P01` on `__aether_schema__`, no flow's rows are read).
- `SchemaPlaceholderStatementTagInterceptorTests`: tag rewriting, bare and quoted placeholder.
