---
paths:
  - "src/**"
  - "orchestration/**"
  - "execution/**"
  - "workers/**"
  - "modules/**"
  - "tools/**"
  - "test/**"
  - "vnext-meta/**"
  - "**/*.cs"
  - "**/*.csproj"
---

# vNext Workflow Developer — Domain Knowledge (path-scoped: loads with runtime code)

This rule complements the workflow concepts in `AGENTS.md`. It is a quick-reference card for
implementing or reviewing pipeline / transition / subflow code: imperative rules, forbidden moves and
key symbols. The reasons, measurements and incident history behind each rule live in the linked
`Full guide:` pages — read them before changing the behaviour a bullet pins.

## Transition Pipeline Order

| Order | Step | Responsibility |
|-------|------|----------------|
| 5 | HandleCancelPreflightStep | Detect cancel/exit; short-circuit if instance already completed |
| 10 | ForwardToActiveSubflowStep | Queue post-commit forward to active subflow; skip epilogue. Does not forward `updateData` or parent shared `$self` transitions. |
| 19 | SetBusyStep | Set instance status to Busy and persist |
| 20 | CreateTransitionRecordStep | Create transition record; duplicate key guard |
| 21 | HandleUpdateDataDataOnlyStep | Parent with active SubFlow: persist update data and skip lifecycle/epilogue |
| 25 | ResourceLockStep | Acquire/release/extend resource locks via script |
| 30 | RunOnExecuteTasksStep | Run transition OnExecute tasks |
| 38 | ApplyTimeoutStateStep | Apply timeout target into context before exit |
| 39 | CancelScheduledJobsStep | Cancel scheduled jobs for current state |
| 40 | RunOnExitTasksStep | Run leaving-state OnExit tasks |
| 50 | ChangeStateStep | Persist state change |
| 60 | RunOnEntryTasksStep | Run target-state OnEntry tasks |
| 70 | HandleSubFlowStep | Start subflow correlation; enqueue StartSubflowJob |
| 75 | HandleLongPollTerminationStep | Pause after state entry and arm acknowledgment fallback when configured |
| 79 | ClearBusyOnResumeStep | Clear busy on subflow resume path |
| 80 | RunAutomaticTransitionsStep | Evaluate auto-transition conditions; set NextTransition |
| 90 | ScheduleTransitionsStep | Schedule future transitions — skipped when auto selected a winner |
| 100 | HandleFinishStep | Complete/cancel instance on finish states |
| 110 | FinalizeTransitionStep | Complete transition record; dispose script cache |
| 112 | ResolveAvailableStep | Resolve deferred Active status |

**Epilogue order is Auto → Schedule.** When Auto picked a winner (`Directives.NextTransition` set),
`ScheduleTransitionsStep` arms no timer. Do not reintroduce "arm, then cancel on the next hop".

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
| AutoChain | Automatic (1) | Preflight, ForwardSubflow, SetBusy, ApplyTimeoutState (ResourceLock runs) |
| Scheduled | Scheduled (2) | Preflight, ForwardSubflow |
| Event | Event (3) | Preflight, ForwardSubflow |
| ErrorBoundary | Error boundary | Preflight, ForwardSubflow, ResourceLock (Auto and SubFlow are **not** excluded in current code) |

Resolution: `IPipelineProfileResolver.Resolve(workflowContext, transitionContext)` — if
`IsErrorBoundaryTransition` → ErrorBoundary; else by the **workflow context's** `TriggerType` (not
the transition definition's — the two can disagree and the inbound trigger is authoritative).
The plan is built from `ExcludedStepOrders` alone (`TransitionExecutor.BuildExecutionPlan`); the old
"allow subflow" flag was deleted, not given teeth — do not add it back.

### Self-target composition — `updateData` ONLY

- `PipelineExecutionProfile.ForSelfTarget(base)` is composed on top of the base (`Manual+Self`, …)
  when `TransitionExecutionContext.SkipsStateLifecycle()` is true; it adds `CancelScheduledJobs (39)`,
  `OnExit (40)`, `OnEntry (60)`, `Schedule (90)` to the exclusions.
