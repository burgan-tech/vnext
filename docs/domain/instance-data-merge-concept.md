# Instance Data Merge Concept

## Purpose

Instance data is immutable, versioned workflow state. Each write creates a new
`InstanceData` record that contains the complete merged payload for that version, not only
the delta. This gives readers a stable latest snapshot while preserving history.

## Boundaries

The `Instance` aggregate owns data history. Pipeline steps and tasks may add data through
domain methods, but they should not manually rewrite old records. Query services expose
latest data, history, and ETag-aware conditional reads.

## Architecture Flow

1. Instance starts with an initial data payload.
2. A task or script produces a JSON payload.
3. Domain logic merges the new payload into the previous full payload.
4. A new semantic version is assigned with a major/minor/patch strategy.
5. The previous latest row is no longer latest.
6. The new row receives `IsLatest`, `VersionNo`, `ETag`, `DataHash`, and history ordering.

## Contracts

| Field | Contract |
| --- | --- |
| `Version` | Semantic version string incremented by version strategy. |
| `VersionNo` | Instance-global monotonic sequence assigned by database trigger. |
| `IsLatest` | Exactly one latest row per instance. |
| `ETag` | Conditional-read token for state and data functions. |
| `DataHash` | Normalized JSON hash for content change detection. |
| `Data` | Full merged JSON snapshot for that version. |

Versioning convention:

- Patch: task result or compatible value update.
- Minor: additive schema/data expansion.
- Major: breaking data shape change.

## Merge Semantics — the Sharp Edges (client-facing)

