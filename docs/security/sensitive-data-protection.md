# Sensitive Data Protection (`x-sensitive`)

Instance data is authored freely by domain teams, so the runtime cannot know which fields carry
personal, financial, or credential data — the schema author can. `x-sensitive` is the annotation
that says so, on the schema property that already describes the field, and every protection
surface derives its behaviour from that single declaration.

This page covers the vocabulary, its definition-time validation, log redaction, and **encryption at
rest**. Encryption is implemented but ships **disabled by default** — see [Status](#status).

## The annotation

```json
{
  "properties": {
    "email": {
      "type": "string",
      "x-sensitive": {
        "enabled": true,
        "purpose": "PII",
        "redactInLogs": true,
        "maskingPattern": "{first}***@***.***"
      }
    },
    "ssn": {
      "type": "string",
      "x-sensitive": {
        "enabled": true,
        "purpose": "PII-Identification",
        "encryptAtRest": true,
        "redactInLogs": true,
        "maskingPattern": "***-**-{last4}",
        "retentionDays": 2555
      }
    }
  }
}
```

| Field | Meaning |
| --- | --- |
| `enabled` | Master switch. `false` makes the whole annotation inert — the documented way to stage one. |
| `purpose` | Classification (`PII`, `PII-Identification`, `Financial`, ...). **Required** when enabled: it is the only part that explains the marker to an auditor. |
| `encryptAtRest` | Encrypt before the value reaches the `Data` jsonb column. Requires `Workflow:Security:Encryption:Enabled`. |
| `redactInLogs` | Replace the value with `maskingPattern` on its way to a log sink or diagnostic message. |
| `maskingPattern` | How much to reveal. Absent ⇒ `***`. |
| `retentionDays` | **Documentation only.** Parsed and surfaced, never enforced — see [Status](#status). |

### Masking patterns

Tokens are `{first}` / `{last}` (one character) and `{firstN}` / `{lastN}` (N between 1 and 99,
e.g. `{last4}`). Everything else is literal.

`SensitiveValueMasker` is total and fails **closed**: an unknown token, an empty value, or a
pattern that renders to nothing all degrade to `***`. A masker that failed open would emit the raw
value spliced into the pattern — exactly what it exists to prevent. Pattern typos are therefore a
publish-time error, not a silent runtime degradation.

Source: [SensitiveValueMasker.cs](../../src/BBT.Workflow.Domain/Security/SensitiveValueMasker.cs)

## Where the annotation is read

`x-sensitive` lives on the **workflow's master schema** (`workflow.schema`) and is resolved
through the shared
[SchemaAnnotationWalker](../../src/BBT.Workflow.Domain/Definitions/Schemas/SchemaAnnotationWalker.cs),
which is now the single traversal behind `x-roles`, `x-filterOperators`/`x-sortable`, and
`x-sensitive`.

Paths are dotted, with `[]` marking an array item: `email`, `customer.address.city`,
`cards[].number`.

The walk follows **`properties` and `items` only**. It does not resolve `$ref` and does not descend
into `$defs`, `definitions`, `oneOf`, `anyOf`, `allOf`, `if`/`then`/`else`, `patternProperties`, or
`additionalProperties`. An `x-sensitive` under any of those is **rejected at publish time** rather
than accepted and silently ignored — an annotation that reads as protection and delivers none is
worse than no annotation.

> **Behaviour change:** consolidating onto the shared walker also gave `x-roles` array-item
> traversal, which it never had. An `x-roles` inside an `items` schema was previously inert and now
> takes effect. Review existing master schemas with annotated array items before upgrading.

## Definition-time validation

[SchemaComponentValidator](../../src/BBT.Workflow.Application/Definitions/Validators/SchemaComponentValidator.cs)
rejects a schema component whose `x-sensitive` annotations are unusable. Every problem is a hard
error; there is no warning tier, because each case is either a contradiction or a silent
loss-of-protection, and `x-sensitive` is new so nothing existing can break.

| Rejected | Why |
| --- | --- |
| `encryptAtRest` **+** `x-filterOperators` / `x-sortable` on the same path | Instance filtering is raw SQL over the `Data` jsonb. A predicate against a ciphertext leaf matches **nothing and reports no error**. Publish time is the only place this is visible. |
| `encryptAtRest` on a non-`string` type | Ciphertext is stored as a JSON string, which would violate the field's own declared type. |
| `enabled: true` with no `purpose` | An unclassified marker is not auditable. |
| `enabled: false` **+** `encryptAtRest`/`redactInLogs` | The "set the flag, forgot `enabled`" bug: the author believes the field is protected and nothing protects it. |
| Unknown `maskingPattern` token | Would silently degrade every mask to `***`. |
| `retentionDays` ≤ 0 | Meaningless. |
| `x-sensitive` under an unreachable keyword | Would never be applied. |

`SensitiveSchemaParser` deliberately has two tempers: `Validate` is strict (publishing), `Parse` is
lenient (runtime) — a malformed annotation must never fail a live transition.

## Log redaction

Redaction is **value-based, not path-based**, and that is the whole point. The leak vectors hand
the runtime a bare value with no path attached: a script writing
`LogInformation("{e}", context.Instance.Data.email)`, or a JSON Schema validation message quoting
the offending value back. Knowing that `email` is sensitive does not help there — knowing *what
the email is* does.

So [SensitiveDataScrubber](../../src/BBT.Workflow.Domain/Security/SensitiveDataScrubber.cs) is built
per instance from the master schema (which fields) plus the instance data (what values), and then
matches on the values themselves.

### Script logging

Every `.csx` mapping's `LogTrace`/`LogInformation`/… call goes through
[ScrubbingLogger](../../src/BBT.Workflow.Application/Security/ScrubbingLogger.cs), a decorator on
the logger handed to `ScriptServices`. It scrubs **both halves** of a log record:

- the rendered message, and
- the structured values — a sink reads those directly and would otherwise persist the raw value
  under a property name even though the rendered line looked clean.

`{OriginalFormat}` is scrubbed too, for the script that interpolated a value into its own template
string instead of passing it as an argument.

Decorating the logger rather than `ScriptBase` means the protection cannot be bypassed by a new
logging helper, and the scripting module keeps its zero-dependency footprint.

The scrubber is published by
[ScriptContextBuilder](../../src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs)
into the scoped
[ISensitiveDataScrubberAccessor](../../src/BBT.Workflow.Domain/Security/ISensitiveDataScrubberAccessor.cs),
because that is the one point where both the master schema and the instance data are in hand.
Cost on the common path is one flag check: a workflow with no master schema does nothing, and an
unannotated schema is memoized as empty by
[SensitiveSchemaCache](../../src/BBT.Workflow.Domain/Definitions/Schemas/SensitiveSchemaCache.cs).

### Schema validation messages

Validation messages routinely quote the offending value (`'a@b.c' does not match format 'email'`),
and that exception becomes both a log record and an API problem-details body.
`InstanceDataWriteService.ValidateAndResolveSensitiveFieldsAsync` scrubs the message and each validation
error against a scrubber built from **the content that failed** — the accessor's scrubber was built
from the *previous* latest data and cannot know an incoming value. Member names are left alone:
they are property paths, not values.

### HTTP body logging

Aether's `HttpBodyLoggingMiddleware` is off by default (`Telemetry:Logging:Body.EnableRequestBody`
/ `EnableResponseBody` are `false`) but is one config flip from on. All six hosts now ship a
populated `AdditionalSensitiveJsonFields` / `AdditionalSensitiveHeaderNames` baseline instead of
empty lists.

This list is **static per host and not schema-driven** — it cannot know a domain's field names. If
you enable body logging, extend the host's list to cover your domain's sensitive fields; the
`x-sensitive` annotation does not reach this middleware.

## Known gaps

These are deliberate and documented rather than silently unprotected:

| Gap | Detail |
| --- | --- |
| **Exception messages** | An `Exception` attached to a log record is forwarded as-is. Rewriting an arbitrary exception type is not safe. Call sites that build a diagnostic string from an exception should scrub it themselves. |
| **Short values** | Values under `SensitiveDataScrubber.MinScrubbableLength` (3 characters) are not scrubbed. A one- or two-character value occurs incidentally all over a log line, and replacing every occurrence would shred the message while protecting almost nothing. Short sensitive values need encryption and `x-roles`, not log scrubbing. |
| **ClickHouse egress** | `ClickHouseInstanceTransitionDataSink` ships every transition `Body` **and `Header`** verbatim, and `ClickHouseInstanceTaskDataSink` ships every task `Request`/`Response`. This is a genuine PII egress path outside `InstanceData` and is **not** covered by Phase 1. The transition body is validated against the *transition* schema, which can carry `x-sensitive`, so the same metadata can drive it later. |
| **Scopes without a script context** | Work running in a DI scope that never built a script context sees `SensitiveDataScrubber.None` and is unscrubbed. Scrubbing is defence in depth, not the primary control. |
| **Unannotated fields** | A value the schema does not mark is logged as-is. Prefer logging identifiers (instance id, state, transition key) over payload values regardless of annotation. |

`x-sensitive` is **not** an access-control mechanism. Field-level access control is `x-roles` —
see [Role Grant Authorization](../domain/role-grant-authorization.md). The two are independent:
`redactInLogs` does not hide a field from an API response, and `x-roles` does not stop a value
reaching a log.

## Status

| Capability | State |
| --- | --- |
| `x-sensitive` vocabulary + shared walker | **Implemented** |
| Definition-time validation | **Implemented** |
| Masking patterns | **Implemented** |
| Log redaction (scripts, schema validation errors) | **Implemented** |
| HTTP body redaction baseline | **Implemented** (static, not schema-driven) |
| `encryptAtRest` | **Implemented, disabled by default** |
| Key rotation | **Supported** — `keyId` in the marker; retired keys stay loadable |
| Backfill of pre-existing rows | **Implemented** — `POST {domain}/security/instance-data/re-encrypt`, dry-run by default |
| `retentionDays` | **Reported, never enforced.** The maintenance pass counts expired values; deleting them is a product decision and its own work item. |

## Encryption at rest

### Ciphertext format

```
enc:v1:<keyId>:<base64url(nonce ‖ ciphertext ‖ tag)>
```

AES-256-GCM, fresh 96-bit nonce per value, and **the field path bound as additional authenticated
data** — a ciphertext relocated to another field fails authentication instead of decrypting into
the wrong place. Always a JSON string, so the jsonb document stays structurally valid.

`keyId` is what makes rotation cheap: writing under a new key leaves old rows readable, so a roll
is a config change plus a background sweep, never a stop-the-world migration. `v1` versions the
algorithm.

### Why the two directions are asymmetric

**Encryption needs the schema** (which paths are marked), so it happens only in the instance-data
write funnel, which already loads the master schema to validate against it.
**Decryption needs nothing but the ciphertext**, because the marker names its own algorithm and key
id. That asymmetry is the whole design: it lets decryption sit behind one property getter reached
from ~20 call sites, none of which knows a schema exists.

### The write order is load-bearing

`InstanceDataWriteService` must do these in exactly this order:

1. read the head row under the `FOR UPDATE` lock
2. **decrypt the head**
3. merge head ∪ delta — on plaintext
4. `DataHash` over **plaintext**, compared to the stored hash → dedup
5. validate against the master schema — on plaintext
6. **encrypt** the `encryptAtRest` leaves
7. persist ciphertext with the plaintext-derived hash

Steps 2 and 4 are the ones that look redundant and are not. GCM uses a fresh nonce per value, so
identical content encrypts to different bytes: hashing ciphertext would make **every** append look
like a change, and merging ciphertext would persist the marker as if it were the value.
`EncryptedAppendOrderingTests` pins this, including a negative control that asserts dedup *breaks*
when the head decrypt is skipped — so the guard cannot decay into a tautology.

Keeping `DataHash` over plaintext has a deliberate consequence: it is a **whole-document** equality
oracle for someone with database access. Not a per-field one, and the alternative (keying the hash)
would invalidate dedup against every existing row. Documented rather than silently changed.

### The read seam

`InstanceData` splits in two:

- `StoredData` — the EF-mapped payload, ciphertext at rest. Keeps the original `Data` column name,
  so **encryption needs no migration**: the marker lives in-band inside the existing jsonb.
- `Data` — unmapped, lazily decrypted, memoised. Every consumer already used this property, so all
  ~20 read sites were unchanged.

The seam is here rather than at the repository boundary because the row is handed straight back to
the aggregate after a write (`Instance.AcceptPersistedData`), and `TransitionExecutionContext` syncs
from it — so the remaining pipeline steps and the transition response read the same object. A
repository-level decrypt would miss exactly that path. Per-read-method decryption was rejected for
the same reason plus the ~20-site audit surface, and an EF value converter cannot work at all for
the encrypt direction because it has no access to the schema.

Decryption goes through `SensitiveDataCipherAccessor`, the one piece of ambient state in the
design. It is confined to the decrypt direction on purpose: no schema, no request scope, no tenant —
just process-wide key material behind a getter that cannot await.

### Keys

`Workflow:Security:Encryption`:

| Setting | Meaning |
| --- | --- |
| `Enabled` | Master switch. Default `false`. |
| `ActiveKeyId` | Key new ciphertext is written under. |
| `KeySource` | `Configuration` (dev/tests) or `DaprSecretStore`. |
| `Keys` | Inline key id → base64 256-bit key. **Never production material.** |
| `SecretStoreName` / `SecretName` / `SecretKeyPrefix` | Dapr secret store coordinates. |

Keys load **once at startup** (`SensitiveDataCipherHostedService`) and lookups afterwards are
synchronous in-memory reads. That is not an optimisation — decryption happens behind a property
getter, and sync-over-async there would deadlock under load. The Dapr secret store is configured
with `vaultValueType: map`, so one fetch returns every key id including retired ones.

Two deliberate fail-loud behaviours:

- Encryption enabled but the active key is missing → **the host refuses to start**. Starting anyway
  would write plaintext into a column the operator believes is encrypted.
- Encrypted data read in a host with no configured cipher → `SensitiveDataEncryptionException`.
  Passing the marker through would hand `enc:v1:...` to a caller as if it were the value.

Decryption is *not* gated on `Enabled`: turning encryption off never strands rows written while it
was on.

### Encrypted fields cannot be queried

Filtering is raw SQL over the `Data` jsonb, so a predicate against ciphertext matches nothing.
Publish-time validation refuses to combine `encryptAtRest` with `x-filterOperators`/`x-sortable`,
and the query path now explains the rejection by name rather than reporting a bare "not filterable".

> **Configuration coupling:** that query-path explanation only fires when
> `Workflow:InstanceFiltering:EnforceMasterSchemaFiltering` is `true` (the orchestration host ships
> it on). With it off there is no schema context at query time, so a filter on an encrypted path
> silently returns nothing. **Keep it on wherever encryption is on.**

### Enabling it

1. Generate a 256-bit key: `openssl rand -base64 32`
2. Put it in the key source — Vault secret `dataKey.v1`, or `Keys: { "v1": "<base64>" }` for local dev
3. Set `Enabled: true` and `ActiveKeyId: "v1"`
4. Confirm `EnforceMasterSchemaFiltering: true`
5. Mark fields `encryptAtRest: true` (must be `type: string`, must not be filterable/sortable)

Only writes **after** step 3 are encrypted. Bring existing rows onto the key with the maintenance
pass below.

### Backfill, rotation and retention

**Backfill and rotation are the same operation** — "bring this row onto the active key". Backfill
starts from plaintext, rotation from an older key, and the code path is identical. Rotation alone is
schema-free (the marker says what is encrypted); only backfill needs the master schema, to learn
what *ought* to be.

```
POST /api/v1/{domain}/security/instance-data/re-encrypt?dryRun=true
POST /api/v1/{domain}/security/instance-data/re-encrypt?dryRun=false
```

`dryRun` defaults to **true** because the pass rewrites live rows. Optional `batchSize`,
`maxInstances`, `instanceKey`.

The pass rewrites the payload column and nothing else. Re-encryption does not change plaintext, so
`DataHash`, `ETag`, `Version`, `VersionNo` and `IsLatest` all stay valid: **no version bump, no ETag
churn, no cache invalidation, no long-polling client disturbed.** That is also what makes it
idempotent and resumable — an already-current row is recognised and skipped.

The currency check is structural, not a string comparison: a fresh nonce makes every re-encryption
differ byte-for-byte, so comparing payloads would rewrite every row on every pass, forever.

Two safety properties worth knowing:

- **A write pass with encryption disabled is refused.** With encryption off, `Encrypt` is a
  pass-through, so the pass would rewrite every row to plaintext — a silent mass-decryption of
  exactly the data this feature protects. Dry runs still work, for inspection.
- **Retention is reported, not enforced.** Expired values are counted in `expiredRetentionValues`
  and nothing is deleted. Whether expiry should drop the history row or blank the field (and accept
  a broken content hash) is a product decision with irreversible consequences.

**Rotation: keep the old key.** `keyId` in the marker is what makes a roll cheap, but removing a key
that has written data makes that data permanently unreadable.

Full case-by-case plan: [examples/TEST-PLAN.md](examples/TEST-PLAN.md).

## Source map

| Concern | File |
| --- | --- |
| Shared schema traversal | `Domain/Definitions/Schemas/SchemaAnnotationWalker.cs` |
| Annotation model | `Domain/Definitions/Schemas/SensitiveFieldMetadata.cs` |
| Parse + validate | `Domain/Definitions/Schemas/SensitiveSchemaParser.cs` |
| Per-schema memoization | `Domain/Definitions/Schemas/SensitiveSchemaCache.cs` |
| Masking | `Domain/Security/SensitiveValueMasker.cs` |
| Value scrubbing | `Domain/Security/SensitiveDataScrubber.cs` |
| Ambient scrubber | `Domain/Security/ISensitiveDataScrubberAccessor.cs` |
| Script log decorator | `Application/Security/ScrubbingLogger.cs` |
| Publish-time rejection | `Application/Definitions/Validators/SchemaComponentValidator.cs` |
| Validation-message scrubbing | `Infrastructure/Data/InstanceDataWriteService.cs` |
| Cipher + marker format | `Domain/Security/SensitiveDataCipher.cs` |
| Ambient decrypt cipher | `Domain/Security/SensitiveDataCipherAccessor.cs` |
| Encryption options | `Domain/Security/DataEncryptionOptions.cs` |
| Key contract + config provider | `Domain/Security/IDataEncryptionKeyProvider.cs`, `ConfigurationDataEncryptionKeyProvider.cs` |
| Vault-backed keys | `Infrastructure/Security/DaprDataEncryptionKeyProvider.cs` |
| Startup key load | `Infrastructure/HostedServices/SensitiveDataCipherHostedService.cs` |
| Stored/plaintext split | `Domain/Instances/InstanceData.cs` |
| Backfill / rotation pass | `Domain/Security/IInstanceDataEncryptionMaintenance.cs`, `Infrastructure/Security/InstanceDataEncryptionMaintenanceService.cs` |
| Operator endpoint | `orchestration/.../Controllers/Security/SecurityMaintenanceController.cs` |

## Cross-repo

`@burgan-tech/vnext-schema` must allow `x-sensitive` on schema properties, spelled **exactly**
`x-sensitive`, or an authored annotation fails `npm run validate` while working at runtime.

> There is precedent for getting this wrong: the field-level role vocabulary is documented as bare
> `roles` in the authoring guides while `SchemaRolesParser` reads `x-roles`. Do not repeat it.
