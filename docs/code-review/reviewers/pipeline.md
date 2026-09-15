# Reviewer: pipeline

Owns the transition pipeline and everything that decides **whether a step runs, in what order, and
who holds the instance**: `LifecycleOrder`, `PipelineExecutionProfile`, subflow lifecycle, locking
and the Busy flag, the error boundary, and `TransitionExecutionContext` reuse.

Source of truth, in order: `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs`
and `PipelineExecutionProfile.cs` → [`.claude/rules/vnext-workflow-developer.md`](../../../.claude/rules/vnext-workflow-developer.md)
→ [Workflow Execution Pipeline](../../architecture/workflow-execution-pipeline.md),
[Subflow Execution](../../architecture/subflow-execution.md),
[Inline Auto-Chain Context Reuse](../../architecture/inline-chain-context-reuse.md).
When a rule and the code disagree, **the code wins** — say so in the finding instead of citing the rule.

## 1. Step discipline (`pipeline/step-*`)

- [ ] `pipeline/step-single-responsibility` — the step does one thing; it does not mix a state change with task execution or persistence it does not own.
- [ ] `pipeline/step-order` — a new order constant is declared in `LifecycleOrder` and slots into the chain **as the code has it** — read `LifecycleOrder.cs`, or the table in `.claude/rules/vnext-workflow-developer.md` § Transition Pipeline Order. Do not review the order from memory or from a copy: the copy this checklist replaced had drifted two steps behind the code. A literal order number outside `LifecycleOrder` is CRITICAL.
- [ ] `pipeline/step-skipto` — `SkipTo(order)` targets an existing `LifecycleOrder` constant; `SkipToFinalize()` is used instead of a hardcoded `SkipTo(110)`.
- [ ] `pipeline/step-result` — the step returns `Result<StepOutcome>`; business failures are `Result.Fail`, not exceptions.
- [ ] `pipeline/step-directives` — `PipelineDirectives` are mutated through `With(Action<PipelineDirectives>)` or the step's own documented responsibility, not from a helper reached sideways.
- [ ] `pipeline/step-registration` — a new step is registered in `PipelineServiceCollectionExtensions`. An unregistered step silently never runs.
- [ ] `pipeline/step-profile` — every `PipelineExecutionProfile` exclusion list is revisited when a step is added. A step that must not run under AutoChain / Scheduled / Event / ErrorBoundary is excluded explicitly; the PR says why for each profile it left alone.

## 2. Profiles and self-target (`pipeline/profile-*`)

- [ ] `pipeline/profile-resolution` — `IPipelineProfileResolver` resolves from the **workflow context's** `TriggerType`, not the transition definition's; `IsErrorBoundaryTransition` wins over both.
- [ ] `pipeline/profile-self-target` — `ForSelfTarget` composition is applied only when `SkipsStateLifecycle()` is true, i.e. `$self` **and** `updateData`. A change that makes any other `$self` transition skip OnExit/OnEntry/Schedule is CRITICAL.
- [ ] `pipeline/profile-self-comparison` — no new code compares the target state key to the current state to infer "self". Start pre-positioning, retry re-runs and genuine self-loops all produce that equality; the comparison has already broken initial-state OnEntry and retry once. Only the literal `$self` marker counts.
- [ ] `pipeline/profile-changestate-kept` — `ChangeState (50)` still runs on the `updateData` path; it is the only step that sets `context.Target`, which `RunAutomaticTransitionsStep (80)` reads.
- [ ] `pipeline/profile-epilogue-order` — the epilogue stays **Auto (80) then Schedule (90)**. Arming timers before the auto winner is chosen re-introduces the removed cancel-churn.

## 3. Known pitfalls that must not come back (`pipeline/pitfall-*`)

Each of these is a regression the repo has already paid for; treat a reintroduction as CRITICAL.

- [ ] `pipeline/pitfall-order9` — there is **no** `HandleUpdateDataPreflightStep` at order 9. Parent `updateData` with an open SubFlow correlation short-circuits at `HandleUpdateDataDataOnlyStep (21)`, and `ForwardToActiveSubflowStep (10)` never forwards it.
- [ ] `pipeline/pitfall-eventhook` — the EventHook infrastructure is deleted. A new `IEventPublishHook`, `EventHookAttribute` or synchronous pre-commit publish path is wrong; see the contract reviewer for the replacement.
- [ ] `pipeline/pitfall-errorboundary-profile` — the ErrorBoundary profile's exclusion set is what `PipelineExecutionProfile.cs` says it is (summarized in `.claude/rules/vnext-workflow-developer.md` § PipelineExecutionProfile), and the Auto step's presence in it is deliberate. A PR that adds or removes an exclusion here must carry a decision record.

## 4. SubFlow lifecycle (`pipeline/subflow-*`)

