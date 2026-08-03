# vNext Workflow Developer — Domain Knowledge (Always Apply)

This rule complements the workflow concepts already captured in the root `CLAUDE.md`. Use it as a quick-reference card when implementing or reviewing pipeline / transition / subflow code.

## Transition Pipeline Order

| Order | Step | Responsibility |
|-------|------|----------------|
| 5 | HandleCancelPreflightStep | Detect cancel/exit; short-circuit if instance already completed |
| 9 | HandleUpdateDataPreflightStep | Parent update-data / shared-transition preflight for subflows |
| 10 | ForwardToActiveSubflowStep | Queue post-commit forward to active subflow; skip epilogue |
| 19 | SetBusyStep | Set instance status to Busy and persist |
| 20 | CreateTransitionRecordStep | Create transition record; duplicate key guard |
| 25 | ResourceLockStep | Acquire/release/extend resource locks via script |
| 30 | RunOnExecuteTasksStep | Run transition OnExecute tasks |
| 38 | ApplyTimeoutStateStep | Apply timeout target into context before exit |
| 39 | CancelScheduledJobsStep | Cancel scheduled jobs for current state |
| 40 | RunOnExitTasksStep | Run leaving-state OnExit tasks |
| 50 | ChangeStateStep | Persist state change |
| 60 | RunOnEntryTasksStep | Run target-state OnEntry tasks |
| 70 | HandleSubFlowStep | Start subflow correlation; enqueue StartSubflowJob |
| 79 | ClearBusyOnResumeStep | Clear busy on subflow resume path |
| 80 | ScheduleTransitionsStep | Schedule future transitions |
| 90 | RunAutomaticTransitionsStep | Evaluate auto-transition conditions; set NextTransition |
| 100 | HandleFinishStep | Complete/cancel instance on finish states |
| 110 | FinalizeTransitionStep | Complete transition record; dispose script cache |
| 112 | ResolveAvailableStep | Resolve deferred Active status |

### StepOutcome Values

- `Continue()` — advance to next step
- `Stop()` — break inner step loop (`StopPipeline = true`)
- `SkipTo(order)` — jump to a specific step (calls `Directives.RequestResumeFrom` + replan)
- `SkipToFinalize()` — shorthand for `SkipTo(LifecycleOrder.Finalize)`
- `With(Action<PipelineDirectives>)` — mutate directives before flow decision

Flow: apply `MutateDirectives` → Stop → break; SkipTo → replan; else continue.

### PipelineExecutionProfile

| Profile | Trigger | Key Exclusions |
|---------|---------|----------------|
| Manual | Manual (0) | None |
| AutoChain | Automatic (1) | Preflight, CheckParentUpdateData, ForwardSubflow, SetBusy, ApplyTimeoutState (ResourceLock runs — auto-chained transitions can acquire locks) |
| Scheduled | Scheduled (2) | Preflight, ForwardSubflow, SetBusy, ResourceLock |
| Event | Event (3) | Preflight, ForwardSubflow, SetBusy, ResourceLock |
| ErrorBoundary | Error boundary | Preflight, UpdateDataCheck, ForwardSubflow, ResourceLock, Auto, Schedule; `AllowAutoChain=false`, `AllowSubFlow=false` |

Resolution: `IPipelineProfileResolver.Resolve(context)` — if `IsErrorBoundaryTransition` → ErrorBoundary; else by `TriggerType`.

## Instance Repository Include Strategy

- Pipeline steps do NOT call EF `Include` directly. Includes applied at load time.
- `EfCoreInstanceRepository.WithDetailsAsync()` loads: `Include(DataList)` + `Include(ChildCorrelations.Where(!IsCompleted))` (split queries).
- `GetActiveAsync` → `GetResultAsync` → `FindByIdentifierAsync` → `WithDetailsAsync()`.
- History paths: `AsNoTracking` + explicit filtered includes.
- **Rule**: do not add unnecessary includes; reuse data from `TransitionExecutionContext`.

## Long-Polling / State Function

- Function type: `FunctionTypeConst.Longpooling` — conditional GET with ETag.
- Client cycle: `GET /functions/state` → `200` (changed) | `304` (not modified → wait → retry).
- ETag source: `LatestData?.ETag` for entity, `IRepresentationEtagService.Generate(output)` for representation.
- **Role filtering**: `ITransitionAuthorizationManager` filters available transitions per role. Supports `$InstanceStarter`, `$PreviousUser` pseudo-roles.
- No server-side hold — 304 drives client-side polling.
- **Response-shape version**: `StateFunctionCache.ResponseShapeVersion` is folded into both the ETag material and the cache key. Bump it in the same commit as any change to what the state body carries — otherwise a client polling a parked instance keeps getting 304 and never sees the new shape.

## Well-Known Transitions (`cancel` / `updateData` / `exit`)

- All three are workflow-level `Transition` objects (`Workflow.Cancel/UpdateData/Exit`) — full surface
  including `roles`, `view`, `schema`, `annotations`.
- **Listed in `availableTransitions`** from every state (subject to `triggerType` Manual/Event and
  `availableIn`), and merged from the **parent** into a subflow's list — that merge is `updateData`'s
  primary surface, since `HandleUpdateDataPreflightStep` only acts while the current state is `SubFlow`.
