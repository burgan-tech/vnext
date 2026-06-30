# Metabase — Analytics Engine (runtime-provided, opt-in)

The vNext runtime **provides** a Metabase container so any domain that wants
business dashboards on its instance data can have one. The runtime is responsible
for the **engine only** — it knows nothing about any domain's flows, schemas, or
reports. Those are owned by each domain repo.

## What lives where

| Concern | Owner | Location |
|---|---|---|
| Metabase **container** (the engine) | **vNext runtime** | this `docker-compose.yml`, `metabase` service |
| DB connection, schema selection, cards, dashboards, seed data | **each domain** | the domain repo, e.g. `vnext-example/etc/docker/config/metabase/` |

vNext deliberately ships **no** dashboard definitions or seed scripts. A grep for
any business term (account-opening, a flow name, a schema name) in this repo's
Metabase config should return nothing.

## Opt-in: it does not start by default

The service is behind a Compose `profiles: ["metabase"]` guard, so a plain
`docker compose up -d` does **not** launch it. A domain that wants dashboards
enables it explicitly:

```bash
cd etc/docker
docker compose --profile metabase up -d metabase
```

Metabase is then available at **http://localhost:3030** (first boot ~2 min).
Domains that don't want analytics simply never enable the profile.

## How a domain uses it

Each domain runs its own vNext instance (its own cluster in production), so each
gets its own Metabase. The domain provisions its dashboards against it:

1. Enable the Metabase profile (above).
2. From the domain repo, run that domain's provisioning, e.g.:
   ```bash
   cd vnext-example
   ./etc/docker/config/metabase/provision.sh
   ```
   The domain script creates its read-only DB user, registers its database +
   schema connection, and builds its own cards and dashboards.

## Production model

Each domain service runs on its own OpenShift cluster with a separate vNext
runtime instance — therefore a **separate Metabase per domain**. This container
definition is the reusable unit the runtime provides; each domain's deployment
enables it (or not) and layers its own dashboard config on top. No domain shares
another domain's Metabase or database.

## Connection notes for domain scripts

- Metabase reaches Postgres over the shared Docker network by service name
  (`vnext-postgres`), port `5432`.
- Each domain has its **own database** (e.g. `vNext_<domain>`); the vNext runtime
  uses **per-flow schemas** within it (workflow name, hyphens→underscores), so a
  domain's reporting queries are schema-qualified, not `public`.
- Domains should connect Metabase with a **read-only** Postgres role.
