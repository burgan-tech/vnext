# End-to-End Trace/Span Tree — Design Spec

**Date:** 2026-08-25
**Status:** Approved (design), implementation plan pending
**Owner:** Platform team (vNext runtime)

## 1. Goal

Every time-consuming mechanism in the transition lifecycle must be visible as a span in the
trace tree, always — in the default (Business) detail level, not only in Verbose. Concretely:

- Pipeline steps, individually, with the tasks of OnEntry/OnExit/OnExecute visible one by one
  under their step group.
- Script engine work: compilation (cold vs cache hit) and execution (input mapping, output
  mapping, rules, conditions, lock scripts, subflow mappings).
- Context loading (workflow definition fetch + instance load) and validation.
- Distributed lock acquire/release around the status flip.
- Component cache reads with L1/L2 hit information as tags and the L2 (Dapr/Redis) read
  duration visible.
- Instance data persistence.

### Decisions taken (with the user, 2026-08-25)

1. **Detail level:** detailed spans are always on (Business mode). Not gated behind Verbose.
2. **Script compile span:** the earlier decision ("no compile span, metric/tag only" from the
   script-perf work) is **reversed** — compile gets a real span. The existing accumulator tags
   (`vnext.script.compile.count/miss.count/total_ms` on the task span) and the `script.compile`
   span event are **kept** for query compatibility.
3. **Locks:** real spans (not events), including failed acquires.
4. **Approach A:** vNext-side only. No Aether change. See §3.

## 2. Current state (survey, 2026-08-25)

Already present:

- All 21 pipeline steps are wrapped from a single point
  (`TransitionExecutor.cs:142` → `PipelineStepActivityHelper.StartStepActivity`), but creation
  is gated on `AetherTracingRuntime.IsVerbose` and names use the `[{Order}] {StepName}`
  convention, which Aether's `BusinessSpanFilterProcessor` suppresses at export in Business mode
  (`aether/.../BusinessSpanFilterProcessor.cs:22`, condition: `DisplayName.StartsWith("[")`).
- Task phases: `Task.PrepareInput` / `Task.Invoke` / `Task.ProcessOutput`
  (`TaskExecutorBase.cs:60/104/136`), `FanOut.Item`, `Trigger.Local`.
- Subflow services (start/forward/complete/fault/cancel), background job handlers, FlatLane,
  `PostCommitExecutor`.
- Component cache at `CacheSet` level with `cache.hit`, `cache.l1_hit`, `cache.generation`,
  `cache.store`, `cache.coalesced` tags (`CacheActivityHelper.cs:22-37`).

Missing entirely (no `Activity` at all):

- Script engine (`modules/BBT.Workflow.Modules.Scripting` — compile core writes tags/events only).
- Distributed lock (`InstanceStatusLock`, `NpgsqlDistributedLockService`) and
  `TransitionAdmissionService` critical section.
- Validation (`TransitionValidationService`, schema validation).
- Context load (`TransitionContextFactory`, `GetActiveAsync`).
- Instance data append/persist.

Config gap (bug, fix regardless): `BBT.Workflow.Tasks`, `BBT.Workflow.SubFlow`,
`BBT.Workflow.BackgroundJobs` ActivitySources are **not listed** in
`Telemetry:Tracing:AdditionalSources` in any host's appsettings — the wildcard
`BBT.Workflow.Execution*` does not cover them, so those existing spans are never exported.

## 3. Approach: vNext-side only (Approach A)

The Business-mode suppression in Aether keys solely on the `[` DisplayName prefix. Instead of
adding an exemption seam to Aether (release + coordination cost), vNext stops producing
`[`-prefixed names and drops the `IsVerbose` creation gate:

- Step spans are renamed `Step.{Name}` (e.g. `Step.RunOnExitTasks`); the order moves to a
  `vnext.step.order` tag.
- With no `[` prefix, `BusinessSpanFilterProcessor` never matches; the spans export in Business
  mode with zero Aether changes.
- Consequence (accepted): vNext detail spans can no longer be hidden via config. Verbose remains
  meaningful for Aether-side infra spans (EF Core, Dapr diagnostic HTTP).
- The parenting hazard documented in `PipelineStepActivityHelper` (created-but-filtered spans
  re-rooting children) disappears because the spans are now always exported.
- Optional backlog note (not this work): an Aether-side configurable exemption list
  (`BusinessSpanExemptions`) if per-environment control is ever needed.

## 4. Target span tree

```
POST /transitions/{key}   (or TransitionJob / lane anchor)
├─ Lock.Acquire                          admission / pipeline status lock        (new)
├─ Transition.LoadContext                TransitionContextFactory                (new)
│  ├─ Cache.Get {componentKey}           CacheSet (existing; L1/L2 tags)
│  └─ Instance.Load                      GetActiveAsync                          (new)
├─ Transition.Validate                   schema + policy                         (new)
├─ Step.SetBusy … Step.FinalizeTransition   all executed steps (always on now)
│  ├─ Step.ResourceLock
│  │  └─ Script.Execute                  lock script                             (new)
│  ├─ Step.RunOnExecuteTasks             group span
│  │  └─ Task.Execute {taskKey}          per-task wrapper                        (new)
│  │     ├─ Task.PrepareInput            existing
│  │     │  ├─ Script.Compile            cold/hit tag                            (new)
│  │     │  └─ Script.Execute            input mapping                           (new)
│  │     ├─ Task.Invoke                  existing
│  │     └─ Task.ProcessOutput           existing → Script.* children
│  ├─ Step.ChangeState
│  │  └─ Instance.AppendData             data persist                            (new)
│  ├─ Step.RunOnEntryTasks / Step.RunOnExitTasks   tasks visible one by one
│  └─ Step.HandleSubFlow → Subflow.*     existing → Script.* mapping children
├─ Lock.Release                                                                  (new)
└─ PostCommit.Execute                    existing → job enqueue children
```

