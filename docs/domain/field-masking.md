# Field Masking and Encryption (`x-masking`, `x-encryption`)

Data-schema property keywords that control how an instance-data field is stored and shown. Issue
[burgan-tech/vnext#135](https://github.com/burgan-tech/vnext/issues/135); council sessions
`2026-09-28-field-masking-x-masking` and `2026-09-29-field-encryption-encrypt`, with the committee amendment of
2026-09-30 (see `docs/agent-council/sessions/README.md`).

## Three controls, three keywords

| Control | Keyword | Where it acts |
|---|---|---|
| Hide a field from some callers | `x-roles` | read (instance GET, list, data function, sync response, Get* tasks) |
| Mask a visible field | `x-masking` | read (same surfaces); stored data never changes |
| Hash a field | `x-encryption` `type: "hash"` | **write** — the stored value is the digest |
| Encrypt a field | `x-encryption` `type: "encrypt"` | **write** (AES-256-GCM token in instance data) + read (decrypted for allow-listed callers; scripts open their own instance's value with `DecryptAsync`) |

The read order is fixed: **`x-roles` → `x-masking` → `x-encryption`**. A property `x-roles` hides is pruned,
and nothing else looks at it. A property carries at most one transform: `x-masking` next to an active
`x-encryption` is rejected at publish. The ordering is structural in the writer (`InstanceDataRoleFilter.Apply`):
a pruned path is skipped before its value is read.

## Where the read-side controls apply — and where they do not

| Surface | `x-roles` | `x-masking` | `encrypt` | `hash` |
|---|---|---|---|---|
| Instance GET, instance list, data function, sync start/transition response | pruned | allow-listed → clear, others masked | allow-listed → plaintext, others the stored token; legacy plaintext → full mask | stored digest; legacy plaintext → full mask |
| Trigger tasks `GetInstance`, `GetInstances`, `GetInstanceData` (same domain and cross-domain) | same, for the task's credential | same | same | same |
| Scripts: `context.Instance.Data`, and `context.Body` where the runtime fills it with instance data | not applied | not applied | **the stored token**; open with `context.Instance.DecryptAsync(path)` | digest |

**One read path.** Every guarded surface reads through `IInstanceDataReadService` (`Instances/`), the read-side
counterpart of `IInstanceDataWriteService`: it preloads the row's secrets and runs the single exposure pass
(`SchemaFieldFilterService` → `InstanceDataRoleFilter`). When nothing can be applied — no schema, a schema that cannot be
read, no rules — it serves the row **as stored**, so an `encrypt` value stays a token. A row carrying tokens is
opened here, on demand, only so that an allow-listed caller can be served the plaintext.
The list builds one page reader: schema metadata once per schema version, the page's secrets in one query, visibility
still decided per instance.

**Trigger-task credential.** A `GetInstance` / `GetInstances` / `GetInstanceData` task is evaluated as the header set it
presents to its target (`HttpTaskInvocation.BuildOutgoingHeaders`): the task's mapping headers without the reserved
trace/correlation headers, plus the request's **credential** — `sub`, `act_sub`, `position`, `client_id` and `role` —
for every one of them the mapping left absent or empty (a non-empty mapping value always wins). Nothing else of the caller
travels. The same rule applies to every outbound task type (see
[Correlation and tracing](../runtime/correlation-and-tracing.md)).
- `role` travels only as the caller **sent** it (its request header). Roles a provider resolved for the caller (morph-idm
  get-roles) are never carried; the target resolves them itself from the forwarded `sub` / `act_sub` / `client_id` /
  `position`.
- Cross-domain, the set is the remote binding's headers, sent to the target domain's endpoint; same domain, the read runs
  under `ICurrentUser.Change(...)` built from the same set — `ICurrentUser` (AsyncLocal, read before headers by role
  resolution) cannot carry anything else of the pipeline caller into it.
- So a task with no headers reads as its caller; set `role` (or the whole credential) in the input mapping to read as
  someone else, e.g. a service role.

Not covered (the row as stored): transition history, `context.Related`, custom functions, the human-task list
(`humanTask.title`/`description`), outbox events and internal endpoints (network isolation is the boundary). A value a
script copies into another field loses its protection; a task mapping that copies an `encrypt` token into another field
is refused by the write funnel (`EncryptedValueReservedException`).

### Scripts: the token, and `context.Instance.DecryptAsync(path)`

A script sees an `encrypt` field as its **token**, and opens it explicitly:

```csharp
var raw  = context.Instance.Data.identityNumber;                               // "ENCRYPTED:AES256:i1:…"
var tckn = await context.Instance.DecryptAsync("identityNumber", cancellationToken);   // plaintext
```

| Where the script reads | What it sees |
|---|---|
| `context.Instance.Data` (every script: mappings, conditions, view rules, extensions, output/timeout mapping, functions) | token |
| `context.Body` where the runtime puts instance data (extensions, view rules, output and timeout mapping, function info; transition task scripts after a parallel branch wrote data) | token |
| `context.Body` of a transition's own task scripts | the client's request body, as sent |
| `context.Body` of a SubFlow's output mapping in the parent | the child's data in clear (the parent cannot open the child's tokens) |
| `context.Instance.LatestData.Data` / `FindData(version).Data` | token — the row as stored |
| DynamicExpresso rules and function-cache `varyBy` | token; no await, so no `DecryptAsync` — write a C# condition script instead |

`DecryptAsync` takes a **path** of the instance's own latest data, never a value: it returns `null` for a plain field, an
unknown path, a value that cannot be opened, or a token string passed as if it were a path — so nothing that reached the
script from elsewhere (a request body, a task response, `context.Related`, another instance) can be decrypted.

**Cancellation.** The token is used for that call only and is never stored, so a cached script context cannot carry a
stale one. The row is opened once per script snapshot; its secret usually comes from the in-process cache (the
transition context load preloads it), otherwise it is loaded through EF in its own scope and unit of work (never the
caller's DbContext, which a parallel task branch may be using). A cancelled token throws
`OperationCanceledException`; the runtime does not catch it or treat it as retryable, and nothing is cached by a
cancelled load.

**Writing back.** The same token at the same path is a no-op; a plaintext value is encrypted again; a token written to
any other field is refused (`EncryptedValueReservedException`). Copying a decrypted value into another field removes its
protection — that is the script author's decision.

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
  encrypted values unrecoverable (every read serves the token, `DecryptAsync` answers `null`, and a write that would
  carry such a value forward is refused — see *Write rules*). **It takes effect per pod once that pod's L1 entry is gone** — sliding
  expiry (`SecretCacheSlidingMinutes`) or a restart; until then a pod that already held the secret keeps opening the
  values. Measured on the lab: same pod after the delete → plaintext; after a restart → token.
- **Creation** happens only in the write funnel, inside its transaction and under its per-instance row lock, in one
  statement (`INSERT … ON CONFLICT DO NOTHING RETURNING` unioned with a read of the existing row), so concurrent first
  writes produce exactly one row. Should that statement return nothing (a row committed by another transaction after its
  snapshot — the row lock rules it out on this path), a plain read follows. The read path never creates one.
- **Cache**: `InstanceSecretStore` keeps secrets in a private, size-bounded in-process `MemoryCache` — the L1 —
  and deliberately **never in Redis** (`SchemaEncryption:SecretCacheEntries`, `SecretCacheSlidingMinutes`).
  Secrets never change, so an entry cannot be stale. Entry points preload them in one query
  (`IInstanceSecretPreloader`: transition context load and `IInstanceDataReadService`); a value opened without a
  preload loads its secret through EF, in an isolated scope and unit of work on the shared connection pool (Debug log
  20471) — there is no second connection pool and no Npgsql code in the store. A token that fails to
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
  fully masked on every guarded surface.

### `encrypt` — AES-256-GCM at rest

```json
"email": { "type": "string", "x-encryption": { "type": "encrypt",
  "roles": [ { "role": "morph-idm.auditor", "grant": "allow" } ], "purpose": "PII" } }
```

| Where | What is there |
|---|---|
| `InstancesData."Data"` column | `ENCRYPTED:AES256:i1:<base64url(0x01 ‖ nonce ‖ ciphertext ‖ tag)>` |
| `InstanceData.Data` everywhere in the engine (the aggregate, scripts, runtime-filled `context.Body`, dynamic role grants, human-task text, transition history) | the stored token |
| Write funnel (merge, dedup, schema validation) | plaintext, opened inside the funnel and never kept |
| Scripts, on request | `await context.Instance.DecryptAsync(path, ct)` opens the instance's own value |
| Guarded surfaces (GET, list, data function, sync response, Get* tasks), allow-listed caller | plaintext |
| Guarded surfaces, every other caller | the stored token |
| Guarded surfaces, value written before the field became `encrypt` | full mask |

**Construction.** BCL `AesGcm` with the instance key, 12-byte random nonce, 16-byte tag; `i1` names the scheme
(instance key, version 1). The additional authenticated data is `vnext.idata.v1|i1|<instanceId>|<path>`, so a token
copied to another field or instance does not open. It is deliberately not bound to the row: an unchanged value
carries its token forward, and a client can echo a token back.

**Storage model.** `InstanceData.Data` is the `"Data"` column exactly as stored — tokens included — in memory, on a
script snapshot and after a reload. Nothing decrypts a row in place: there is no plaintext view on the entity and no
materialization interceptor, so no EF tracking state (`Add`, fixup, change detection, the detached `Set.Update(graph)`
of the retry/fault scopes) can write plaintext into the column. Content and `DataHash` are still ignored after save,
and `EfCoreInstanceRepository.UpdateAsync` attaches a detached aggregate's rows `Unchanged`. Decryption happens only
where it is asked for — `IInstanceDataProtector.UnprotectAsync`, called by the write funnel, the read guard and
`DecryptAsync` — and is driven by the token prefix, never by the current schema. The model change needed no migration:
the column, its name and its index are the ones Phase 1 created.

**Dynamic role grants and the human-task list see the token.** By decision: a field used as a role or a task title
should not be `encrypt`.

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
- A write that would carry an undecryptable value forward unchanged (secret row missing, token tampered) is refused
  with `EncryptionKeyUnavailableException` (`Instance:100040`): sealing the token string again would store it as the
  value. There is no earlier gate — loading the transition context no longer opens the row — so the refusal surfaces
  as the failure of the step whose write hit it. Reads never fail on such a value; they serve the token.
  `WorkflowLogs.EncryptedValueUndecryptable` (20468) names instance and path, never the value.

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

## Invariants (developer card)

The implementation rules the developer quick-reference card points at, kept here in full.

- Order `x-roles → x-masking → x-encryption`, decided in ONE pass (`SchemaFieldFilterService` →
  `InstanceDataRoleFilter.Apply`) with ONE evaluator. Never add a second masking stage or decorator: the
  data-function cache stores the filtered body and skips the filter on a hit.
- `x-masking.roles` and `x-encryption.roles` are **allow-only exemption lists** (an allow match sees the raw
  value, everyone else the transformed one); exemption is `IRoleGrantEvaluator.IsAnyRoleAllowed` over the list,
  and a role-less caller can never satisfy a role-bound grant (`IsUnprovableRoleBoundGrant` — a `$role.` value
  resolving to `""` matched the empty role once). `deny` is rejected at publish (`FieldMaskingDefinition`).
- **ONE read path**: instance GET, instance list, data function and sync start/transition response read through
  `IInstanceDataReadService` (the `IInstanceDataWriteService` counterpart); the Get* trigger tasks reach the same
  surfaces. It preloads secrets and runs the exposure pass; when nothing applies (no schema, unreadable schema, no rules)
  it serves the row as stored — `ISchemaFieldFilterService.ApplyAsync` returns `null` for "nothing applied" precisely so
  no caller can fall back to a plaintext. It opens a row (`UnprotectAsync`) only when the row carries tokens, so the
  filter can serve an allow-listed caller the plaintext. Do not call the filter from a surface directly.
- `x-encryption.type: "hash"` is applied on **WRITE**: the funnel stores `HASHED:SHA256:<hex>` (HMAC under the
  instance's own salt), the read path serves the digest; no `roles`, no validation keywords, one transform per field.
- Per-instance key + salt live in the flow schema's `InstanceSecrets` table, created ONLY by the write funnel
  under its row lock (`InstanceSecretStore.GetOrCreateAsync`), cached in a private in-process L1 and **never in
  Redis**. Entry points preload (`IInstanceSecretPreloader`); an open without one loads the secret through EF in an
  isolated scope + unit of work (`ExecuteInIsolatedUnitOfWorkAsync`) — never Npgsql directly, never the caller's context.
- **Get* trigger tasks read as the header set they present** — mapping headers + the request's credential
  (`sub`, `act_sub`, `position`, `client_id`, `role`) for each one the mapping left absent or EMPTY; a non-empty mapping
  value wins (`HttpTaskInvocation.AppendCallerCredential`, the one rule EVERY outbound task type uses — see
  `docs/runtime/correlation-and-tracing.md`). The values come from the REQUEST headers, so a morph-idm-resolved role is
  never carried. One set for both paths: cross-domain it is the remote binding's `Headers`
  (`SerializeReadCredential`); same-domain the executor reads under `ICurrentUser.Change(...)` from it (`ReadAs`), ALWAYS
  — `ChangeFromHeaders` is a no-op for an empty set and `ICurrentUser` (AsyncLocal) wins over headers. `SystemRead` is
  deleted.
- Data-function cache generation (`v3` + `-nomask`) is in the key AND the ETag; rows carrying tokens are never cached.
- **`InstanceData.Data` IS the column as stored** — tokens included — on the live aggregate, on a script snapshot and
  after a reload. There is no plaintext view on the entity and no materialization interceptor; nothing decrypts a row in
  place. Decryption happens only where it is asked for, through `IInstanceDataProtector.UnprotectAsync` (async only):
  the write funnel (opens the head to merge/validate, never keeps it), the read guard (allow-listed callers) and
  `context.Instance.DecryptAsync(path, ct)`. Do not reintroduce a plaintext member on `InstanceData` or a lazy open:
  the two-faced model (`StoredData` + view) was removed by decision, and with it any way for EF to write plaintext.
  Never write `InstancesData."Data"` outside `InstanceDataWriteService`. Decryption is prefix-driven, never
  schema-driven. `SchemaEncryption:EncryptWrites` is the rollback switch for both hash and encrypt.
- **`DecryptAsync` opens only the instance's OWN path**: `null` for a plain field, an unknown path, a value that cannot be
  opened, a token passed as a path, or a snapshot without a bound protector (`ScriptContextBuilder` binds it via
  `Instance.BindDecryption`; `CreateSnapshot`/`CopyDecryptionTo` carry it). Its token is per call (never captured;
  contexts are cached) and `OperationCanceledException` propagates. Dynamic role grants and the human-task list see the
  token by decision — a role or a task title should not be `encrypt`; DynamicExpresso sees tokens.
- **A SubFlow/SubProcess hands its parent PLAINTEXT**: `ISubItemEventDataResolver` opens the child's own tokens with the
  child's key before `Instance.Complete`/`Fault` raise the sub event (the parent cannot open them, least of all
  cross-domain). Every Complete/Fault call site of a sub item passes it.
- There is no context-load gate for an undecryptable value anymore (`Instance:100040` before admission was removed);
  only a WRITE that would carry an unopenable value forward refuses (`EncryptionKeyUnavailableException`). Scope is
  InstanceData only — transition bodies, task journals and outbox payloads still hold plaintext.
