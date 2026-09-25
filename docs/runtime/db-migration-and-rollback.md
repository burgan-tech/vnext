# Database Migration and Rollback (DbMigrator)

How the runtime's schemas move forward at deploy time, and how to roll them back with the
DbMigrator `downgrade` command (issue #937). Code is the source of truth:
`workers/BBT.Workflow.DbMigrator/` and `src/BBT.Workflow.Infrastructure/Schemas/`.

## The two migration chains

| Chain | Context | Schemas | History table |
|---|---|---|---|
| Workflow | `WorkflowDbContext` | every system schema (`Runtime:Schemas`) **and** every domain schema discovered from `sys_flows` (one schema per published flow) | `{schema}.__Workflow_Migrations` — one history **per schema** |
| Messaging | `MessagingDbContext` | fixed `sys_queues` (outbox, inbox, background jobs, lock leases) | `sys_queues.__Workflow_Migrations` |

They are independent migration histories with independent migration ids — a rollback names a
target **per chain**.

## Forward (unchanged default)

Running the migrator with no arguments does exactly what it always did: migrate the messaging
chain, then every workflow schema, to this build's latest — system schemas sequentially, domain
schemas in parallel, each under the distributed lock `schema-migration:{schema}`
(`SchemaMigrationOrchestrator`). Migration files are authored against `public`;
`MultiSchemaNpgsqlMigrationsSqlGenerator` rewrites every operation to the target schema at apply
time (raw `Sql(...)` bodies get a `SET search_path` prefix). Each schema catches up individually
from wherever its own history is — a domain jumping several runtime versions applies everything in
between, in id order.

Two other places migrate forward, which matters for rollback ordering: the Orchestration host
migrates the messaging chain **at boot**, and `DefinitionAppService` migrates a newly published
flow's schema with the **running** binary at publish time.

## Commands

```text
(no arguments)      forward migrate everything (the historical behavior, byte-for-byte)
status [--schema S]...
downgrade [--target <migrationIdOrName>] [--messaging-target <migrationIdOrName>]
          [--schema <name>]... [--script <directory>] [--accept-data-loss]
          [--for-runtime-rollback]
```

Every option has a configuration fallback for deployments that cannot pass container args
(the current compose/helm templates pass none): `DbMigrator:Command`, `DbMigrator:Target`,
`DbMigrator:MessagingTarget`, `DbMigrator:Schemas` (comma-separated), `DbMigrator:ScriptDirectory`,
`DbMigrator:AcceptDataLoss` — i.e. `DbMigrator__Command=downgrade` as an environment variable.
Arguments win over configuration. An invalid invocation exits 1 with usage before touching
anything.

- **`status`** — read-only: per schema, the applied head, how many migrations are pending against
  this build, and any applied migrations this build does not know. Use it to find the target id.
  Strictly read-only end to end: the startup side effects of the forward run (dev database creation,
  the messaging-chain forward migration) execute in forward mode only — `status` and `downgrade`
  never trigger them.
- **`downgrade`** — converges every targeted schema to the named migration. EF semantics, on
  purpose: a schema **above** the target reverts (its `Down()` methods run newest-first, one
  transaction per migration, the history row deleted with each), a schema **below** it catches up.
  That makes the command self-healing when schemas sit at mixed levels (a flow published mid-rollback
  is migrated by the running binary, so mixed levels are a real state, not a corner case).
- **`--script <dir>`** — dry run: writes the per-schema SQL (history bookkeeping included, schema-
  qualified exactly as the direct apply would emit it) and applies **nothing**. For review; the
  supported rollback path is the direct apply, which keeps every schema's history consistent.

## The rule that cannot be configured away

**A migration's `Down()` code exists only in the build that shipped it.** An older runtime image
has never heard of the newer migrations: its migrator silently ignores unknown history rows and
no-ops. Rollback therefore always runs from the **newer** image, before the deployment switches to
the older one:

1. Stop (or scale down) the new runtime hosts.
2. Run the **new** migrator image: `downgrade --target <last migration of the version you are going
   back to> [--messaging-target …] --accept-data-loss` (when prompted by the gate).
3. Deploy the older runtime version. Its own migrator run no-ops — every remaining history row is
   one it knows.

The command enforces this direction: when any targeted schema's history contains migrations this
build does not ship, the **whole run refuses** and names them — only the newer build can revert
them. (Pinned by `TargetedSchemaDowngradeTests`.)

## Safety gates

Everything is **planned before anything is applied**: per schema the command logs head → target,
the exact migrations to revert/apply, and the destructive operations in the revert range. Then:

1. **Schemas ahead of this build** → the whole run refuses (see above).
2. **Model-breaking reverts** → refused without `--for-runtime-rollback`. The migrator binary
   carries exactly the compiled EF model the same-version runtime queries with, so it checks every
   `Down()` operation against it: dropping, renaming or re-typing a table/column the model maps
   means a runtime that STAYS on the current version fails with `42703`/`42P01` on every read/write
   of that entity, at first touch (EF selects all mapped columns). The refusal names each offending
   operation and the entity it breaks. `--for-runtime-rollback` is the operator's declaration that
   an OLDER runtime is being deployed right after the command — the one case where the current
   model's needs no longer apply. Index-, constraint- and trigger-only reverts are model-neutral
   and pass without the flag.
3. **Destructive reverts** — any `DROP TABLE` / `DROP COLUMN` / `DROP SCHEMA` in the `Down()`
   bodies to be executed — require the explicit `--accept-data-loss` acknowledgment. That data does
   not come back: converging back up re-creates the shapes, not the rows (e.g. reverting past
   `MoveInstanceIncidentsToTable` drops the incident table; the backfill's `Down` is deliberately a
   no-op). Raw-SQL `Down()` bodies are not classified (in this repo they recreate triggers and
   functions) — the structured drops are the gate. Gates 2 and 3 answer different questions:
   dropping a fresh, still-empty column is model-breaking but loses no data; dropping an unmapped
   legacy table loses data but breaks no model.
4. Target `0` (EF's revert-everything sentinel) is rejected outright, as is a target that does not
   resolve against this build's migrations assembly.
5. `Runtime:EnableSchemaMigration=false` refuses the command the same way it disables the forward
   run.
6. `--script` (dry run) applies nothing, so gates 2 and 3 do not block it — the findings are
   emitted as warnings next to the generated SQL instead. Gate 1 still applies (a script for
   migrations this build does not contain cannot be generated).

Applies run in parallel per schema under the **same** distributed lock as the forward path, so a
downgrade and a forward migration can never interleave on one schema. Exit code contract matches
the forward run: any failed schema ⇒ exit 1, remaining schemas still processed.

## Where a domain runs these commands

The migrator is the one-shot `ghcr.io/burgan-tech/vnext/db-migrator:<version>` image the deployment
already runs at deploy time — the commands are the same container with two extra environment
variables (or args). Pick the image version by the rule above: **the newer one**.

- **vnext-runtime compose** (dev/QA boxes): the migrator service reads
  `docker/domains/<domain>/.env.db-migrator` and mounts `appsettings.DbMigrator.Development.json`.
  One-off run without editing files:
  `DOMAIN_NAME=<domain> VNEXT_VERSION=<newer> docker compose --profile vnext run --rm -e DbMigrator__Command=downgrade -e DbMigrator__Target=<migration> -e DbMigrator__AcceptDataLoss=true vnext-db-migrator`
  (or put the `DbMigrator__*` keys into `.env.db-migrator` and `up vnext-db-migrator`). The sidecar
  is only needed for Vault secrets; the schema lock is Postgres-based.
- **Kubernetes (helm)**: run a one-off Job cloned from the deploy's migrator Job spec — same image
  (the newer tag), same env/secret refs, plus the `DbMigrator__*` variables. No chart change is
  required because everything is env-driven; native arg support in the chart is a follow-up.
- **This repo, locally**: `dotnet run --project workers/BBT.Workflow.DbMigrator -- status` (or
  `downgrade --target …`) with the same environment `etc/docker/run-docker.sh` gives the migrator
  (connection string, `APP_DOMAIN`, store names).

## Downgrading while staying on the same runtime version

The command enforces the safety boundary itself (gate 2 above): a same-version downgrade that
would remove or reshape anything the binary's model maps is **refused with the exact reason**, and
only the explicit `--for-runtime-rollback` declaration overrides it. What passes without the flag —
index-, constraint- and trigger-only reverts — is exactly what a same-version runtime tolerates.
Beyond the gate, know this:

- **The running binary's EF model must fit the schema.** Reverting a migration that drops a
  column or table the binary maps (`AddInstanceType`, `AddInstanceEffectiveStatus`,
  `MoveInstanceIncidentsToTable`, …) makes every query touching it fail at runtime (42703/42P01) —
  that is what gate 2 detects, migration by migration, against the compiled model.
- **It does not stick.** Three forward ratchets push a schema back to the binary's head: the next
  deploy's migrator run, a flow publish (the running Orchestration migrates that flow's schema to
  latest at publish time), and — for the messaging chain — every Orchestration boot.
- **To undo a migration permanently while staying on the version, fix forward**: ship a new
  migration whose `Up()` reverts the change. That is versioned, survives every ratchet, and needs no
  special runbook. Same-version downgrade is an emergency/diagnostic tool for model-neutral reverts,
  not a steady state.

## Finding the target for a runtime version

Migration ids are the canonical target. The last migration shipped by each recent release (derived
from the release commits — verify against the tag when in doubt):

| Runtime | Last workflow-chain migration |
|---|---|
| 0.0.91 | `20260901065706_AddSubflowSettlementMarker` |
| 0.0.92 | `20260906105616_BackfillInstanceIncidents` |
| 0.0.93 | `20260911065311_AddSubStateNotificationSeq` |

`status` on the older image also answers it: everything that image lists as *applied and known* is
its level; a machine-readable version→migration map in vnext-meta is a candidate follow-up once the
release process wants to own it.

## Guide for domain teams

Everything below was executed end to end against a real stack (E2E record 2026-09-22: fresh
database, 59-migration chain, a live orchestration runtime serving traffic during the downgrades).
The outputs quoted are the real ones.

### Recipe 1 — roll back to a previous runtime version (the main case)

You are on runtime N, something is wrong, you want runtime N-1 back — database included.

1. Find the target: run `status` (any image ≥ your current one) and take the last migration of the
   version you are going back to from the table above, or ask the platform team.
2. Stop (scale down) the runtime hosts.
3. Run the migrator — **the CURRENT (newer) image, not the old one**:
   ```
   downgrade --target <migration> [--messaging-target <migration>]
             --for-runtime-rollback --accept-data-loss
   ```
   `--for-runtime-rollback` declares an older runtime deploys right after this command.
   `--accept-data-loss` acknowledges the reverts that drop tables/columns (the refusal lists each
   one before you pass it, e.g. `DROP COLUMN "Instances"."Type"` — that data does not come back).
4. Deploy runtime N-1. Its own migrator run finds nothing to do and no-ops.

Expected success output: one `Downgrade completed for schema <X>` per schema, then
`Downgrade completed successfully for every targeted schema.`, exit 0.

### Recipe 2 — revert harmless migrations while staying on your version

Only index-, constraint- and trigger-only migrations qualify (e.g. a new index from the release is
hurting one of your workloads). The command decides for you: if the revert range is model-neutral it
runs **without any flag**; if it is not, it refuses and names why.

```
downgrade --target <migration>
```

Verified live: with the runtime serving traffic, an index-only 3-migration revert applied cleanly
and the runtime kept answering reads and writes throughout. This state is temporary — the next
deploy's migrator run, or a flow publish, re-applies the reverted migrations. To remove a migration
permanently while staying on the version, ask for a fix-forward (a new migration that reverts it).

### Recipe 3 — review first (dry run)

```
downgrade --target <migration> --script /path/to/dir
```

Writes one reviewable `.sql` file per schema (history bookkeeping included), applies **nothing**,
and prints the model-break / data-loss findings as warnings. The supported apply path is still the
direct command — hand-running the SQL across many schemas risks inconsistent history.

### Every outcome you can see, and what it means

| You see | Meaning | What to do |
| --- | --- | --- |
| `Downgrade completed successfully for every targeted schema.`, exit 0 | Converge done; every targeted schema is exactly at the target. | Nothing. |
| `Schema X: already at <target>; nothing to do` | No-op — that schema was already there. | Nothing. |
| Usage text, exit 1 | Invalid invocation (unknown option, `downgrade` without a target, a downgrade-only option on another command). Nothing ran. | Fix the command line. |
| `Target migration '…' does not exist in this build's migrations assembly` | Typo — or you are running an image older than the target. | Check the spelling; use the newer image. |
| `Target '0' (revert every migration) … is not supported` | EF's full-wipe sentinel is rejected by design. | Name a real migration. |
| `Schema X has applied migrations this build does not contain: … Run the downgrade from the newer runtime image` | The schema was migrated by a newer build; this binary has no `Down()` code for those migrations. The whole run refused, nothing changed. | Re-run from the newer image. |
| `The requested downgrade was refused: N operation(s) remove or reshape tables/columns that THIS build's runtime model maps …` | The revert would break a runtime that stays on the current version (verified live: forcing it produced HTTP 500, `42703: column i.Type does not exist`, on the first read). Nothing changed. | Only if an older runtime deploys right after: add `--for-runtime-rollback`. Otherwise fix forward. |
| `The requested downgrade contains N destructive operation(s) and was refused … <list of drops>` | The revert destroys data permanently; the list names every drop. Nothing changed. | Add `--accept-data-loss` only after reading the list. |
| `Schema migration is disabled (Runtime:EnableSchemaMigration=false); refusing to run` | This deployment's migrator is read-only by configuration. | Run where migration is enabled, or flip the flag deliberately. |
| `Failed to enumerate domain schemas from sys_flows; refusing to run against an unknown schema set …` (with the exception) | Discovery failed — unreachable DB or a database with no sys_flows yet. A rollback that silently misses schemas is worse than one that refuses. | Fix connectivity, or name the schemas explicitly with `--schema`. |
| `Schema X is already being migrated by another instance` | Another migrator holds that schema's lock right now. | Re-run after it finishes. |
| Dapr lock errors + `Downgrade failed for schema X … the run will exit with failure`, exit 1 | The lock service (the migrator's sidecar) was unavailable. Verified live: the failed schemas received **no partial writes**; other schemas still processed. | Restore the sidecar/lock store and re-run — converge makes re-runs safe. |
| After forcing a same-version downgrade: runtime answers 500, logs `42703`/`42P01` | You are running a binary whose model needs columns the schema no longer has. | Run the plain forward migrate; the runtime heals **in place, no restart** (verified live). |

Any failed schema ⇒ exit code 1; the remaining schemas are still processed, and because the command
converges, simply re-running it finishes the job.

## Key source

- Commands and parsing — `workers/BBT.Workflow.DbMigrator/MigratorCommandLine.cs`
- Downgrade/status runner (plan → gates → script or apply) — `workers/BBT.Workflow.DbMigrator/SchemaDowngradeRunner.cs`
- Targeted converge / plan / script primitives — `ITargetedSchemaMigrator`
  (`src/BBT.Workflow.Domain/Schemas/`), implemented by `MultiSchemaMigrator`
  over `MigrationChainInspector` (`src/BBT.Workflow.Infrastructure/Schemas/`)
- Per-schema lock, shared with the forward path — `SchemaMigrationOrchestrator`
- Pinned by `TargetedSchemaDowngradeTests` (real chain against PostgreSQL: revert across a
  table-dropping migration, destructive detection, newer-build refusal, script generation,
  converge back up) and `MigratorCommandTests` (invocation contract).