- The **configured key** is listed, never the alias (`cancel` / `update-parent-data` / `exit`): role
  filtering resolves via `FindTransitionInContext`, which matches these three on the configured key.
  Aliases stay accepted on the request side (`ResolveWellKnownKey`).
- `kind` discriminator mirrors the JSON field names: `cancel` | `updateData` | `exit` — note
  `updateData`, *not* the `update-parent-data` alias.
- `roles` are enforced at discovery (state function filtering, `/functions/authorize`,
  authorization-matrix) — **not** at `POST .../transitions/{key}`, which is true for every transition
  type, not just these.
- `WorkflowValidator` runs all three through `ValidateSingleTransition` (role-grant syntax +
  trigger-type rules); `updateData.target` must be `$self`.
- Schema (`vnext-schema` `cancelTransition`/`exitTransition`/`updateDataTransition`) accepts both
  `roles` and `availableIn` — `availableIn` was added in 0.0.79 to match runtime behavior that had
  always been there but was unauthorable under `additionalProperties: false`.
- Full guide: `docs/domain/well-known-transitions.md`.

## Role Grant Validation

- Three role forms: **static** (`backoffice.operator`), **predefined** (`$InstanceStarter`,
  `$PreviousUser`, `$InstanceBehalfOfStarter`, `$PreviousBehalfOfUser`), **dynamic**
  (`$user.` / `$userBehalfOf.` / `$role.` + `$.context.<path>`). Only dynamic is validated; the
  other two are free-form.
- A qualifier prefix ⇒ dynamic *intent*. The remainder must be the literal `$.context.`
  (**Ordinal — case-sensitive**) plus a non-empty nav path. `$user.customer`,
  `$user.$.Context.x` and `$role.$.context.` are all errors.
- Why strict: `DynamicRoleGrant.TryParse` returns null on any deviation, and runtime `IsMatch` then
  falls through to the **static** comparison — the grant becomes silently inert (an ALLOW that never
  grants, a DENY that never denies). Definition time is the only place it is visible.
- Never re-implement the parse rules in a validator. Use `DynamicRoleGrant.Classify`, which shares
  `TryParse`'s constants and comparisons; the `Classify == WellFormed ⟺ TryParse != null` invariant
  is pinned by `DynamicRoleGrantTests`.

## Sync vs Async

- `sync=true`: blocks until pipeline completes; full instance returned.
- `sync=false` (default): immediate `{ id, status }`; client polls via State function.

## Status / State / Type Semantics

**Instance Status**: `Busy (B)`, `Active (A)`, `Passive (P)`, `Completed (C)`, `Faulted (F)`.
**State Types**: `Initial=1`, `Intermediate=2`, `Finish=3`, `SubFlow=4`, `Wizard=5`.
**State Sub Types**: `None=0`, `Success=1`, `Error=2`, `Terminated=3`, `Suspended=4`, `Busy=5`, `Human=6`, `Cancelled=7`, `Timeout=8`.
**Trigger Types**: `Manual=0`, `Automatic=1`, `Scheduled=2`, `Event=3`.

## Error Boundary

- Levels: Task → State → Global (resolved by `CompiledBoundaryChain`).
- Rules sorted: `EffectivePriority` ASC → specificity DESC → definition order.
- Actions: `Abort`, `Retry`, `Rollback`, `Ignore`, `Notify`, `Log`.
- `BoundaryOutcomeHandler` mapping:
  - `Log`/`Ignore` → `Continue()`
  - Transition set → `RequestNextTransition(key, ErrorBoundary)` + `SkipToFinalize()`
  - Abort without transition → Fail → instance fault
- Error-boundary transitions set `IsErrorBoundaryTransition = true`.
- Error-boundary profile disables auto-chain and subflow.

## SubFlow Lifecycle

- **SubFlow (S)**: completion → output mapping → `ResumePipelineAsync` (`ExecMode.Resume`, `ResumeFrom = ClearBusyOnResumeStep`, `IsSubFlowResume = true`). Parent resumes from step 79.
- **SubProcess (P)**: completion → correlation complete + persist → no parent resume (fire-and-forget).
- On resume failure, correlation reverted in a new UoW.
- Start: `CreateInstanceInput` with parent metadata in `ExtraProperties`, `StrictIdempotency: true`.

### SubFlow Completion Window
If subflow is in terminal status (`Completed`/`Faulted`/`Passive`) while parent correlation is still open, State function shows **parent** main-flow transitions instead of subflow terminal view.

## Instance Data

- Immutable, SemVer-versioned: task results → Patch, schema additions → Minor, breaking → Major.
- Full-merge model: each version = full state + delta.
- `LatestData` = current; `DataList` = history.
- Queryable via filters on instance columns and `attributes.*` JSON paths.

## Related Instance Access (scripts)

- `context.Related` (`IRelatedInstanceAccessor`) — one hop only: `HasParent` (sync), `ParentAsync()` (up,
  from `parent.*` ExtraProperties), `SubAsync(key)` / `SubsAsync(key?)` / `SubKeysAsync()` (down, from
  correlations incl. completed).
