# Multi-Domain Hosting

One runtime set — orchestration, execution, inbox and outbox, with their Dapr sidecars — can serve
several domains at once, on **one database**. Until now every domain needed its own full copy; locally
that was about 0.7–1 GB of containers per domain (measured 2026-10-09: four domains, 43 containers,
about 4.6 GB).

The feature is opt-in. With only `APP_DOMAIN` set, nothing changes — not the behaviour, not a single
schema or table name.

## Configuration

| Variable | Meaning |
|---|---|
| `APP_DOMAIN` | **Primary** domain, required. It names this runtime's Dapr app-ids (`vnext-{APP_DOMAIN}-app`, `-execution-app`, …), so the pool is addressed by its primary domain. |
| `APP_DOMAINS` | Comma-separated **co-hosted** domains, optional. Duplicates and the primary are collapsed. |

Every host of the pool — orchestration, execution, inbox, outbox **and the DbMigrator** — gets the same
two values. All of them use one connection string: the primary's database.

Locally:

```bash
cd etc/docker && ./run-docker.sh up core --with partner,sales
cd ../vnext-example
wf domain use partner && wf sync      # every co-hosted domain is registered in wf with the same API and DB
```

`status` shows `also serves: partner,sales`; the record `ai-docs/local-environments/core.md` lists
the co-hosted domains.

## Database layout

| Schemas | Primary domain | Co-hosted domain `mobile` |
|---|---|---|
| Flow schema of flow `navigations` | `navigations` | `mobile_navigations` |
| Definitions (`sys-flows`, `sys-tasks`, …) | `sys_flows`, `sys_tasks`, … | `mobile_sys_flows`, `mobile_sys_tasks`, … |
| Outbox, inbox, jobs, locks | `sys_queues` (shared) | `sys_queues` (shared) |
| Function metrics | `sys_metrics` (shared) | `sys_metrics` (shared) |

- **Co-hosted domains are isolated by schema.** Two domains can have the same flow or component key —
  every domain's own `navigations` flow, for example — without colliding.
- **Backward compatible by construction.** `DomainSchemaNameFormatter` (it replaces Aether's
  `ISchemaNameFormatter`) returns exactly the default name for a single-domain process and for the
  primary domain of a pool. An existing database becomes a pool's primary without any migration.
  `DomainSchemaNameFormatterTests` pins this.
- **Tables inside a schema are unchanged**: the same EF migrations create them.
- **Shared schemas stay shared.** Rows in `sys_queues` and `sys_metrics` carry their domain, the outbox
  and background jobs work unchanged.
- **Name length.** A prefixed name over PostgreSQL's 63 characters is refused rather than truncated
  (truncation could make two schemas collide).
- **Migration.** `DbMigrator` runs both phases — system schemas, then every flow schema listed in that
  domain's `sys_flows` — once per hosted domain. Publish migrates a new flow's schema under the
  component's domain. The `schema-migration:{schema}` lock is keyed on the physical (prefixed) name.

A domain that moves from its own database into a pool as a **co-hosted** domain starts with empty
prefixed schemas; its data is not moved automatically. Re-publish its components and re-seed, or copy
the schemas with a renaming migration of your own.

## How the runtime knows which domain it is serving

| Concern | Behaviour |
|---|---|
| Which domains are served | `IRuntimeInfoProvider.HostedDomains`; `Check` / `IsDomainMatch` mean "hosted here". A request, job or event for another domain is rejected or ignored exactly as before. |
| Domain of the current operation | `IRuntimeInfoProvider.Domain` returns the domain of the current **domain scope** (`DomainScope`, an `AsyncLocal`), or the primary outside any scope. Scripts reading `context.Runtime.Domain` get the domain being served. |
| Who opens the scope | HTTP: `DomainResolutionMiddleware` (route `{domain}`, else `X-Workflow`), after routing and before schema resolution. Publish: `DefinitionAppService` from the component's `domain` (the route has none). Dapr jobs: the five `BackgroundJobs/Handlers/*JobHandler` from `args.Domain`. Component loads: `RuntimeCacheBackend`. Routed gateways and trigger tasks: the target domain. DbMigrator: each hosted domain in turn. **A new entry point must open it before it changes the schema** — the schema name is formatted at that moment. |
| Calls between co-hosted domains | Local. The routed gateways (`Routed*Gateway`) and trigger task executors treat every hosted domain as local and run the call in-process under the target's scope (`LocalDomainCall`, `TriggerTaskExecutorBase.RunLocalScopedAsync`). No Dapr hop, no discovery lookup. |
| Calls to other domains | Unchanged: discovery and Dapr. |
| Discovery registration | Every hosted domain is registered at startup, each under its own lock key, all with the pool's `DAPR_APP_ID`. |
| Events | Topics are unchanged and carry `Domain` in the payload. The inbox guards accept every hosted domain. Each worker app-id has its own consumer group (`consumerID: "{appID}"`). |

## Known limits

- **Not production-ready yet.** The Helm chart still deploys one domain per release.
- **Shared Dapr scopes.** A pool's app-id carries the union of its domains' component and secret scopes.
- **Telemetry.** `service.namespace` no longer separates domains. Filter on `vnext.domain`. Aether's
  outbox and inbox spans do not carry that tag.
- **Script state store.** `custom:{key}` keys (`DaprStateStoreClient`) are not domain-qualified.
- **Edge case.** In a co-hosted domain, a flow whose key already starts with `<domain>-` maps to the same
  schema as the unprefixed key (`mobile-x` and `x` in domain `mobile`); the formatter is idempotent for
  callers that pass a formatted name back.

## Code

- `src/BBT.Workflow.Domain/Runtime/IRuntimeInfoProvider.cs`, `DomainScope.cs`, `DomainSchemaNameFormatter.cs`
- `src/BBT.Workflow.HttpApi.Shared/Middlewares/Runtime/DomainResolutionMiddleware.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/LocalDomainCall.cs`, `Routed*Gateway.cs`
- `src/BBT.Workflow.Infrastructure/Schemas/SchemaMigrationOrchestrator.cs`, `workers/BBT.Workflow.DbMigrator/SchemaMigrationRunner.cs`
- `src/BBT.Workflow.Application/Definitions/DefinitionAppService.cs`, `Caching/RuntimeCacheBackend.cs`
- `orchestration/.../HostedServices/Discovery/DomainDiscoveryInitializationHostedService.cs`
- `etc/docker/run-docker.sh` (`--with`), `etc/workers/*/dapr/components/pubsub.yaml`
