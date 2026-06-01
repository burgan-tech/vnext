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