- Key = `InstanceCorrelation.SubFlowName` (sub workflow key, no alias field). `SubAsync` = newest by
  `CreatedAt`; `SubsAsync` returns all, oldest first, batched (never N+1).
- `IsCompleted` = target instance status `C`; `CorrelationCompleted` = relationship closed (always null
  for the parent direction). They disagree during the subflow completion window — don't conflate them.
- Reads are **system-identity and unfiltered**: no query-role check, no `x-roles` filter, no extensions,
  no data-function cache. Copying a related field into instance data bypasses `x-roles` for that field —
  document it where you copy it.
- Runs inside the current transition's DB transaction — sees that transition's own uncommitted writes.
- Absence → `null`/empty list. Read failure or resolution-cap breach → `RelatedInstanceAccessException`.
  Reading after `ScriptContext` disposal → `ObjectDisposedException`.
- Same domain → in-process (`RoutedRelatedInstanceReader`); cross-domain → internal `related-data` /
  `related-data/batch` endpoints (no in-app authorization — network isolation only; see
  `docs/contracts/api-and-service-contracts.md` § Internal-Only Endpoints). Memoized per `ScriptContext`;
  cap `Workflow:Scripting:RelatedAccess:MaxResolutionsPerContext` (default 10). Full guide:
  `docs/runtime/script-related-instance-access.md`.

## View Selection

- Views array on states/transitions, evaluated in order, first matching rule wins.
- Rule: C# script implementing `IConditionMapping` with `ScriptContext` (Headers, QueryParams, Instance.Data, State, Transition).
- Last entry without a rule serves as fallback.
- `loadData: true` → instance data loaded with view response.

## TransitionExecutionContext

- Built by `TransitionContextFactory`: workflow from `IComponentCacheStore`, instance from `instanceRepository.GetActiveAsync`.
- Request payload overlays `Data` from `input.Data?.Attributes`.
- `Cache` dict for ephemeral data (e.g. `ScriptContext`) — cleared at Finalize.
- `Directives` accumulate mutations (next transition, post-commit jobs, epilogue skip).
- Same context reference flows through all steps — avoid redundant loads.

## Events & Instance Filtering (quick reference)

- **Events**: `event.mapping` on the workflow (`action=start`) or on a `triggerType: 3` transition
  (`action=transition`). Mapping implements `IEventMapping` → `EventMappingResult { InstanceKey, Body, Selector }`.
  Delivery is domain-owned Dapr Subscription YAMLs routing topics to
  `POST /api/v1/{domain}/workflows/{workflow}/instances/events?action=...`.
  **Response is a Dapr pub/sub protocol body** (`EventDeliveryResponse`), never an instance DTO — Dapr
  reads the top-level `status` field as its signal, so an `InstanceStatus` code there (`"B"`) causes
  endless redelivery. Processed or no-active-match ⇒ `200 {"status":"SUCCESS"}`; permanently
  unprocessable (bad `transitionKey`/action/domain, non-JSON body, missing event definition) ⇒
  `200 {"status":"DROP","reason":…}` + `EventDeliveryDropped` warning; transient failures keep non-2xx
  so the broker retries. Full guide: `docs/domain/event-driven-workflows.md`.
- **Filtering**: author instance queries with fluent `InstanceQuery` (default script import), never
  hand-concatenated GraphQL JSON. Terminals: `.First()/.Last()` → event `Selector` (single-resolve);
  `.Build()` → `InstanceQuerySpec` for `GetInstancesTask.SetFilterSpec(...)` (preferred; in-process when
  same-domain) or `spec.ToFilterJson()/ToSortJson()/ToQueryString()` for raw `DaprServiceTask` calls.
  Operators: Eq/Ne/Gt/Ge/Lt/Le/Like/StartsWith/EndsWith/In/NotIn/Between/IsNull/Includes + OrGroup/Not;
  GroupBy + Count/Sum/Avg/Min/Max (list-only, aggregations nest under groupBy). Full guide with operator
  table and migration examples: `docs/runtime/instance-filtering-and-queries.md`.

## vnext-meta Package

- **Purpose**: Runtime metadata for offline consumption (Forge Studio, CLI, domain packages).
- **npm**: `@burgan-tech/vnext-meta`
- **Location**: `vnext-meta/`

| File | Purpose |
|------|---------|
| version-manifest.json | Runtime → schema version map |
| features.json | Engine capabilities, API endpoints, integration status |
| deprecations.json | Fields/features past end-of-life |
| migrations.json | Version migration steps and guides |
| known-issues.json | Active bugs, workarounds, affected version ranges |
| component-registry.json | Task/function/extension catalog with availability |
| performance-profiles.json | Runtime limits and thresholds |
| security-policy.json | Enforced security rules by scope |

- **Consumers**: Forge Studio (designer validation, feature gating, inline warnings), vnext-template CLI (`npm run validate`), domain packages (CI pre-publish checks).
- **Principle**: machine-readable, offline, no runtime connection required.
- **Version alignment**: package version equals runtime `<Version>` in `common.props`; published via `publish-npm` GitHub Actions job.
