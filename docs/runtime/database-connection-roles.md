# Database Connection Roles

Every host reads one PostgreSQL connection string, `ConnectionStrings:Default`. With `Vault:Enabled=true`
all hosts load the **same** Dapr secret store (`DAPR_SECRET_STORE_NAME`, default `vnext-secret`), and the
secret store is added after environment variables, so a per-host `ConnectionStrings__Default` in Helm
cannot override it. A **connection role** lets one host use a different user or connection settings
from the same secret.

## Rule

| Host | Role key | When the role key is missing or blank |
|------|----------|---------------------------------------|
| Inbox worker | `ConnectionStrings:Inbox` | `ConnectionStrings:Default` |
| Outbox worker | `ConnectionStrings:Outbox` | `ConnectionStrings:Default` |
| DbMigrator | `ConnectionStrings:Migrator` | `ConnectionStrings:Default` |
| Orchestration | — (always `Default`) | — |

- A non-empty role key **replaces `ConnectionStrings:Default` for that process only**. Every consumer of
  `Default` in the process — the DbContexts, the Postgres lock provider, the attribute index, health
  checks — gets the same string, so a unit of work never spans two connection strings.
- No role key → behaviour is unchanged. The general case stays one user, one configuration.
- `ConnectionStrings:Domains:<domain>` (co-hosted domain databases) is not affected by roles.
- `Application Name` is still added when the chosen string has none; a name written in the role string wins.
- The key can come from any configuration source: the Vault secret (key `ConnectionStrings:Inbox`),
  appsettings, or an environment variable (`ConnectionStrings__Inbox`). The latest source wins, as usual.

Code: `DatabaseConnectionDefaultsConfigurationExtensions.ApplyDatabaseConnectionDefaults` in
`BBT.Workflow.HttpApi.Shared`, called from each host's `Program.cs` right after the secret store.

## Example (Vault secret)

```json
{
  "ConnectionStrings:Default": "Host=pgbouncer;Database=vNext;Username=vnext_app;Password=...",
  "ConnectionStrings:Outbox":  "Host=pgbouncer;Database=vNext;Username=vnext_outbox;Password=...;Maximum Pool Size=200",
  "ConnectionStrings:Migrator": "Host=postgres;Database=vNext;Username=vnext_ddl;Password=..."
}
```

Here the Outbox worker uses `vnext_outbox`, the DbMigrator `vnext_ddl`, and the Inbox worker and
Orchestration `vnext_app`.

## Operational notes

- **Separate users isolate PgBouncer pools.** PgBouncer pools per (database, user); a dedicated user
  keeps a high-volume worker from consuming the server connections of the others. Changing only
  `Maximum Pool Size` does not give that isolation.
- **Grant what the host really touches.** Inbox handlers write instance data in every flow schema, not
  only `sys_queues`; the Outbox worker reads and updates `sys_queues`. Flow schemas are created later by
  the DbMigrator, so grant through `ALTER DEFAULT PRIVILEGES` for the migrator's user, or the first new
  flow fails with a permission error.
- **No secret isolation.** Every host loads the whole secret, so the Outbox password is present in the
  Orchestration process too (unused). If a credential must be invisible to other pods, give that host its
  own secret path and Dapr component and point `DAPR_SECRET_STORE_NAME` at it.
