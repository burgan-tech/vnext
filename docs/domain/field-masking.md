# Field Masking and Encryption (`x-masking`, `x-encryption`)

Data-schema property keywords that control how an instance-data field is stored and shown. Issue
[burgan-tech/vnext#135](https://github.com/burgan-tech/vnext/issues/135); council sessions
`2026-09-28-field-masking-x-masking` and `2026-09-29-field-encryption-encrypt`, with the committee amendment of
2026-09-30 (see `docs/agent-council/sessions/README.md`).

## Three controls, three keywords

| Control | Keyword | Where it acts |
|---|---|---|
| Hide a field from some callers | `x-roles` | read (data function, sync response) |
| Mask a visible field | `x-masking` | read (data function, sync response); stored data never changes |
| Hash a field | `x-encryption` `type: "hash"` | **write** — the stored value is the digest |
| Encrypt a field | `x-encryption` `type: "encrypt"` | **write** (AES-256-GCM token in instance data) + read (decrypted for the engine and for allow-listed callers) |

The read order is fixed: **`x-roles` → `x-masking` → `x-encryption`**. A property `x-roles` hides is pruned,
and nothing else looks at it. A property carries at most one transform: `x-masking` next to an active
`x-encryption` is rejected at publish. The ordering is structural in the writer (`InstanceDataRoleFilter.Apply`):
a pruned path is skipped before its value is read.

## Where the read-side controls apply — and where they do not

| Surface | `x-roles` | `x-masking` | `encrypt` | `hash` |
|---|---|---|---|---|
| Data function, sync start/transition response | pruned | allow-listed → clear, others masked | allow-listed → plaintext, others the stored token; legacy plaintext → full mask | stored digest; legacy plaintext → full mask |
| **Instance GET / list** | **not applied** | **not applied** | **stored token** | **stored digest** |
| Trigger tasks (`GetInstance`, `GetInstances`, `GetInstanceData`, local) | not applied | not applied | plaintext (engine view) | digest |

**Instance GET and list serve data exactly as stored** (committee decision 2026-09-30, revisited in Phase 2):
no `x-roles` pruning, no masking, no decryption. This removes the `x-roles` pruning those two endpoints applied
before this release — a field hidden by `x-roles` is visible there until Phase 2 (`vnext-meta` deprecations and
known-issues). Encrypted fields show their token and hashed fields their digest, so neither leaks.

Other surfaces that see the engine's view (plaintext for `encrypt`, digest for `hash`, clear for masked fields):
extensions, output mapping, `context.Related` and every script context; the human-task list
(`humanTask.title`/`description`); outbox events and internal endpoints (network isolation is the boundary);
local trigger-task reads (`SystemRead`). Cross-domain trigger tasks go through the remote instance GET and receive
the stored form. A value a script copies into another field loses its protection.

## `x-masking`

```json
"iban": {
  "type": "string",
  "x-masking": {
    "operator": "mask",
    "params": { "keepFirst": 2, "keepLast": 4, "maskingChar": "*" },
    "roles": [ { "role": "morph-idm.auditor", "grant": "allow" } ]
  }
}
```

| Field | Meaning |
|---|---|
| `operator` | `mask` or `replace` |
| `params` (`mask`) | `keepFirst`, `keepLast` (non-negative, default 0), `maskingChar` (one character, default `*`) |
| `params` (`replace`) | `value` (required, non-empty string) |
| `roles` | **Allow-only exemption list**. A caller matching an `allow` grant sees the raw value |

### `roles` is an allow-only exemption list

The transform applies to every caller **unless** the caller matches an `allow` grant. Every other outcome keeps
it transformed (fail closed):

| Caller | Result |
|---|---|
| holds an allow-listed role (alone or with others) | raw |
| holds only unlisted roles | transformed |
| holds a misspelled role (`morph-idm.audtor`) | transformed |
| has no roles | transformed, unless an identity-bound grant (`$InstanceStarter`, `$user.…`) matches it |
| role resolution failed | transformed (every rule applies, every `x-roles` field is pruned) |
| no `roles` declared | transformed for everyone |

A `deny` grant is rejected at publish. The exemption is decided with the canonical
`IRoleGrantEvaluator.IsAnyRoleAllowed` over the allow list; a caller with no roles can never satisfy a role-bound
grant (a static role or `$role.`) — a `$role.` value resolving to `""` must not match the empty role name a
role-less caller is evaluated with (`TransitionAuthorizationManager.IsUnprovableRoleBoundGrant`). (The same list
was spelled as deny grants during development; behaviour is unchanged, only the wording.)

### `mask` semantics

- `keepFirst` and `keepLast` count Unicode scalar values, so a surrogate pair is never split.
- The middle is masked **per UTF-16 code unit**, so an emoji becomes two masking characters.
- The output has the same length as the input. Length stays observable, so use `replace` for values whose length
  is itself sensitive.
- If `keepFirst + keepLast` is at least the value length, the value is returned **unchanged**.

## `x-encryption`

| Field | Meaning | Runtime |
|---|---|---|
| `type` | `none`, `hash`, `encrypt` (exactly, lower case) | `persisted`/`transport` were removed — rejected at publish, use `encrypt` |
| `params.algorithm` | `sha256` (default) or `sha512` | `hash` only; `encrypt` takes no params |
| `roles` | allow-only exemption list, same semantics as `x-masking.roles` | `encrypt` only (read path); rejected on `hash` |
| `purpose` | data classification (`PII`, `PII-Identification`, …) | metadata, not enforced |
| `redactInLogs` | the value must never be logged | metadata; the runtime's own logs never carry instance data |
| `retentionDays` | retention period | metadata, not enforced |

### Per-instance secrets (`InstanceSecrets`)

Each instance gets its own AES-256 key and hash salt, generated by the runtime (`RandomNumberGenerator`) on the
instance's first protected write and stored in its flow schema's `InstanceSecrets` table
(`InstanceId` PK → `Instances`, cascade). There is no key or salt in configuration, Vault or the schema.

- **Stored in plaintext by decision**: database security is owned by the DB team; the runtime never serves the
  values on any API, log, span or cache. **Consequence**: someone who can read the database can decrypt every
  value — `encrypt` protects API exposure and single-table dumps, not a database reader.
- **Crypto-shredding**: deleting an instance deletes its secret; deleting only the secret row makes the instance's
  encrypted values unrecoverable (reads keep serving the token, transitions answer 503 `Instance:100040`, the instance
  is neither faulted nor given an incident). **It takes effect per pod once that pod's L1 entry is gone** — sliding
  expiry (`SecretCacheSlidingMinutes`) or a restart; until then a pod that already held the secret keeps opening the
  values. Measured on the lab: same pod after the delete → plaintext; after a restart → token + 503.
- **Creation** happens only in the write funnel, inside its transaction and under its per-instance row lock, in one
  statement (`INSERT … ON CONFLICT DO NOTHING RETURNING` unioned with a read of the existing row), so concurrent first
  writes produce exactly one row. Should that statement return nothing (a row committed by another transaction after its
  snapshot — the row lock rules it out on this path), a plain read follows. The read path never creates one.
- **Cache**: `InstanceSecretStore` keeps secrets in a private, size-bounded in-process `MemoryCache` — the L1 —
  and deliberately **never in Redis** (`SchemaEncryption:SecretCacheEntries`, `SecretCacheSlidingMinutes`).
  Secrets never change, so an entry cannot be stale. Entry points preload them in one query
  (`IInstanceSecretPreloader`: transition context load, instance GET/list, data function); a row opened without a
  preload falls back to a synchronous lookup on its own pooled connection (Debug log 20471). A token that fails to
  authenticate under a cached secret evicts it and is retried once against the database.

### `hash` — applied on write

```json
"tckn": { "type": "string", "x-encryption": { "type": "hash", "purpose": "PII-Identification" } }
```

The write funnel replaces the value with `HASHED:SHA256:<hex>` — HMAC-SHA256 keyed by the instance salt
(`HASHED:SHA512:` for `sha512`). The raw value is gone: the engine sees the digest too, and nobody can be shown the
raw value, so `hash` takes no `roles`.

- **Deterministic within an instance**: an unchanged value keeps its digest (a value that already carries the
  prefix is not re-hashed) and dedup stays exact.
- **Different across instances** by construction (per-instance salt): digests cannot be matched between
  instances.
- **Validation**: every later write re-validates the merged document, which then holds the digest, so a `hash`
  property may not declare `pattern`, `format`, `minLength`, `maxLength`, `enum` or `const` (rejected at publish).
- **Read**: the stored digest is served as is. A value without the prefix (written before the rule existed) is
  fully masked on the data function; instance GET/list show it as stored.

### `encrypt` — AES-256-GCM at rest

```json
"email": { "type": "string", "x-encryption": { "type": "encrypt",
  "roles": [ { "role": "morph-idm.auditor", "grant": "allow" } ], "purpose": "PII" } }
```

| Where | What is there |
|---|---|
| `InstancesData."Data"` column | `ENCRYPTED:AES256:i1:<base64url(0x01 ‖ nonce ‖ ciphertext ‖ tag)>` |
| Engine (scripts, mappings, conditions, tasks, schema validation, extensions) | plaintext |
| Data function / sync response, allow-listed caller | plaintext |
| Data function / sync response, every other caller | the stored token |
| Data function / sync response, value written before the field became `encrypt` | full mask |
| Instance GET / list | the stored token |

**Construction.** BCL `AesGcm` with the instance key, 12-byte random nonce, 16-byte tag; `i1` names the scheme
(instance key, version 1). The additional authenticated data is `vnext.idata.v1|i1|<instanceId>|<path>`, so a token
copied to another field or instance does not open. It is deliberately not bound to the row: an unchanged value
carries its token forward, and a client can echo a token back.

**Storage model.** `InstanceData.StoredData` is the only EF-mapped content member and always holds the stored
form; `InstanceData.Data` is the unmapped engine view. No EF tracking state — `Add`, fixup, change detection, the
detached `Set.Update(graph)` of the retry/fault scopes — can write plaintext into the column. Content and
`DataHash` are ignored after save, and `EfCoreInstanceRepository.UpdateAsync` attaches a detached aggregate's rows
`Unchanged`. `InstanceDataProtectorInterceptor` hands each materialized row the protector and its flow schema; the
row opens its tokens on its first `Data` read. Decryption is driven by the token prefix, never by the current
schema.

### Write rules (both types)

Funnel order: open the head → sanitize the request → merge → **hash** `hash` paths → dedup → validate →
**encrypt** `encrypt` paths → persist.

- A request may not introduce a string starting with `ENCRYPTED:AES256:` or `HASHED:` (400 `Instance:100041`),
  except the very value already stored at the same path (an echoed token becomes a no-op, an echoed digest stays).
  Checked in request validation (`TransitionValidationService.ValidateSchemaAsync`), before admission, so a refusal
  never faults the instance. The funnel repeats it for values a mapping produces, with one difference: there
  `HASHED:` is reserved only on the schema's hash paths. The engine sees digests, so a script copying a hashed field
  into another one writes an ordinary string (an integration run faulted on exactly that before the rule was scoped).
  `ENCRYPTED:` stays reserved everywhere, because decryption is prefix-driven.
- `DataHash` of a row that carries tokens is a keyed HMAC-SHA256 under the instance key (40 hex).
- The schema cannot be resolved while the instance's head already carries protected values → 503
  `Instance:100043`, never a plaintext write.
- An undecryptable value (secret row missing or token tampered) refuses the transition before any task runs —
  503 `Instance:100040`, the instance is neither marked Busy nor faulted. `WorkflowLogs.EncryptedValueUndecryptable`
  (20468) names instance and path, never the value.

### Queries

Filter, sort, groupBy and aggregation on an `encrypt` path (or an ancestor, whose `@>` containment would reach
it) are refused on the list endpoint (400 `Validation:900010`) whatever `EnforceMasterSchemaFiltering` says.
Publish rejects `x-masking`/`hash`/`encrypt` together with `x-filterOperators`, `x-sortable` or `x-indexed`.
**Gap:** event selectors (`FindByFilterAsync`) carry no schema context and are not guarded.

### Honest scope

Only `InstancesData` rows written after a field became `hash`/`encrypt` are protected. Still in plaintext:

| Copy | Phase |
|---|---|
| `InstanceTransition.Body`/`Header` — the request, often the very delta just protected | 2 |
| `InstanceTask.Request`/`Response`/`InvocationResult` | 2 |
| Outbox/Inbox `InstanceSubCompleted`/`InstanceSubFaulted.InstanceData`, job payloads | 2 |
| Redis human-task cache; the data-function cache never stores a row that carries tokens | 2 |
| History rows written before the field became protected (no backfill) | 2 |
| Anything the engine copies (a script writing the value elsewhere) | author's responsibility |

## Cost (measured 2026-09-30, E3)

Rows without protected values pay only a prefix scan (≈ 0.2–2.4 µs per row). A row with protected fields pays one
document rewrite per pass (open, hash, seal): ≈ 15–45 µs each at 2 KB, ≈ 130–160 µs at 20 KB; AES-GCM itself is ≈ 3 µs
per field. Every protected write also runs the secret get-or-create, one statement (`INSERT … ON CONFLICT DO NOTHING
RETURNING` unioned with a read of the existing row; p50 ≈ 0.19–0.21 ms, ≈ 30 % less than the earlier INSERT + SELECT pair);
warm reads issue no secret query, a cold read one batched preload (≈ 0.3 ms). On the lab workload that is ≈ 5–6 % of a
write transition, below client-level noise. Benchmarks: `test/BBT.Workflow.Benchmarks` (`FieldEncryptionBenchmarks`,
baseline `baselines/2026-09-30-field-encryption.md`).

## Publish-time rules (`FieldMaskingDefinition`)

The schema component's `attributes.type` is not checked; the keywords take effect wherever the schema is
referenced as `workflow.schema`. A declaration is rejected when it:

- is not reachable through nested `properties` (under `items`, `$defs`, combinators or conditionals);
- sits on a property that does not declare `type: "string"` (optionally with `"null"`);
- uses an unknown operator/type/key or the other operator's parameters;
- lists a `deny` grant or a malformed dynamic role (`roles` on `hash` is rejected outright);
- is combined with `x-filterOperators`, `x-sortable`, `x-indexed`, or `x-masking` with an active `x-encryption`;
- is a `hash` with `pattern`/`format`/`minLength`/`maxLength`/`enum`/`const`;
- declares `hash`/`encrypt` on a host with `SchemaEncryption:EncryptWrites=false` (`SchemaComponentValidator`).

## Cache and ETag

The data-function cache generation is `v3` (+ `-nomask`), in the key **and** the ETag material. A row that
carries `encrypt` tokens is never cached — an allow-listed caller's body holds plaintext. **Known gap**:
re-publishing a schema at the same version changes nothing the ETag is built from; bump the schema version when
you change these keywords.

## Configuration

```json
"SchemaMasking": { "Enabled": true },
"SchemaEncryption": { "EncryptWrites": true, "SecretCacheEntries": 100000, "SecretCacheSlidingMinutes": 30 }
```

`SchemaMasking__Enabled=false` turns off `x-masking` only. `SchemaEncryption__EncryptWrites=false` is the rollback
switch: new values are stored plain (existing tokens keep decrypting while their secret rows exist), and schemas
declaring `hash`/`encrypt` cannot be published. Decryption and the value served to non-allow-listed callers can
never be switched off. No switch touches `x-roles`.

## Engine

`IFieldMaskingEngine` is the Application port for `mask`/`replace`; `TasmanianDevilFieldMaskingEngine`
(Infrastructure) wraps the TasmanianDevil operator layer pinned to `[0.3.0]` — `MaskOperator` and
`ReplaceOperator` only. `hash` and `encrypt` use the BCL (`HMACSHA256`/`HMACSHA512`, `AesGcm`) in
`AesGcmFieldCipher`.
