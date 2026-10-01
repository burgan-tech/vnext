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

Every transition body is merged into instance data **deeply and unconditionally** — there
is no replace mode, no per-transition opt-out, no schema-level flag
(`CreateTransitionRecordStep` applies it to every body; the merge is
`JsonCanonicalizer.MergeAndCanonicalize`, legacy twin `JsonData.Merge` →
`ObjectMerger.MergeValues`). Two consequences every client team must design for
(finding AB-18, [vnext-client-sdk-core#58](https://github.com/burgan-tech/vnext-client-sdk-core/issues/58)):

- **Objects merge per key, recursively.** A field you did not send survives; a field you
  send overwrites. Sending an *untouched photograph* of a record therefore overwrites every
  field in it with the possibly-stale values you read — send deltas, not snapshots, on
  schema-less transitions.
- **Arrays are replaced wholesale, never merged element-wise**
  (`CollectionMergeStrategy` — this is also what allows *removing* items from a list). A
  stale `documents` array in any body silently deletes items a concurrent writer added.
  If two writers touch the same array, serialize them at the workflow level or model the
  collection as a keyed object.

A per-transition `bodyMode: merge | input-only` opt-out has been asked for and is an open
design question — `input-only` interacts with the full-merge invariant above (every version
carries the complete state), so it needs its own decision before any implementation.

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