## 5. Work areas

| # | Area | Change |
|---|------|--------|
| 1 | **Config fix** | Add `BBT.Workflow.Tasks`, `BBT.Workflow.SubFlow`, `BBT.Workflow.BackgroundJobs` to `AdditionalSources` in all four hosts' appsettings (orchestration, execution, Inbox, Outbox — plus DbMigrator if applicable). Check vnext-helm-charts for the corresponding values; remind if missing. Independent, ship first. |
| 2 | **Step spans always-on** | `PipelineStepActivityHelper`: remove `IsVerbose` gate; rename to `Step.{Name}` (strip the `Step` suffix from class names for display); tags `vnext.step.order`, `vnext.step.outcome` (Continue/Stop/SkipTo/SkipToFinalize), `span.category=business`. Update the class's doc comment — its re-rooting rationale becomes obsolete. |
| 3 | **Script engine spans** | New `ScriptActivityHelper` (ActivitySource `BBT.Workflow.Scripting`, register in AdditionalSources). `Script.Compile` span around compile core incl. helper-set compilation (tags: `vnext.script.key`, `cache.hit`, compile kind); `Script.Execute` span around script invocation (tags: `vnext.script.key`, `vnext.script.usage` = inputMapping / outputMapping / rule / condition / lock / subflowMapping / view / event). Existing `ScriptCompileTelemetry` accumulator tags + `script.compile` event stay untouched. |
| 4 | **Per-task wrapper span** | `Task.Execute {taskKey}` wrapper in the task executor path so PrepareInput/Invoke/ProcessOutput nest under one task node; tags `vnext.task.key`, `vnext.task.type`, task order. OnEntry/OnExit/OnExecute step spans become the group parents. |
| 5 | **Lock spans** | `Lock.Acquire` / `Lock.Release` spans in `InstanceStatusLock` and the `TransitionAdmissionService.AcceptAsync` critical section; tags `vnext.lock.key`, `vnext.lock.acquired` (false on Busy/409 path), `vnext.lock.kind` (reserve/takeover/accept/release). Failed acquire is a span with `acquired=false`, not an exception span. |
| 6 | **Validation span** | `Transition.Validate` around schema validation + `TransitionExecutionPolicy`; tags: result, error code on failure. |
| 7 | **Context load spans** | `Transition.LoadContext` around `TransitionContextFactory` (child cache spans attach automatically); `Instance.Load` around `GetActiveAsync`. |
| 8 | **Component cache verification** | No new store-level span (CacheSet suffices; double-wrapping is noise). Verify: L2 read duration is its own child span (or measurable from the CacheSet span when `l1_hit=false`); tag names align with conventions; spans export in Business mode. Adjust only if verification fails. |
| 9 | **Instance data span** | `Instance.AppendData` around the append/persist path (v2 funnel); tags: data version, payload byte size. Never the payload itself. |
| 10 | **Constants & docs** | All new tag names into `TelemetryConstants`. Update `docs/` telemetry documentation (and `ai-docs` if touched); note the compile-span decision reversal where the old decision is recorded. |

## 6. Conventions

- Span names: `Area.Operation` (`Step.ChangeState`, `Script.Compile`, `Lock.Acquire`,
  `Task.Execute`), matching the existing `Task.PrepareInput` pattern. No `[` prefix anywhere.
- Tags centralized in `TelemetryConstants.TagNames`; new ones: `vnext.step.order`,
  `vnext.step.outcome`, `vnext.lock.key`, `vnext.lock.acquired`, `vnext.lock.kind`,
  `vnext.script.key`, `vnext.script.usage`, `vnext.data.version`, `vnext.data.size_bytes`.
- No instance data content, headers, or payloads on any span — identifiers and sizes only.
- Every new ActivitySource must be added to `AdditionalSources` in the same commit that
  introduces it (that is how source #1's gap happened).

## 7. Testing & verification

- **Unit:** follow the ActivityListener-based telemetry test pattern introduced in PR #917 —
  for each new span: correct parent, required tags present, no span leakage on the skip paths
  (e.g. self-target updateData profile, error-boundary profile).
- **Parenting regression is the top risk:** step spans now always exist, so every child (task,
  subflow start, job enqueue, HttpClient) re-parents from the transition span to its step span.
  FlatLane / hop-chain (`vnext.hop.predecessor`, lane anchor) invariants must be re-verified.
- **Integration verification (manual, no new scenario):** run an existing vnext-example flow
  against the local runtime (docker infra + 4 apps, `--launch-profile http`) and inspect the
  full tree in Jaeger end-to-end: manual transition, auto-chain, subflow start/resume,
  scheduled/timeout, error boundary.
- **Volume observation (optional):** reuse `script-race-lab` load script to observe span volume
  under load; no new load test.

## 8. Volume & rollback

- Estimated +25–40 spans per transition (steps ~15–20, +2–4 per task, 2 lock, 3–5
  context/validation). OTLP export is batched; risk assessed low.
- Rollback: single-commit revert restores the old naming and the Verbose gate; no config or
  schema migration involved.

## 9. Out of scope

- Aether changes (`BusinessSpanFilterProcessor` untouched; exemption seam is a backlog note).
- Sampling / tail-sampling policy, dashboards, alerts.
- gRPC client instrumentation (Dapr orphan spans — separate known issue).
- Metrics export (`AdditionalMeters` is empty — separate work).