Every transition body is merged into instance data **deeply** — there is no replace mode for
the body as a whole and no input-only opt-out (`CreateTransitionRecordStep` applies the merge
to every body; the merge is `JsonCanonicalizer.MergeAndCanonicalize`, legacy twin
`JsonData.Merge` → `ObjectMerger.MergeValues`). Two consequences every client team must design
for (finding AB-18,
[vnext-client-sdk-core#58](https://github.com/burgan-tech/vnext-client-sdk-core/issues/58)):

- **Objects merge per key, recursively.** A field you did not send survives; a field you
  send overwrites. Sending an *untouched photograph* of a record therefore overwrites every
  field in it with the possibly-stale values you read — send deltas, not snapshots, on
  schema-less transitions. **This is not configurable.**
- **Arrays are replaced wholesale by default** (`CollectionMergeStrategy` — which is also what
  allows *removing* items from a list). A stale `documents` array in a body silently deletes
  items a concurrent writer added. Since 0.0.98 a transition can opt out of that with
  `arrayMerge` — see below.

### `arrayMerge` — the per-transition array setting

```jsonc
{ "key": "attach-document", "target": "$self", "arrayMerge": "M" }
```

| Value | Behaviour | Gives you | Costs you |
| --- | --- | --- | --- |
| absent | Same as `R`. **Every definition written before 0.0.98 is this.** | — | — |
| `R` | Replace: the incoming array wins outright. | Removal by omission — dropping an item from the array deletes it. | A stale copy erases a concurrent writer's additions, silently. |
| `M` | Merge: stored items are kept and incoming ones folded in. | Concurrent additions are safe; re-sending the same payload is idempotent. | **Removal by omission stops working** — under `M` nothing is ever deleted by leaving it out. |

The two values are deliberately opposite trade-offs, which is why this is an authoring choice
and not a runtime fix. Pick `M` for append-shaped collections (`documents`, `attachments`,
`notes`) written by more than one actor; keep `R` wherever the array is a *set the caller
owns* and removing an item is a real operation.

**How `M` matches items.** Target order is preserved; an incoming item that matches one already
stored replaces it **in place**, and anything unmatched is appended in arrival order. Matching is
by the `id` property when **both** items are objects that carry one (case-insensitive property
name, compared as text — so `"7"` and `7` are different identities, deliberately: the authored
data decides and the runtime does not coerce); otherwise by exact value, which is the right rule
for arrays of scalars. Consequence worth knowing: **an array of objects without an `id` cannot
express an update** — an edited item does not match its predecessor and arrives as a second
entry. `ArrayUnion` is the single definition of this rule, shared by the live and legacy
pipelines so their byte-parity contract holds.

**It is body-wide and depth-wide, not per field.** The setting travels down the whole merge, so a
transition declaring `M` gets `M` for **every array anywhere in its body**, at any nesting depth.
There is no way to say "merge `documents` but replace `tags`" on one transition — if you need both
rules, split the writes across two transitions.

**Dedup is incoming-vs-stored only.** `M` will not *add* an item that already matches one in the
stored array, but it does not clean up duplicates the stored array already contains — those were
written by something else and are left exactly as they are.

**Scope: the transition body only.** `CreateTransitionRecordStep` reads the setting off the
transition being executed. Task outputs (`TaskExecutionEngine`), subflow output mapping
(`SubflowOutputMappingService`) and the instance-creation payload (`InstanceCommandAppService`)
keep the historical wholesale replace — nothing that was not explicitly opted into changes
behaviour.

**Kill-switch interaction.** `InstanceDataWrite:LegacyAppendPipeline` (default off) routes ordinary
appends through the legacy multi-pass merger, which cannot express `M` — its array step is a
singleton strategy behind a static recursive call with nowhere to carry the choice. A transition
that explicitly opted into `M` therefore always takes the canonicalizer path **even with the
kill-switch on**: silently degrading `M` back to `R` would reinstate the exact data loss the setting
exists to prevent. Every ordinary (`R`) append still honours the switch.

**Where it can be authored**: state transitions, shared transitions, `startTransition`, and the
three well-known transitions (`cancel`, `exit`, `updateData`). `updateData` is the common case.
It is resolved through `Workflow.ResolveTransition`, so a workflow using a custom key for a
well-known transition is covered identically.

**Validation**: `ArrayMergeStrategy.FromCode` accepts only `R` and `M` and throws on anything
else, so a mistyped value **fails the component publish** with `Unknown array merge strategy: X`
rather than running with a silent default — the same contract as `executionType` (S/A) and
`executionLog` (E/D). The JSON-schema enum (`vnext-schema`, `definitions.arrayMerge`) rejects it
one step earlier for authors using the CLI or Forge.

**Known gap**: the workflow root object does not set `additionalProperties: false`, so an
`arrayMerge` written at *flow* level passes schema validation and then does nothing — it is a
transition-level setting only. Pinned as a known gap in vnext-schema's workflow-document tests;
closing it means closing the root object, which is a separate, breaking validation change.

An `input-only` body mode (persist the body without merging it at all) was discussed on the same
finding and is **not** implemented: it conflicts with the full-merge invariant above, where every
version row carries the complete state, so it needs its own decision before any implementation.

### Where One Payload Gets Persisted (storage multiplication)

A transition body — a base64 file included — is stored, verbatim or merged, in each of
(finding AB-21): the transition record (`InstanceTransitions.Body`), the task journal per
task (`InstanceTasks.Request`, potentially `Response`/`InvocationResult`), the merged
snapshot (`InstancesData.Data`, once per version row), and — for an async accept — the
offloaded-body table (`InstanceJobRequestData`, only when the body exceeds the inline cap;
since the AB-17 fix the Dapr job payload and outbox event carry a reference instead of a fifth
and sixth in-flight copy). All of these
are `jsonb`, and base64 TOAST-compresses poorly: a 1 MiB file costs several MiB of storage
per transition that carries it. Prefer uploading bytes to a document store and passing a
reference through the workflow; a platform-level document-offload story (store once,
reference everywhere) is an open ask on the same finding.

## Failure Modes

- Invalid JSON schema input fails before unsafe data is persisted.
- Duplicate version sequence is guarded by database uniqueness.
- Out-of-order readers should use ETag or `VersionNo` rather than timestamps alone.
- Scripts that mutate a copied `ScriptContext` must apply changes back to the live
  `TransitionExecutionContext`.

## Observability

Data changes affect state/data function ETags. Data sink and monitoring components can
observe instance and transition persistence without changing the domain write model.

## Change Safety

- Do not mutate historical rows to repair current state.
- Do not expose internal `CurrentState` when external clients need `EffectiveState`.
- When adding query filters, keep JSON-path filtering consistent with schema validation rules.
- Treat data shape changes as contract changes for SDK and client teams.

## References

- `src/BBT.Workflow.Domain/Instances/Instance.cs`
- `src/BBT.Workflow.Domain/Instances/InstanceData.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs`
- `src/BBT.Workflow.Infrastructure/Data/InstancesModelCreatingExtensions.cs`
- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Functions/Handlers/DataFunctionHandler.cs`

