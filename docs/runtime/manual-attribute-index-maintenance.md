# Manual attribute index maintenance

Attribute index maintenance is owned by the DBA team. Publishing a Master schema or workflow does
not schedule index work and does not prepare generated columns or attribute indexes. The runtime
contains only a read-only ready-index catalog and query routing; normal EF schema migrations remain
separate. `SchemaMigrationRunner` lives in Infrastructure and DbMigrator invokes it for normal EF migrations.

## Generate SQL in a domain workspace

Use the updated sibling `vnext-workflow-cli` checkout/package, from the directory containing the domain's
`vnext.config.json`:

```sh
wf indexes generate --output ./index-sql
wf indexes generate --flow money-transfer --output ./index-sql
```

The command reads only local files. It does not connect to an API or database, publish definitions,
execute SQL, or hook into `sync`/`update`. Each invocation creates a new batch directory; earlier batches
are preserved. A batch contains one SQL file per workflow, `manifest.json` with source versions and SHA-256
checksums, and DBA execution notes.

Configured workflow and schema folders are discovered recursively. All local versions of a workflow
contribute their Master requirements. Local `latest`, artifact, partial, major and full `-pkg.` references
are resolved using runtime version ordering (artifact before package; build metadata ignored). Missing
references, duplicate component identities, cross-domain references and unsupported indexed fields fail
before any batch is written. Bring referenced dependency schemas into the configured local schema folder.
**An offline generator cannot prove which definitions are deployed**: the DBA/release owner must compare
the manifest with the intended deployed versions before execution.

`x-indexed: true` opts a scalar into physical preparation; `x-filterOperators` and `x-sortable` retain their
existing permissions. Nested scalar string/number/integer/boolean fields are supported; date fields use
`format: "date-time"`. Array, object, reference, conditional and ambiguous indexed paths are rejected.

## What the DBA executes

```sh
psql -X -v ON_ERROR_STOP=1 --dbname=vNext_MyDomainDb --file=index-sql/<batch>/money_transfer.sql
```

Review each file and schedule it in a maintenance window. The SQL:

1. Verifies the existing workflow table; takes a per-flow transaction advisory lock.
2. Locks `InstancesData` in ACCESS EXCLUSIVE mode (default lock wait 5s, editable during review).
3. Validates conversions across current **and historical** data before adding a new projection. Invalid
   values abort the transaction and report the field/type; they are never silently changed to NULL.
4. Adds missing stored generated columns in one ALTER TABLE. The `v1:latest` physical contract and
   `q_<hash>` column names remain compatible with runtime queries. Historical projections are NULL;
   changing `IsLatest` recomputes them. Timestamp conversion explicitly parses ISO-8601 offsets.
5. Compares actual index structure against a PostgreSQL-normalized empty prototype: access method,
   key/include columns, order, collation, operator classes and partial predicate. Equivalent existing
   indexes, including legacy names, are reused. Changed CLI-owned indexes are rebuilt; unmanaged
   name collisions fail. New names contain a readable path, index kind and definition hash (under 63 bytes).
6. Removes obsolete **owned** indexes for updated projections. Records the actual selected index names
   in `AttributeIndexCatalog`, analyzes changed tables, and commits atomically. A replay keeps correct
   index OIDs, skips column rewrites and data rescans, and does not ANALYZE unchanged structures.

This is maintenance-window DDL, not `CREATE INDEX CONCURRENTLY`: readers/writers block while the table
lock is held. Plan disk space, WAL, replicas and duration for stored-column rewrites on large tables.
There is no 100ms maintenance-time guarantee. On failure the transaction rolls back; close/ROLLBACK the
session before replaying. No partial structures are marked ready. `pg_trgm` must be installable in `public`
and the runtime's `tr-TR-x-icu` collation must exist when text-search indexes are requested.

## Type changes and retirement

By default obsolete projections remain available for other active versions. To retire them:

```sh
wf indexes generate --flow money-transfer --retire-obsolete
```

Use this only with **every still-active workflow version** represented locally. Drain runtime readers and
writers for retirement and wait out catalog caches (or restart drained hosts). Retired catalog entries are
marked not ready; their generated expressions and owned indexes are removed while columns/stored values
remain. This stops an obsolete numeric/date cast from rejecting future writes after a type change. Old
columns are never converted in place and no `CASCADE` is issued. A same-name column with an unexpected
marker/type/expression fails rather than silently changing the physical contract. Reintroducing a retired
projection validates data and recreates its stored column.

## Runtime activation and rollback

`AttributeIndexes:Enabled` enables routing to ready projections; its default is false. `DisabledFlows`
can disable individual workflows. These options never enable DDL. Missing/unfinished projections use the
existing JSON query expression; filter authorization remains independent.

`CatalogCacheSeconds` defaults to 30. After the DBA commits, each host discovers readiness on the next
catalog refresh. Roll back routing with `AttributeIndexes:DisabledFlows`, leaving data and columns in
place for a later DBA cleanup. Removed settings: `AttributeIndexPreparation`, maintenance connection,
maintenance timeout options and the `attribute-index.prepare` handler. Aether is unchanged.

## Verification (2026-09-14)

- CLI offline/validation/version tests and PostgreSQL SQL tests are in the CLI repo's `test/indexes*.test.js`.
- Real-host scenario: `vnext-example/api-tests/attribute-index-preparation/test_manual_indexes.py` covers
  publication without jobs/DDL, offline generation, explicit fixture-only DBA execution, replay and a later
  Master update without automatic changes.
- Historical performance files under `docs/performance` are unchanged. This delivery changes maintenance
  ownership and does not claim a new query-performance benchmark.
- The old runtime provisioner survives only as `test/Shared/LegacyAttributeIndexFixture.cs` to reproduce
  earlier query tests. It is not compiled into any runtime or worker assembly. New SQL semantics are tested
  against the CLI output, not against that historical fixture.