- `SkipsStateLifecycle() = IsSelfTargetTransition() && IsUpdateDataTransition()` — only `updateData`.
  Every other `$self` transition (a `$self` **shared transition** above all) runs the FULL lifecycle.
- The `+Self` name is about the target, not the policy; selection lives in `PipelineProfileResolver`.
- Only the authored `$self` counts. Never compare a literal target to the current state — do not
  reintroduce the comparison (it killed the initial state's OnEntry and turned retry into a no-op).
- `ChangeState (50)` must keep running (only step that sets `context.Target`, read by step 80);
  `OnExecute (30)` still runs. A parent with an open SubFlow stops earlier at step 21.
- Full guide: `docs/architecture/workflow-execution-pipeline.md`.

## Instance Repository Include Strategy

- Pipeline steps do NOT call EF `Include` directly — includes are applied at load time:
  `GetActiveAsync` → `GetResultAsync` → `FindByIdentifierAsync` → `EfCoreInstanceRepository.WithDetailsAsync()`
  (`DataList` or latest-only under `WorkflowExecution:LatestOnlyInstanceLoading`, plus
  `Include(ChildCorrelations.Where(!IsCompleted))`, split queries).
- `GetResultAsync(includeDetails: false)` is lean; history paths use `AsNoTracking` + filtered includes.
- Post-commit settlement (`FindForPostCommitSettlementAsync(id, includeLatestData)`) always loads open
  correlations; add a new `LatestData` reader to `PostCommitParentMutationService.NeedsLatestDataForSettle`,
  never by re-widening the include.
- **Incidents are never included.** Guards read `Instance.HasActiveIncident`; readers call
  `IInstanceRepository.LoadActiveIncidentsAsync` first. Never attach read-back rows to the EF navigation
  (`Instance.AcceptLoadedIncidents` keeps them detached — attaching caused `23505 PK_InstanceIncidents`).
- A task step records the incident BEFORE its own `UpdateAsync(instance, autoSave: true)`; nothing
  records one after a save. Resolve is set-based (`Instance.ResolveOpenIncidents()`), never single-row.
- Retry loads no-tracking (`GetResultAsReadOnlyAsync`) and unfaults with the `TryUnfaultAsync` CAS,
  committed before `ExecuteRetryAsync`. Never mutate an ambient-tracked aggregate and leave the write
  to an inner `RequiresNew` scope (`Instance:100027`).
- Full guide: `docs/runtime/instance-incident-persistence.md`.

## Long-Polling / State Function

- `FunctionTypeConst.Longpooling`: `GET /functions/state` → `200` | `304`; no server-side hold.
  ETag: `LatestData?.ETag` (entity), `IRepresentationEtagService.Generate(output)` (representation).
- Bump `StateFunctionCache.ResponseShapeVersion` (currently `v14`) in the same commit as any change to
  what the state body carries — otherwise parked pollers keep getting 304.
- Every built-in instance function descends an active subflow — except `data`.
- Parent overrides resolve in ONE place per kind and REPLACE (never merge): `IsQueryAllowedAsync`,
  `TransitionAuthorizationManager.EffectiveTransitionGrants`, `GetSubFlowViewWithOverrideAsync`.
- `effectiveStatus` is served CLAMPED via `Instance.GetEffectiveStatus` (`InstanceStatus.IsTerminal`
  on either side ⇒ own `Status`). Never rewrite it as `HasActiveSubFlow ? EffectiveStatus : Status`.
- `Instance.Type` (`R`/`S`/`P`) is write-once and NOT the relationship check; do not unify
  `IsSubFlow`/`IsSubItem`/`parent.*` readers with it. Filter as `instanceType`, never bare `type`.
- `incident` block carries **links, not content** — never put incident fields back in the body.
  Scheduled entries come from `JobType.ScheduledTransition` rows and are outside the fingerprint (#864).
- `interaction.longPoll` admits through `ILongPollInteractionGate` (`roles` OR one `rule`, fail-closed);
  a rule-gated body is never cached. Full guide: `docs/domain/long-poll-termination.md`.
- `interaction` presence: `terminate: true` → only while `IsAwaitingLongPollAck`, with `ack.href`;
  `terminate: false` → whenever in the declaring state, no `ack`, no token. Never gate the
  non-terminating block on the token. Full guide: `docs/domain/long-poll-termination.md`.
- Full guide: `docs/runtime/state-function-cache-and-etag.md`; timeout block, `$timeout` resolution at
  order 20 and `timer.reset` (read nowhere): `docs/runtime/workflow-timeout.md`.

## Field masking (`x-masking` / `x-encryption`)

- Order `x-roles → x-masking → x-encryption` in ONE pass (`SchemaFieldFilterService` →
  `InstanceDataRoleFilter.Apply`), one evaluator. Never add a second masking stage — the data-function
  cache stores the filtered body (generation `v3` + `-nomask` in key AND ETag; token rows never cached).
- `x-masking.roles` / `x-encryption.roles` are allow-only exemption lists; `deny` is rejected at publish;
  a role-less caller never satisfies a role-bound grant (`IsUnprovableRoleBoundGrant`).
- ONE read path: `IInstanceDataReadService` (GET, list, data function, sync response, Get* tasks). Do not
  call the filter from a surface directly. Get* tasks read as their header set (`AppendCallerCredential`).
- `InstanceData.Data` is the column as stored (tokens included). Decrypt only via
  `IInstanceDataProtector.UnprotectAsync` / `context.Instance.DecryptAsync`; never reintroduce a plaintext
  member; never write `InstancesData."Data"` outside `InstanceDataWriteService`. Prefix-driven, never schema-driven.
- Per-instance key + salt in `InstanceSecrets`, created only by the write funnel, L1 cache, **never Redis**.
  A sub item hands its parent PLAINTEXT (`ISubItemEventDataResolver`).
- Full guide: `docs/domain/field-masking.md`.

## Task / Action History (system functions)

- `functions/tasks` returns the `InstanceTasks` journal; `functions/actions?taskId=` one row's
  `InstanceActions` (`Instance:100039` / `Instance:100038`). Same `queryRoles` gate as state.
- **Metadata only.** Never serve `Request`/`Response`/`InvocationResult` payloads; keep the SQL
  projection (`InstanceTaskHistoryRow`), never materialize the entity.
- `InstanceActions` has no writer. Full guide: `docs/runtime/instance-task-and-action-history.md`.

## Well-Known Transitions (`cancel` / `updateData` / `exit`)

- Listed in `availableTransitions` under the **configured key** (never the alias
  `update-parent-data`), `kind` = `cancel` | `updateData` | `exit`; merged from parent into a subflow.
- `ForwardToActiveSubflowStep` (10) never forwards `updateData`; `updateData.target` must be `$self`.
- `roles` are enforced at discovery only, as for every transition type.
- Execution gate: `WellKnownTransitionSpecification` (`Transition:100024`), keyed by
  `Workflow.IsWellKnownTransitionKey` (aliases AND configured custom keys).
- Full guide: `docs/domain/well-known-transitions.md`.

## `availableIn` (shared + cancel/updateData/exit)

- Shapes: bare state key or `{ state, roles }` (`AvailableInJsonConverter`); a role-less object
  normalizes to a string — do not add an "authored shape" flag.
- Never read `AvailableIn` directly: use `Transition.IsAvailableInState(stateKey)` / `FindAvailableIn(stateKey)`.
- `availableIn` is NOT the gate for a STATE transition: `TransitionAuthorizationManager` asks
  `IsStateScoped(workflow, key)` and requires the key on the CURRENT state.
- Roles compose as AND with `transition.roles`. State function + `authorize` check state+roles,
  execution checks state only — go through `IsTransitionAllowedInStateAsync` /
  `FilterAuthorizedTransitionKeysAsync`, never a third path. `grantsForPrefetchHint` must include per-state grants.
- Full guide: `docs/domain/well-known-transitions.md`, `docs/domain/role-grant-authorization.md`.

## Role Grants (validation + evaluation)

- Forms: static, predefined (`$InstanceStarter`, `$PreviousUser`, `$InstanceBehalfOfStarter`,
  `$PreviousBehalfOfUser`), dynamic (`$user.` / `$userBehalfOf.` / `$role.` + `$.context.<path>`, Ordinal).
  Never re-implement parse rules — use `DynamicRoleGrant.Classify` (shares `TryParse`).
- **One evaluator**: `IRoleGrantEvaluator` via `ITransitionAuthorizationManager.CreateEvaluatorAsync`.
  `authorized = DenyGroupOk AND AllowGroupOk`, deny first; empty set ⇒ allow.
- Grants evaluate three-valued (Kleene): a role-bound leaf is Unknown for a role-less caller, a deny fires on Yes or Unknown, an allow only on Yes; a denied role is not
  bought back by an allowed one. Never loop the caller's roles returning on the first allowed one.
- Every decision point takes the WHOLE role set; `ICallerRoleResolver.SingleRoleOf` is for cache scoping
  (`CallerScopeHash`) only. Never read `currentUser.Roles` directly — use `currentUser.ResolveCallerRoles(headers)`.
- Batch: one evaluator per instance/schema; pass every surface the same `AuthorizationRequestContext`.
- `queryRoles` is ANSWERED (by `authorize?queryRoles=true`), not enforced on read paths — do not add
  the gate back. `authorize` decides queryRoles at the deepest active subflow leaf only (no chain AND; a parent restricts via `subFlow.overrides.states.<state>.queryRoles`); `?ack=true` is the long-poll ack pre-flight.
- The `queryRoles` gate reads the instance's OWN `CurrentState`, never `EffectiveState`.
- `transition.roles` is not enforced at execution, by design — do not "fix" it. `cancel`/`exit` are
  parent-retained. Under `morph-idm` a non-blank `role` header replaces the service's answer.
- Full guide: `docs/domain/role-grant-authorization.md`, `docs/domain/authorize-function.md`.

## Sync vs Async

- `sync=true` blocks to completion; `sync=false` (default) returns `{ id, status }` for polling.
- A flow/transition `executionType` (`S`/`A`) overrides `sync` (transition > flow > query); never for
  runtime-internal calls (`SuppressResponseEnrichment`). Automatic continuations always run inline.
- Runtime-generated child start, active-child forward and descended retry always use `sync=true`.
- A sync response never evaluates extensions; `IInstanceExtensionService` stays out of
  `InstanceCommandAppService` — do not reintroduce the pass.
- Full guide: `docs/runtime/execution-type.md`.

### Activation episode (trace)

- Episode = trigger → rest point. One trace + one backdated `Instance.Activation/{key}` span, emitted
  **after** the UoW commit, never at `Transition.Settle`. Kind `Internal`, never `Consumer`.
- A new lane carrier must copy all four: `EpisodeStartedAt` / `EpisodeTrigger` /
  `EpisodeTransitionKey` / `EpisodeTraceRoot` (beside `TraceRoot`), else `vnext.activation.partial=true`.
- Only status owners emit (`OwnsStatus`); a lost CAS yields no verdict; `Instance.Fault` always emits.
  A hop that enqueued a continuation would not emit (`ContinuationEnqueued` → `chainSettled:false`),
  but that branch is unreachable while only `InlineContinuationStrategy` is registered.
- `ActivationActivity` must keep its `Activity.Current` save/restore (`Emit_restores_Activity_Current`).
- Full guide: `docs/runtime/trace-lanes.md` § Activation episode, `docs/runtime/trace-span-tree.md`.

## Locking — one lock, at the status change

- **The Busy flag is the mutex.** Exactly one short lock per hop, on `ctx.LockKey`
  (`vnext:{domain}:{flow}:{id}`): sync admission (`ReserveAsync` / `TakeOverAsync`) or async
  `ITransitionAdmissionService.AcceptAsync`. Never hold it across the pipeline body.
- Order: fast-fail Busy check → validation → lock → flip → work → release.
- Never call `ReserveAsync`/`TakeOverAsync`/`ReserveSubflowChainAsync`/`Release*` inside
  `AcceptAsync`'s callback — the lock is held and non-reentrant.
- The duplicate-active-job guard lives in that critical section; no partial unique index can replace it.
- cancel/exit/timeout flip Busy at the accept; `updateData` (`Unconditional`) takes NO lock and NO
  duplicate-job guard. Do not bring back `{LockKey}:enqueue` or per-kind lock keys.
- Full guide: `docs/runtime/status-locking.md`.

## Status / State / Type Semantics

Enum values (Instance Status, State Types, State Sub Types, Trigger Types): `AGENTS.md` § Status / State / Type Semantics.

## Error Boundary

- Levels Task → State → Global (`CompiledBoundaryChain`); sort `EffectivePriority` ASC → specificity
  DESC → definition order. Actions: `Abort`, `Retry`, `Rollback`, `Ignore`, `Notify`, `Log`.
- `BoundaryOutcomeHandler`: `Log`/`Ignore` → `Continue()`; transition set →
  `RequestNextTransition(key, ErrorBoundary)` + `SkipToFinalize()`; abort without transition → Fail → fault.
- Error-boundary transitions set `IsErrorBoundaryTransition = true`. Profile exclusions: see the table above.

## SubFlow Lifecycle

- **A state starts a SubFlow, only a SubFlow:** `state.subFlow.type` must be `S`
  (`WorkflowValidator.ValidateStateSubFlowType`). A `P` is started only by `SubProcessTask` (`TaskType.SubProcess = 14`).
- `Instance.HasActiveSubFlow`, `Instance.Subflow`, `Instance.AddCorrelation` and
  `HasActiveCorrelationForSameState` read `S` only — correct, not narrow; do not widen them to `P`.
- `S`: completion → output mapping → `ResumePipelineAsync` from `ClearBusyOnResumeStep` (79).
  `P`: correlation complete, no parent resume. On resume failure the correlation is reverted in a new UoW.
- Runtime child calls force `sync=true` and set `SuppressResponseEnrichment` (identity-only answer) —
  never read attributes off a sub-start or forward response.
- Completion window: child terminal while parent correlation open ⇒ state function shows parent transitions.
- Full guide: `docs/architecture/subflow-execution.md`.
- **Initial state is optional:** with none declared the instance is born in the implicit `$start` state and
  `startTransition.target` (mandatory, a declared state key) decides the entry; see
  `docs/domain/well-known-transitions.md#well-known-state-keys`.

### Parent overrides are resolved child-side

- Stamps `subflow.state_role_overrides` / `subflow.transition_role_overrides`, parsed only by `SubFlowOverrideStamp`.
- Long-poll: always `Instance.ResolveEffectiveLongPoll(state)`, never `State.LongPoll*`. Views:
  `Instance.ResolveViewOverride`, keyed by `CurrentState`, rules never overridden.
- Legacy `overrides.views` / `viewOverrides` is parent-side and deprecated; mixing with scoped views is an error.
- Full guide: `docs/domain/subflow-overrides.md`.

### `sub:state-changed` and `SubflowStateService`

- `Instance.ChangeState` only ARMS the event; `Instance.PublishPendingSubStateChange()` publishes it
  once per episode at the rest point. Excluded: open SubFlow correlation, `Faulted`.
- Never remove the explicit flush at creation in `InstanceCommandAppService` — it is the parent's only
  notification when the start transition targets the initial state.
- `SubflowStateService` takes the per-sub-item lock; equal `ChangedAt` is re-applied, only older rejected.
- Never add a correlation-first CAS before the parent load — write order stays P → C (40P01 otherwise).
- Full guide: `docs/runtime/event-publish-modes.md`.

### Accept-time chain reserve

- Async `IsSubflowForward` accepts call `ReserveSubflowChainAsync` (`MarkBusyWithPropagationAsync`,
  not the `Try…` variant) before the 202; the sync path deliberately does not reserve.
- The relay claims it via `TransitionInput.ChainReserved` → `AdmissionKind.OwnerReentry`; never claim
  a reserve that was not taken. Cross-domain: internal `POST .../internal/subflow-forward` only.
- Full guide: `docs/architecture/subflow-chain-reserve.md`.

## Instance Data

- Immutable, SemVer-versioned: task results → Patch, schema additions → Minor, breaking → Major.
- Full-merge model: each version = full state + delta. `LatestData` = current; `DataList` = history.
- Queryable via filters on instance columns and `attributes.*` JSON paths.
- `attributes.history: none` (one-shot flow): no `InstanceTransitions`/`InstanceTasks`; data is buffered
  (`InstanceDataBuffer`, outside the EF navigation) and flushed at Finish, SubFlow handoff or fault; a
  non-Finish rest faults (`Instance:100046`); a `none` parent needs `none` S children (`Instance:100044`);
  `/retry` → 409. Full guide: `docs/runtime/history-mode.md`.

## Related Instance Access (scripts)

- `context.Related` (`IRelatedInstanceAccessor`) — one hop: `HasParent`, `ParentAsync()`,
  `SubAsync(key)` / `SubsAsync(key?)` / `SubKeysAsync()`; key = `InstanceCorrelation.SubFlowName`.
- `IsCompleted` (instance `C`) ≠ `CorrelationCompleted` (relationship closed) — don't conflate.
- Reads are system-identity and unfiltered (no `x-roles`) — document every copy into instance data.
- Failure → `RelatedInstanceAccessException`; cap `Workflow:Scripting:RelatedAccess:MaxResolutionsPerContext`.
- Full guide: `docs/runtime/script-related-instance-access.md`.

## View Selection

- `views[]` on states/transitions, first matching `IConditionMapping` rule wins; last rule-less entry
  is the fallback. `loadData: true` → instance data loaded with the view.

## TransitionExecutionContext

- Built by `TransitionContextFactory` (workflow from `IComponentCacheStore`, instance from `GetActiveAsync`).
- Inline hops reuse via `CreateFromPreloaded(previous.Workflow, previous.Instance)`; never carry a
  tracked instance across post-commit, subflow callback or retry boundaries.
- `Cache` is cleared at Finalize; `Directives` accumulate mutations; payload overlays `input.Data?.Attributes`.
- Full guide: `docs/architecture/inline-chain-context-reuse.md`.

## Events & Instance Filtering (quick reference)

- Event delivery returns a Dapr pub/sub body (`EventDeliveryResponse`), never an instance DTO:
  `SUCCESS`, or `DROP` + `EventDeliveryDropped` for permanently unprocessable; transient ⇒ non-2xx.
  Full guide: `docs/domain/event-driven-workflows.md`.
- Author queries with fluent `InstanceQuery`, never hand-concatenated GraphQL JSON; prefer
  `GetInstancesTask.SetFilterSpec(...)`. Full guide: `docs/runtime/instance-filtering-and-queries.md`.

## vnext-meta Package

- `@burgan-tech/vnext-meta` (`vnext-meta/`): offline, machine-readable runtime metadata —
  version-manifest, features, deprecations, migrations, known-issues, component-registry,
  performance-profiles, security-policy.
- Consumers: Forge Studio, vnext-template CLI (`npm run validate`), domain-package CI.
- Package version equals runtime `<Version>` in `common.props`; published by the `publish-npm` job.