- [ ] `pipeline/subflow-resume` — SubFlow (`S`) completion resumes the parent from `ClearBusyOnResumeStep` (79) with `ExecMode.Resume`, `IsSubFlowResume = true`; SubProcess (`P`) does not resume the parent at all.
- [ ] `pipeline/subflow-sync` — runtime-generated child start, active-child forward and descended retry calls force `sync=true` regardless of the caller's mode and of `S` vs `P`, and set `SuppressResponseEnrichment`. Nothing reads attributes off a sub-start or forward response.
- [ ] `pipeline/subflow-idempotency` — `StrictIdempotency: true` on the child start, with complete parent metadata in `ExtraProperties` (parentId, parentKey, domain, flow, version, state, transition, flowType).
- [ ] `pipeline/subflow-revert` — resume failure reverts the correlation in a **new** UoW so the retry path still has something to claim.
- [ ] `pipeline/subflow-chain-reserve` — an async transition on a parent with an active SubFlow reserves the chain down to the leaf (`MarkBusyWithPropagationAsync`, not the `Try…` variant) **and** the relay claims that reserve via `ChainReserved` → `IsPreReserved`. A claim without a reserve, or a reserve without a claim, is CRITICAL. The sync path deliberately does not chain-reserve.
- [ ] `pipeline/subflow-state-notify` — `sub:state-changed` is armed by `Instance.ChangeState` and published once per activation episode at the rest point. A per-hop publish, or removing the explicit flush in `InstanceCommandAppService`, is CRITICAL.

## 5. Locking and the Busy flag (`pipeline/lock-*`)

- [ ] `pipeline/lock-one-per-hop` — exactly one distributed lock per request-handling hop, on `ctx.LockKey`, held only across the status check-and-set. No lock is held across context creation, schema/policy validation or the pipeline body.
- [ ] `pipeline/lock-ordering` — fast-fail Busy check → validation → lock → flip → work → release.
- [ ] `pipeline/lock-no-nesting` — nothing inside `AcceptAsync`'s callback calls `ReserveAsync` / `TakeOverAsync` / `ReserveSubflowChainAsync` / `Release*`; `InstanceStatusLock` is single-attempt and non-reentrant.
- [ ] `pipeline/lock-updatedata-exempt` — `updateData` (`Unconditional`) takes no lock and no duplicate-job guard on either path. Re-adding either loses data for parallel notifiers.
- [ ] `pipeline/lock-dup-job-guard` — the duplicate-active-job guard stays inside the lock's critical section; it has no DB constraint behind it and a partial unique index cannot replace it.

## 6. Error boundary (`pipeline/boundary-*`)

- [ ] `pipeline/boundary-chain` — `CompiledBoundaryChain` resolves Task → State → Global, sorted `EffectivePriority` ASC → specificity DESC → definition order.
- [ ] `pipeline/boundary-mapping` — `BoundaryOutcomeHandler` mapping is intact: `Log`/`Ignore` → `Continue()`; transition set → `RequestNextTransition` + `SkipToFinalize()`; abort without transition → `Fail` → fault.
- [ ] `pipeline/boundary-incident-order` — a task step records the incident **before** its own save, so the row and `HasActiveIncident` commit together. Recording after the save produces two rows for one failure.
- [ ] `pipeline/boundary-resolve-setbased` — incident resolution stays set-based (`ResolveOpenIncidents` / `ResolveAllAsync`); no single-row resolve API is introduced.
- [ ] `pipeline/boundary-retry` — a retry policy names `maxRetries` and a backoff; no unbounded retry.

## 7. Execution context and includes (`pipeline/context-*`)

- [ ] `pipeline/context-reuse-boundary` — `CreateFromPreloaded` reuse stays inside one uninterrupted pipeline/UoW. A tracked instance carried across a post-commit, retry or subflow-callback boundary is CRITICAL.
- [ ] `pipeline/context-no-include` — pipeline steps do not call EF `Include` directly; includes belong at load time (`WithDetailsAsync`, `FindForPostCommitSettlementAsync`, `FindForSubflowStateChangeAsync`).
- [ ] `pipeline/context-no-requery` — no repository call for data the context already holds.
- [ ] `pipeline/context-cache` — `Cache` entries (e.g. `ScriptContext`) are disposed/cleared at Finalize; `ScriptContext` is built via `GetOrBuildScriptContextAsync`, never constructed by hand.
- [ ] `pipeline/context-incidents` — incidents are never added to the default include set; readers call `LoadActiveIncidentsAsync` behind the `HasActiveIncident` flag, and loaded rows are not attached to the EF navigation.

## 8. Activation episode and tracing (`pipeline/episode-*`)

- [ ] `pipeline/episode-carrier` — a new async carrier copies **all three** of `EpisodeStartedAt`, `EpisodeTrigger`, `EpisodeTransitionKey` beside `TraceRoot`. A missing start degrades the consumer to a partial span.
- [ ] `pipeline/episode-emit-point` — the activation span is emitted **after** the UoW commit, never at `Transition.Settle`, and only by a status owner (`OwnsStatus`) — except `Instance.Fault`, which always emits.
