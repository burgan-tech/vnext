# Activation Episode Tracing — One Trace from Trigger to "Available" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This file is the English mirror of the planning document that drove the 2026-09-02 implementation; checked boxes reflect what landed in that working tree.

**Goal:** Make the time from a trigger (HTTP start/transition request, timer fire, event delivery, subflow resume, child start) until the instance is next observable at rest (Active, Completed/Canceled, Faulted, or a deliberate rest in Busy) readable as **one span in one trace** — the `Instance.Activation/{key}` span — and name the accept-path, start-path and fault-path gaps around it. Two trace-continuity defects found on the way (stale W3C headers copied onto cross-domain internal calls; the Dapr duplicate-`traceparent` repair installed in only one of four hosts) are fixed in the same change.

**Architecture:** A lane-borne *activation episode* (`WorkflowTraceLane.Episode`: start instant, trigger, first-hop transition key, partial flag) is seeded at every entry point, inherited across subflow handoffs, restarted for trigger-family tasks, and carried across every async boundary as three nullable fields beside the lane anchor. At the rest point the settlement records an `ActivationVerdict`; **after the UoW commit** the runner emits a synthetic, **backdated**, `Internal`-kind span parented to the lane anchor with the settling hop attached as an `ActivityLink`. Everything else is additive named spans on existing `ActivitySource`s, plus a histogram. No behavior change, no API change, no new dependency, no DB migration.

**Tech Stack:** .NET 10, `System.Diagnostics.Activity` (`ActivitySource.StartActivity(name, kind, parentContext, tags, links, startTime)` — present in the .NET 10 ref pack), `System.Diagnostics.Metrics`, xUnit + NSubstitute + Shouldly, OpenTelemetry export via Aether → otel-collector → Elastic APM (Kibana) + OpenObserve.

**Spec:** Embedded — see "Gap inventory" and "Decisions" below. Source: trace analysis of async start/transition traces on the local stack (2026-09-01/02) against `docs/runtime/trace-lanes.md` and `docs/runtime/trace-span-tree.md`.

## Gap inventory (the spec)

What the analysis found was **not** a parenting problem: on the async path the job spans already land in the right trace under the right parent (flat-lane model — `TransitionJob.Execute/{key}` hops, `PostCommit.*`, `SubFlow.Resume` are all siblings under the HTTP server span). The problem was **duration and visibility**.

| # | Gap | Evidence | Resolution |
|---|-----|----------|------------|
| G1 | **No span represents "request → Active".** The APM transaction (server span) closes at the 202; later hops start under an already-ended parent, so the trace's "duration" shows only the accept | `trace-lanes.md` "Known cosmetic effect", `trace-span-tree.md` check 8 | `Instance.Activation/{key}` (Tasks 3–7) |
| G2 | **The only "became Active" signal is `vnext.settle.status`**, stamped identically whether the CAS flipped, lost, or the guard skipped | `TransitionSettlement.cs` — tag set before the guard, CAS inside it | `vnext.settle.cas` + `instance.available` / `instance.available.committed` events (Task 5) |
| G3 | **The start path's instance-creation region is span-less**: existence probe, aggregate construction, start-transition validation, INSERT, `RequiresNew` commit, workflow-timeout scheduling — only the inner `Instance.AppendData` shows | `InstanceCommandAppService.PrepareInstanceAsync`, `ScheduleWorkflowTimeoutIfConfiguredAsync` | `Instance.Create`, `Instance.Timeout.Schedule` (Task 8) |
| G4 | **The async accept's queue is invisible**: intake (snapshot + definition resolve), the enqueue UoW and above all the Dapr scheduler **arm**. Aether's `BackgroundJob.Schedule*` spans are Verbose-gated, so in production `Business` mode the dead time between the 202 and the first job span has no name | `InstanceCommandAppService.TransitionAsync`, `AsyncTransitionStrategy` | `Transition.Intake`, `Transition.Enqueue`, `BackgroundJob.Arm` (Task 9) |
| G5 | **Defect:** cross-domain internal endpoint calls (`internal/subflow-forward`, `busy-release`, `related-data`) copy **stale `traceparent`/`tracestate`/`baggage`** from captured request headers. `HttpClient`'s `DiagnosticsHandler` is fill-if-absent, so the stale value wins and the callee parents to the wrong span. The correct guard already exists on the task-invoker path | `CurrentUserForwardHeadersHelper`, `RemoteHttpResponseHelper`; correct example `HttpTaskInvocation` | Task 1 |
| G6 | `DuplicateTolerantTraceContextPropagator` (repairs Dapr's duplicated `traceparent`) is installed only in the Execution host; Orchestration/Inbox/Outbox are unprotected and its counter meter is unregistered | `execution/.../Program.cs`; absent from the other three | Task 2 |
| G7 | Fault path (`MarkInstanceFaultedAsync`) is span-less; the `state.notify` job still uses the old nested parenting; a non-existent `BBT.Workflow.Instances.Events` source is registered in config; `correlation-and-tracing.md` § Diagnostic spans and the `CLAUDE.md` "Jaeger" wording are stale | `TransitionPipeline.cs`, `StateNotifyJobHandler.cs`, the four `appsettings.json` | Tasks 5c, 10, 11, 13 |

**Separate traces by design — not broken, kept that way:** timer/timeout/long-poll-ack jobs (`StartActivityAsChildWithLink`, so an hours-later fire does not resurrect an old trace), fact-event backup deliveries (`LinkedDelivery`), `Outbox.Process` (Aether-owned). The client's state-function poll is a separate HTTP request and therefore a separate trace by nature.

## Decisions

| # | Decision | Taken | Rationale |
|---|---|---|---|
| **D1** | Timer/timeout/ack jobs: stay a separate linked trace, or join the origin trace? | **Stay separate; each fire opens its own episode** (`trigger = scheduled \| timeout \| ack-timeout`, start = the Dapr callback server span) | No hours-long traces. For a timer the client's question is already "fire → Active". |
| **D2** | A parent entering a SubFlow: does its episode end at the handoff, or span the child's lifetime? | **Ends at the handoff (`busy.subflow`)**; the child **inherits** the parent's episode start through `EnterChildLane()` (child's `active` episode = parent request → leaf Active); the parent **resume** inherits the child's terminal-trigger start. Trigger-family tasks (`TriggerTaskExecutorBase`) **restart** the episode. | The state function shows the client the leaf's status; the two inherited spans are exactly the two things the client waits for. |
| **D3** | Episode span kind: `Internal` or `Consumer`? | **`ActivityKind.Internal`** | `Consumer` is counted as a transaction by apm-server: transaction list and latency distribution would inflate. `Internal` draws as an ordinary waterfall bar. Fleet p95 comes from the metric (Task 12). |
| **D4** | Defect + propagator fixes in the same PR? | **Same PR, first two tasks, separate commits** | The verification queries ("one trace per episode", "no orphan roots") are meaningless without them. |
| **D5** | Where does the episode start travel: lane state or `WorkflowExecutionContext.RequestedAt`? | **Lane-borne** (`WorkflowTraceLane` `AsyncLocal` state) | `AsyncLocal` flows through inline hops, the post-commit barrier and the terminal relay by itself; only the existing lane carriers gain fields. `RequestedAt` is written as `UtcNow` at 13 construction sites and means "this hop's setup instant" — untouched. |
| **D6** | When is the episode span emitted: at Settle or after commit? | **Verdict recorded at Settle, emitted after commit.** | Settle's flip is not durable until the UoW commits; the client sees Active only after the commit. |

## Design summary

**Concept — "activation episode":** a trigger → the instance's next **rest point**. Rest point = Active flip (CAS succeeded) | Completed/Canceled | Faulted | rest in Busy (open SubFlow correlation, Busy-subtype state, auto-gate not met = "parked"). Every episode is one trace (largely already true) **and** contains one span carrying the trigger→rest duration.

**Mechanism — the backdated synthetic span `Instance.Activation/{transitionKey}`:** emitted at the rest point with `startTime = episode start`; parent = lane anchor (sibling of the hops, directly under the transaction); the settling hop's span attached as an `ActivityLink`. Tags: `vnext.activation.outcome` (`active|completed|canceled|faulted|busy.subflow|busy.parked|busy.subtype`), `vnext.activation.trigger`, `vnext.activation.transition.key`, `vnext.activation.hops` (lane seq), `vnext.activation.duration_ms`, `vnext.activation.partial` (legacy carrier), `vnext.activation.clock_skew`, `vnext.settle.cas` (`flipped|n/a`), instance/flow/domain/transition identities, `vnext.state.to`, `vnext.layer=orchestration`, `span.category=business`.

**Why synthetic:** a .NET `Activity` span id is minted at `Start()`; no span can both be known at accept time (to be the real parent of the following hops) **and** be stopped at the rest point in another process. The lane anchor (server span) already parents the hops; the episode span adds the measurable envelope beside them. API verified: the `startTime` overload of `ActivitySource.StartActivity` is in the .NET 10 ref pack (fallback `Activity.SetStartTime`).

**Two repo facts shape the design:** (1) exactly one `TransitionSettlement.ApplyAsync` runs per hop — when there are post-commit jobs `RunChainAsync` returns before Settle and the post-commit path settles from a fresh reload (`PostCommitParentMutationService`); when an inline continuation follows the post-commit the runner starts a new stage and `SettleAsync` is never called. (2) The flip is not durable until commit (D6).

**Extra visibility:** `vnext.settle.cas = flipped|lost|skipped` on `Transition.Settle` and an `instance.available` `ActivityEvent` only on a real flip; named spans in the start-path and async-accept gaps; the defect and propagator fixes.

**Naming note:** `2026-08-30-trace-episode-separation.md` uses "episode" for *trace separation*. To avoid the clash the code/tag family is **`Instance.Activation` / `vnext.activation.*`**; "activation episode" in prose.

## Global Constraints

- **Additive only**: no existing span name, tag, source or payload field is renamed.
- New spans are Business category, always-on, `vnext.span.category=business`; ordinary spans use the implicit-parent overload. **The single explicit-parent exception is `Instance.Activation`** (it must parent to the lane anchor). Explicit parent ⇒ `Activity.Parent == null` ⇒ `Stop()` nulls `Activity.Current`; the helper **saves and restores `Activity.Current`** (pinned by test).
- **No new `ActivitySource`**: `Instance.Activation` and the pipeline gap spans emit on `BBT.Workflow.Pipeline` (`PipelineStepActivityHelper.ActivitySource`, public), `BackgroundJob.Arm` on `BBT.Workflow.BackgroundJobs`. Both are registered in all four hosts.
- Payload/event/body additions are nullable and default-null: an older build's message degrades to a "partial episode", deserialization never breaks. No DB migration (payloads live in the Dapr store / outbox blob).
- Test pattern: `[Collection(TracingDetailLevelCollection.Name)]`, `IDisposable` + `ActivityListener`, dispose the listener and set `Activity.Current = null` in `Dispose()`, **literal** source name in `ShouldListenTo` (never `Helper.ActivitySource.Name`), `Sample = AllDataAndRecorded`, collect in `ActivityStopped`, Shouldly. Template: `test/BBT.Workflow.Application.Tests/Telemetry/UnattributedRegionSpanTests.cs`.
- Logging only through `WorkflowLogs.cs` `[LoggerMessage]` extensions.
- **Aether is not modified**; two Aether observations (Verbose-gated scheduler span; auto-chain arming deferred to the UoW post-commit hook) are recorded as a suggestion for the user in the Task 9 note.
- Build: `dotnet build`. Tests with `--filter`; master carries ~191 unrelated pre-existing failures, judge by targeted runs.

---

### Task 1: Never copy stale W3C trace headers onto cross-domain requests (G5)

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` — `HeaderNames.W3CTraceContext = ["traceparent","tracestate","baggage"]` + `IsW3CTraceContextHeader(name)` (OrdinalIgnoreCase)
- Modify: `src/BBT.Workflow.Infrastructure/Remote/CurrentUserForwardHeadersHelper.cs` — both skip conditions gain `|| IsW3CTraceContextHeader(kv.Key)` **unconditionally** (so `RemoteInstanceQueryAppService` / `RemoteAuthorizeAppService`, which pass no `isRestrictedHeader` callback, are protected too)
- Modify: `src/BBT.Workflow.Infrastructure/Remote/RemoteHttpResponseHelper.cs` — `IsRestrictedHeader` includes the same predicate
- Test: `test/BBT.Workflow.Infrastructure.Tests/Remote/CurrentUserForwardHeadersHelperTraceHeaderTests.cs` (create)

**Why `HttpTaskInvocation.IsReservedTraceHeader` is not reused:** its list also holds `x-request-id`, `X-Correlation-Id`, `X-Workflow-Instance-Id`, which `MergeIntoRequest` legitimately forwards; and `Execution.Abstractions` deliberately does not reference `Domain`. Two narrow lists with cross-referencing comments.

- [x] **Step 1: Failing test** — `Theory(traceparent, TraceParent, tracestate, baggage)`: in forwardHeaders → absent from the request; in inputHeaders → absent and no throw; guard unconditional even without a restricted-header callback; `X-Custom` / `x-request-id` / `Authorization` still copied; `IsRestrictedHeader` true for the trio (any case), false for ordinary headers. Implemented as `MergeIntoRequest_TraceContextHeaderInForwardHeaders_IsNotCopied`, `MergeIntoRequest_TraceContextHeaderInInputHeaders_IsNotCopiedAndDoesNotThrow`, `MergeIntoRequest_TraceContextGuardIsUnconditional_EvenWhenNoRestrictedCallbackIsPassed`, `MergeIntoRequest_NonTraceHeadersInInputHeaders_AreStillCopied`, `IsRestrictedHeader_TraceContextHeaders_AreRestricted`, `IsRestrictedHeader_OrdinaryHeaders_AreNotRestricted`.
- [x] **Step 2: Implement** the constant, the predicate and the two guards.
- [x] **Step 3: Run** `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~CurrentUserForwardHeadersHelperTraceHeaderTests"` → PASS.
- [ ] **Step 4: Commit** `fix(tracing): never forward captured W3C trace headers on cross-domain internal calls`

---

### Task 2: Install the propagator in the other three hosts, register the meter (G6)

**Files:**
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Program.cs`, `workers/BBT.Workflow.Workers.Inbox/Program.cs`, `workers/BBT.Workflow.Workers.Outbox/Program.cs` — `DistributedContextPropagator.Current = new DuplicateTolerantTraceContextPropagator(DistributedContextPropagator.Current);` **above** `WebApplication.CreateBuilder`, with a comment pointing at the Execution host's rationale and `docs/runtime/dapr-invocation-transport.md`
- Modify: `AdditionalMeters` → `["BBT.Workflow.Telemetry"]` in orchestration, inbox and outbox `appsettings.json` (the workers have metrics export off; forward-looking)

- [x] **Step 1: Install** in all three `Program.cs` files (all three reference `BBT.Workflow.HttpApi.Shared`).
- [x] **Step 2: Register** the meter in the three `AdditionalMeters` arrays.
- [x] **Step 3: No unit test** (the propagator has its own); live verification in the Verification section.
- [ ] **Step 4: Commit** `fix(tracing): install DuplicateTolerantTraceContextPropagator in all hosts; register BBT.Workflow.Telemetry meter`

---

### Task 3: The lane carries the episode (D5)

**Files:**
- Create: `src/BBT.Workflow.Domain/Logging/ActivationEpisode.cs` — `public sealed record ActivationEpisode(DateTimeOffset StartedAt, string Trigger, string? TransitionKey, bool Partial)` + `StartingAt(Activity?, trigger, key)` + `FromCarrier(startedAt, trigger, key)` (null when no start; a missing trigger with a present start defaults to `http`)
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowTraceLane.cs` — `LaneScopeState(Anchor, ParentAnchor, Seq, Episode)`; `Episode` accessor; `Use(..., episode)` preserve-on-null; `Reset(..., episode)` set-exactly/clear; `UseCurrentActivity(trigger = http)` seeds the episode from `Activity.Current.StartTimeUtc` (else `UtcNow`); `EnterChildLane(restartTrigger = null)` inherits on null, restarts from `Activity.Current` otherwise; `UseEpisode(trigger, key)` keeps anchor/parent/seq and the start, replaces the trigger **only while it is still `http`**, refreshes the key when supplied, seeds a fresh episode starting now when none is ambient
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` — `ActivationTriggers` (`http, start, manual, event, retry, ack, scheduled, timeout, ack-timeout, trigger, job`), `ActivationOutcomes` (`active, completed, canceled, faulted, busy.subflow, busy.parked, busy.subtype`), `TagNames.SettleCas`, `ActivationOutcome`, `ActivationTrigger`, `ActivationHops`, `ActivationDurationMs`, `ActivationPartial`, `ActivationClockSkew`, `ActivationTransitionKey`, `ActivationEmitted`
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/WorkflowTraceLaneEpisodeTests.cs` (create; model `WorkflowTraceLaneTests.cs`)

- [x] **Step 1: Failing tests** — implemented: `UseCurrentActivity_seeds_the_episode_from_the_ambient_span_start`, `Use_with_a_null_episode_preserves_the_enclosing_one`, `Reset_with_a_null_episode_clears_it`, `Reset_with_an_episode_installs_exactly_that_episode`, `EnterChildLane_inherits_the_episode_by_default`, `EnterChildLane_with_a_trigger_restarts_the_episode_at_the_handing_off_span`, `UseEpisode_classifies_an_http_seeded_episode_without_moving_its_start`, `UseEpisode_keeps_an_already_classified_trigger_and_refreshes_only_the_key`, `UseEpisode_with_a_null_key_keeps_the_existing_key`, `UseEpisode_without_an_ambient_episode_seeds_one_starting_now`, `Episode_flows_across_await_and_into_Task_Run`, `FromCarrier_yields_null_without_a_start_and_defaults_a_missing_trigger`.
- [x] **Step 2: Implement** the record, the lane changes and the constants.
- [x] **Step 3: Run** `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~WorkflowTraceLaneEpisodeTests|FullyQualifiedName~WorkflowTraceLaneTests"` → PASS.
- [ ] **Step 4: Commit** `feat(tracing): WorkflowTraceLane carries the activation episode`

---

### Task 4: `ActivationActivity` helper (the backdated synthetic span)

**Files:**
- Create: `src/BBT.Workflow.Application/Telemetry/ActivationActivity.cs` (beside `FlatLaneActivity.cs`)
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/ActivationActivityTests.cs` (create; literal `"BBT.Workflow.Pipeline"`)

**Interfaces:**
- `Emit(ActivitySource source, string outcome, Guid instanceId, string domain, string flow, string? lastTransitionKey, string? stateTo, bool casFlipped = false)` → the stopped `Activity?` (null when nobody listens; `Activity.Current` untouched in that case).
- `Emit(TransitionExecutionContext context, ActivationVerdict verdict)` shortcut (the plan sketched `Emit(ctx, outcome, casFlipped)`; the verdict record is the cleaner carrier).
- Logic: `episode = WorkflowTraceLane.Episode`; `startedAt = episode?.StartedAt ?? ambient.StartTimeUtc ?? now` (`partial = episode is null || episode.Partial`); `startedAt > now` ⇒ clamp to `now` + `clock_skew` tag; parent = the lane anchor when it parses **and** shares the ambient trace id, else the ambient span; `links = [ambient.Context]`; name `Instance.Activation/{episode.TransitionKey ?? lastTransitionKey ?? "resume"}`; `Internal`; write the tags; record the histogram unless partial/clock-skewed; `SetEndTime(now)`; `Stop()`; **`finally { Activity.Current = ambient; }`**.

- [x] **Step 1: Failing tests** — implemented: `Emit_backdates_the_start_to_the_episode_and_parents_to_the_lane_anchor` (`StartTimeUtc == anchor.StartTimeUtc ±1 ms`, `ParentSpanId == anchor.SpanId`, link to the settling span), `Emit_restores_Activity_Current`, `Emit_without_an_episode_covers_only_the_hop_and_is_tagged_partial`, `Emit_with_a_future_start_clamps_to_zero_and_tags_clock_skew`, `Emit_with_an_anchor_from_another_trace_falls_back_to_the_ambient_parent`, `Emit_names_the_span_after_the_episode_key_and_falls_back_to_the_settling_key`, `Emit_returns_null_and_leaves_Activity_Current_alone_when_nobody_listens`, `Emit_records_a_non_active_outcome_without_a_cas_flip`.
- [x] **Step 2: Implement** the helper.
- [x] **Step 3: Run** `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ActivationActivityTests"` → PASS.
- [ ] **Step 4: Commit** `feat(tracing): ActivationActivity emits the backdated Instance.Activation span`

---

### Task 5: Settle verdict, CAS tag, `instance.available` event, post-commit emit (G1, G2, D6)

**Files:**
- Create: `src/BBT.Workflow.Domain/Execution/Transitions/Context/ActivationVerdict.cs` — `record ActivationVerdict(string Outcome, bool CasFlipped, string? StateTo)`
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/PipelineDirectives.cs` — `ActivationVerdict? Activation` + `RecordActivation`, `bool ContinuationEnqueued` + `MarkContinuationEnqueued`; `ToContinuations()` passes the flag
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/ContinuationSet.cs` — `bool ContinuationEnqueued = false`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Continuations/EnqueueContinuationStrategy.cs` — `current.Directives.MarkContinuationEnqueued()` after the successful enqueue
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionSettlement.cs` — `ApplyAsync(..., bool chainSettled, IInstanceStatusLock? statusLock = null)`; guard into `guardPassed`; `flipped = TryReleaseBusyAsync(...)`; `vnext.settle.cas = guardPassed ? (flipped ? "flipped" : "lost") : "skipped"`; on `flipped` → `activity.AddEvent(new ActivityEvent("instance.available", tags: instanceId, stateTo))`; `ResolveVerdict` (private static) → `Directives.RecordActivation`; `vnext.activation.emitted` tag
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs` — Settle call passes `chainSettled: !hadNextTransition`; `MarkInstanceFaultedAsync` body wrapped in an `Instance.Fault` span (`error.code`, Error status) and emits `faulted` after `faultUow.CommitAsync` regardless of `OwnsStatus`
- Modify: `src/BBT.Workflow.Application/Execution/PostCommit/PostCommitParentMutationService.cs` — mutation delegate returns `Task<ActivationVerdict?>`; `SettleAsync` passes `chainSettled: !continuations.ContinuationEnqueued && instance.IsBusy` and returns `Directives.Activation`; `FaultAsync` returns `faulted` (null on the early `IsCompleted` return); `MutateFreshAsync` emits after `uow.CommitAsync`
- Modify: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs` — right after the `Uow.Commit` block, before `terminalRelay.RelayAsync`: `if (Directives.Activation is { } v) { ActivationActivity.Emit(ctx, v); if (v.CasFlipped) Activity.Current?.AddEvent(new("instance.available.committed")); }`
- Modify: `src/BBT.Workflow.Application/BackgroundJobs/Recovery/JobTimeoutRecoveryService.cs` — emit `faulted` after the commit with the payload's identities (`TransitionJobHandler`'s `finally` runs inside its `using var lane`, so the episode is still ambient)
- Tests: `test/BBT.Workflow.Application.Tests/Execution/PostCommit/PostCommitParentMutationServiceActivationTests.cs` (create)

**`ResolveVerdict` rules:** `!chainSettled || !OwnsStatus` → null; `guardPassed && flipped` → `active` (CasFlipped); `guardPassed && !flipped` → **null** (somebody else settled and emits); `Faulted` → `faulted`; `IsCompleted` → `canceled` when cancel transition or `Cancelled`-subtype target, else `completed`; not Busy → null (already Active); Busy + `Target.SubType == Busy` → `busy.subtype`; Busy + open SubFlow correlation → `busy.subflow`; Busy otherwise → `busy.parked`.

**Audited paths:** TransitionPerJob (intermediate hop `hadNextTransition=true` → no verdict; last hop emits), inline chain (Settle only at the end), sync (server span open; episode a sibling of the steps, start == server span start), retry (`InstanceRetryAppService`), long-poll ack (`ClearBusyOnResumeStep` → Active → `active`), cancel/exit (`HandleFinishStep` → `canceled`/`completed`), updateData non-owner (`OwnsStatus=false` → no emit; the chained hop after a handoff reserve is the owner, episode start = the updateData request), error-boundary (same pipeline), subflow forward (parent non-owner → no emit; the leaf emits).

- [x] **Step 1: Failing tests** — implemented in `PostCommitParentMutationServiceActivationTests`: `SettleAsync_FreshBusyParentResolvesToActive_EmitsActivationAfterCommit`, `SettleAsync_FreshParentNoLongerBusy_EmitsNothing`, `SettleAsync_ContinuationEnqueued_KeepsTheEpisodeOpen`, `SettleAsync_HandoffToSubflow_ClosesTheEpisodeAsBusySubflow`, `FaultAsync_FreshBusyParent_EmitsFaultedAfterCommit`, `FaultAsync_ParentAlreadyTerminal_EmitsNothing`.
- [ ] **Planned, not yet written:** `Execution/Transitions/Pipeline/TransitionSettlementVerdictTests.cs` (NSubstitute `TryReleaseBusyAsync` true/false; the full outcome matrix incl. lost → `cas=lost` + no verdict, non-owner, `chainSettled:false`, already-Active), `TransitionRunnerPostCommitTests.RunAsync_EmitsActivationAfterCommit_NotBefore`, `Telemetry/ActivationEmissionSyncPathTests.cs` (full sync pipeline: exactly one `Instance.Activation/*`, `Transition.Settle` count unchanged).
- [x] **Step 2: Implement** 5a (domain), 5b (settlement), 5c (post-commit emission at the four sites).
- [x] **Step 3: Run** `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~PostCommitParentMutationService|FullyQualifiedName~TransitionRunnerPostCommitTests|FullyQualifiedName~TransitionPipelineTests"` → PASS.
- [ ] **Step 4: Commit** `feat(tracing): settlement verdict, vnext.settle.cas, instance.available events, post-commit Instance.Activation emit`

---

### Task 6: Seed the episode at every entry point

| Entry | File | Change |
|---|---|---|
| HTTP request | `ParentInstanceIdEnrichmentMiddleware` | call unchanged (`UseCurrentActivity()`); now seeds `http` from the server span's start |
| Start | `InstanceCommandAppService.StartAsync` (after `LoadWorkflowAsync` succeeds) | `UseEpisode(Start, workflow.StartTransition.Key)` |
| Manual transition | `InstanceCommandAppService.TransitionAsync` | `UseEpisode(Manual, transitionKey)` — loses to an earlier `event` by the classify-once rule |
| Event-triggered | `EventAppService.TransitionAsync` | `UseEpisode(Event, input.TransitionKey)` before re-entering the generic entry point |
| Retry | `InstanceRetryAppService` | `UseEpisode(Retry, data.Transition.TransitionId)` |
| Long-poll ack (HTTP) | `LongPollAckResumeService.ResumeAsync` | `UseEpisode(Ack, null)` — the plan gated this on `Episode?.Trigger == Http`; the classify-once rule in `UseEpisode` makes the gate redundant (an `ack-timeout` job that drives the same resume keeps its trigger) |
| Timer job | `TransitionTimerJobHandler` | `UseEpisode(Scheduled, args.TransitionKey)` — anchor stays the callback server span (D1) |
| Timeout job | `FlowTimeoutJobHandler` | `UseEpisode(Timeout, WellKnownTransitionKeys.Timeout)` |
| Ack-timeout job | `LongPollAckTimeoutJobHandler` | `UseEpisode(AckTimeout, null)` |
| Transition job | `TransitionJobHandler` | `Reset(args.TraceRoot, args.ParentTraceRoot, args.LaneSeq, args.ToActivationEpisode())` |
| Legacy payload | `TransitionJobHandler` | `EpisodeStartedAt` null ⇒ `Use(null, episode: ActivationEpisode.StartingAt(activity, Job, key) with { Partial = true })` |
| Subflow handoff | `StartSubflowJobHandler`, `ForwardToSubflowJobHandler` | `EnterChildLane()` unchanged → inherits (D2) |
| Trigger-family task | `TriggerTaskExecutorBase` | `EnterChildLane(Trigger)` → restart (D2) |
| Inbox handler | `workers/.../Inbox/Tracing/EventTraceScope.cs` | `Reset(laneAware.TraceRoot, laneAware.ParentTraceRoot, episode: FromCarrier(...))` |
| Relay endpoints | `InstanceController` `/complete`, `/sub/fault`, `/sub/cancel`, `internal/subflow-forward` | `Reset(..., episode: FromCarrier(body fields))` |

- [x] **Step 1: Failing tests** — implemented in `TransitionJobHandlerTests`: `HandleAsync_WithPayloadEpisode_RestoresItForTheDurationOfTheJob`, `HandleAsync_LegacyPayloadWithoutEpisode_SeedsAPartialEpisode`.
- [ ] **Planned, not yet written:** one test per timer handler asserting the trigger seen inside the mocked execution service.
- [x] **Step 2: Implement** every row above.
- [x] **Step 3: Run** `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionJobHandlerTests"` → PASS.
- [ ] **Step 4: Commit** `feat(tracing): seed the activation episode at every entry point`

---

### Task 7: Carry the episode across every async boundary

Three nullable fields — `DateTimeOffset? EpisodeStartedAt`, `string? EpisodeTrigger`, `string? EpisodeTransitionKey` — everywhere `TraceRoot` is already handled:

| Carrier | Definition | Filled from the lane | Restored / forwarded |
|---|---|---|---|
| `TransitionJobPayload` (`ITraceableJobPayload` default-null members) | after `LaneSeq` | `AsyncTransitionStrategy.BuildDirectPayload`, `EnqueueContinuationStrategy` | `InstanceController` `/enqueue` relay copies them from the continuation event |
| `TransitionContinuationRequested` | after `LaneSeq` | `AsyncTransitionStrategy.BuildOutboxEvent`, `EnqueueContinuationStrategy` | — |
| `ILaneAwareDistributedEvent` | three settable members; implemented by `InstanceSubCompletedEvent`, `InstanceSubFaultedEvent`, `InstanceSubCanceledEvent`, `TransitionContinuationRequested` | `TraceStampingDistributedEventBus` — fills when `EpisodeStartedAt is null` (never overwrites) | `SubflowTerminalRelay` mappings; Inbox `InstanceSub*EventHandler` mappings |
| `FlowCompletedInput` / `SubFlowFaultedInput` / `SubItemCanceledInput` | after `ParentTraceRoot` | relay / inbox mappings | copied back onto the republished event in `SubflowCompletionService`, `SubflowFaultService`, `SubflowCancellationService` (terminal revert) |
| `SubflowForwardInput` | after `ParentTraceRoot` | `RemoteInstanceCommandAppService` | `InstanceController.SubflowForwardAsync` → `Reset` |

`ActivationEpisodeCarrierExtensions.ToActivationEpisode(this ITraceableJobPayload | ILaneAwareDistributedEvent)` (`Application/Telemetry/`) keeps the consuming sites one-liners.

**Not carried (known boundary, documented):** the cross-domain **child start** body (`CreateSubInstanceDto`, `InstanceController.StartSubAsync`) — a cross-domain child's episode starts at its own `sub/instances/start` server span. Optional follow-up: add the three fields there too (a timestamp is not an anchor; the `trace-lanes.md` header-injection concern does not apply).

- [x] **Step 1: Failing tests** — implemented in `TraceStampingDistributedEventBusTests`: `Publish_LaneAwareEventWithoutEpisode_StampsTheAmbientActivationEpisode`, `Publish_LaneAwareEventWithPresetEpisode_DoesNotOverwriteIt`.
- [ ] **Planned, not yet written:** `AsyncTransitionStrategyTests.EnqueueAsync_PayloadAndOutboxEvent_CarryEpisode`; the `EnqueueContinuationStrategy` counterpart.
- [x] **Step 2: Implement** every row above.
- [x] **Step 3: Run** `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~TraceStampingDistributedEventBusTests"` → PASS.
- [ ] **Step 4: Commit** `feat(tracing): carry the activation episode in every lane carrier`

---

### Task 8: Start-path gap spans (G3)

**Files:**
- Modify: `src/BBT.Workflow.Application/Instances/InstanceCommandAppService.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` — `TagNames.InstanceDataAppended`

- [x] `PrepareInstanceAsync` → `Instance.Create` (`PipelineStepActivityHelper.StartOperationActivity`), tags `vnext.flow.key`, `vnext.flow.version`, `vnext.instance.data.appended`, `vnext.instance.id` on success; Error status + message on failure.
- [x] `ScheduleWorkflowTimeoutIfConfiguredAsync` → `Instance.Timeout.Schedule` after the `effectiveTimeout == null` return, tags `vnext.instance.id`, `vnext.job.name`; Error status in the catch (exception still swallowed).
- [ ] `CheckExistingInstanceAsync` → optional `Instance.Probe` — **not implemented** (low priority).
- [ ] **Planned, not yet written:** `Telemetry/StartPathSpanTests.cs` (with the `InstanceCommandAppServiceStartProbeTests` fixture): create-span-with-data-tag, with-timeout-emits-schedule-span, without-timeout-no-span.
- [ ] **Commit** `feat(tracing): Instance.Create and Instance.Timeout.Schedule spans on the start path`

---

### Task 9: Async-accept gap spans (G4) — NO `Transition.Accept` envelope

Decision: the server span *is* the accept; an envelope would duplicate the transaction (same reasoning as hop 1 having no group span). Name the phases instead:

- [x] `InstanceCommandAppService.TransitionAsync` → `Transition.Intake` around the snapshot + `LoadWorkflowAsync` + Busy fast-fail; tags `vnext.transition.key`, `vnext.instance.busy` (`TagNames.InstanceBusy`).
- [x] `AsyncTransitionStrategy.EnqueueAndSaveJobAsync` → `Transition.Enqueue` (RequiresNew UoW: job row + gateway + commit); tags `vnext.job.name`, `vnext.enqueue.path` (`Direct|Outbox`, `TagNames.EnqueuePath`).
- [x] `AsyncTransitionStrategy` arm → `BackgroundJobActivityHelper.StartArmActivity(jobName)` (`BackgroundJob.Arm`, Internal, Business, `vnext.job.name`) around `armHandle.ArmAsync`; Error status on exception, rethrown.
- [ ] **Planned, not yet written:** `AsyncTransitionStrategyTests.ExecuteAsync_EmitsTransitionEnqueueAndBackgroundJobArmSpans`, `ExecuteAsync_ArmFailure_MarksArmSpanError`; `InstanceCommandAppServiceBusyFastFailTests.TransitionAsync_EmitsTransitionIntakeSpan_EvenOnBusyReject`.
- [ ] **Commit** `feat(tracing): Transition.Intake, Transition.Enqueue and BackgroundJob.Arm name the async accept`

**Aether suggestion (not done here, relayed to the user):** the auto-chain arm (`EnqueueContinuationStrategy`) runs in Aether's UoW `OnCompleted` hook and cannot be spanned from vNext. Either Aether promotes `BackgroundJob.Schedule*` to Business or exposes a hook. Until then the gap between hop N's `Uow.Commit` end and hop N+1's job span = Dapr scheduler latency + that arm. Also: if Aether 1.0.39 turns out to export `BackgroundJob.Schedule*` in Business already, `BackgroundJob.Arm` would double it — check one live trace before merging.

---

### Task 10: Config hygiene

- [x] Remove `"BBT.Workflow.Instances.Events"` from `AdditionalSources` in all four hosts (orchestration, execution, inbox, outbox `appsettings.json`).
- [ ] Check `vnext-helm-charts` values for a templated counterpart (see `trace-span-tree.md` § AdditionalSources registration rule).
- [ ] **Commit** `chore(telemetry): drop the removed BBT.Workflow.Instances.Events source from AdditionalSources`

---

### Task 11: `state.notify` becomes lane-aware (optional, low priority) — implemented

- [x] `StateNotifyPayload` gains `TraceRoot` / `ParentTraceRoot`; `StateNotificationScheduler` fills them from the lane; `StateNotifyJobHandler` → `WorkflowTraceLane.Reset(...)` + `StartFlatLaneActivity("StateNotify.Execute", args)`. An anchor-less payload degrades to the previous continue-the-predecessor parenting.
- [ ] **Planned, not yet written:** `StateNotifyJobHandlerLaneTests.cs`.
- [ ] **Commit** `feat(tracing): state.notify job is a flat-lane item`

---

### Task 12: Optional metric `workflow_activation_duration_ms` — implemented

- [x] Create `src/BBT.Workflow.Application/Telemetry/WorkflowMetrics.cs`: `Meter("BBT.Workflow.Telemetry")` (the same meter name exists in `DuplicateTolerantTraceContextPropagator`; several `Meter` instances may share a name and one `AddMeter` subscribes to all) + `Histogram<double> ActivationDurationMs` (`unit: ms`). `ActivationActivity.Emit` records with tags `vnext.domain`, `vnext.flow.key`, `vnext.activation.transition.key`, `vnext.activation.outcome`, `vnext.activation.trigger`; **skipped when partial or clock-skewed**. Registered by Task 2's `AdditionalMeters` change.
- [ ] **Commit** `feat(telemetry): workflow_activation_duration_ms histogram`

---

### Task 13: Documentation

- [x] `docs/runtime/trace-span-tree.md`: span table rows for `Instance.Activation/{key}` (D3 rationale; backdated start, lane-anchor parent, settling span linked, post-commit emit, one per episode, `partial`), `Instance.Create`, `Instance.Timeout.Schedule`, `Instance.Fault`, `Transition.Intake`, `Transition.Enqueue`, `BackgroundJob.Arm`, `StateNotify.Execute`; new tags and the `instance.available` / `instance.available.committed` events and the `vnext.settle.cas` value set; new spans in the target tree; Verification check 8 amended; `Instances.Events` marked removed from config; "Activation episode: why the span is synthetic" subsection.
- [x] `docs/runtime/trace-lanes.md`: "Episode" column on the seeding table; new "Activation episode" section (definition, rest points, inherit/restart, carriers, emission rules, partial/clock_skew); "Known cosmetic effect" updated; `state.notify` and timestamp-is-not-an-anchor safety bullets.
- [x] `docs/monitoring/correlation-and-tracing.md`: § Diagnostic spans rewritten (always-on `Step.{Name}`, no `transition/{key}`); broken-trace checklist item 5 fixed, item 7 (missing/partial episode) added; the reserved-header rule for cross-domain internal calls; `state.notify` row → flat lane; propagator installed in all four hosts.
- [x] `CLAUDE.md`, `AGENTS.md`: "Jaeger in Docker" → "OpenTelemetry via Aether → otel-collector → Elastic APM (Kibana) + OpenObserve".
- [x] `.claude/rules/vnext-workflow-developer.md`: "Activation episode" bullets under "Sync vs Async".
- [x] This file.

---

## Verification

**Unit (after each task):**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~CurrentUserForwardHeadersHelperTraceHeaderTests|FullyQualifiedName~TraceStampingDistributedEventBusTests"
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~WorkflowTraceLaneEpisodeTests|FullyQualifiedName~ActivationActivityTests|FullyQualifiedName~PostCommitParentMutationServiceActivationTests|FullyQualifiedName~PostCommitParentMutationServiceTests|FullyQualifiedName~TransitionRunnerPostCommitTests|FullyQualifiedName~TransitionJobHandlerTests|FullyQualifiedName~AsyncTransitionStrategyTests|FullyQualifiedName~InstanceCommandAppServiceBusyFastFailTests"
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~Telemetry"   # FlatLaneActivityTests / WorkflowTraceLaneTests / UnattributedRegionSpanTests must not regress
```

**Local stack (per `CLAUDE.local.md`):** if the infrastructure is not already up, `cd etc/docker && ./run-docker.sh`; no migration; the four apps each with **`--launch-profile http`** (orchestration 4201, execution 4202, inbox, outbox). Drive: an async `start` on a flow with an auto-chain; an async manual transition; a sync transition; parent → subflow → resume (the vnext-example Subflow subset); a transition with a timer; a cancel. This does **not** count as an integration-test requirement (no behavior changes, telemetry is added); no new vnext-example scenario is written — trace verification is done with the queries below.

**OpenObserve** (`start_time` / `end_time` are **nanoseconds**, `duration` is **microseconds** — see `trace-span-tree.md` methodology note):

1. One trace per episode: count `Instance.Activation/%` spans grouped by `trace_id` → exactly 1 in single-instance flows; exactly 2 in parent + child (`busy.subflow` + `active`) or 3 (+ the resume's `active`).
2. Episode correctness: in a sample trace, `Instance.Activation/*`.`start_time` == root transaction `start_time` (±1 ms), `end_time` ≥ the last `Uow.Commit`'s end, `parent_id` == the root transaction's span id.
3. Exactly one `instance.available` per `active` episode: count of `Transition.Settle` with `vnext_settle_cas='flipped'` == count of `active` episodes; that span's `events` contains `instance.available`.
4. No orphan roots: `parent_id=''` AND span_name ∈ {`Transition.Settle`, `Instance.Activation/*`, `PostCommit.StartSubflowJob`, `SubFlow.Resume/*`} → 0.
5. Sync path unchanged: span set = old set + exactly 1 `Instance.Activation/*`; server-span duration equal within noise.

**Kibana Dev Tools** (`.ds-traces-apm*,traces-apm*`; string tags under `labels.*`, numeric under `numeric_labels.*`): on `span.name` prefix `Instance.Activation/`, a terms aggregation on `labels.vnext_activation_outcome` and p50/p95 of `numeric_labels.vnext_activation_duration_ms`. Expected: `active`, `completed`, `busy.subflow` present; **no** `vnext_activation_partial` in new-build traffic; in the waterfall the `Instance.Activation/{key}` bar starts at the transaction's start and ends after the last job span, and the document has `span.type` (it is a span, not a transaction) → D3 confirmed. Kibana's axis extends to the latest-ending span (`getWaterfallDuration` = max(offset + duration)); confirm visually on the first live trace anyway.

**Propagator / header fixes:** in a cross-domain forward trace the child's `TransitionJob.Execute/*` sits under `PostCommit.ForwardToSubflowJob` with `vnext.trace.lane=true` and no `vnext.trace.lane.mismatch`; with `ExecutionApi:Transport=grpc`, `workflow_traceparent_extractions_total{outcome="repaired"}` > 0 on the orchestration side and no execution transaction roots its own trace.

## Risks

- **Explicit-parent `Stop()` nulls `Activity.Current`** — mitigated by the save/restore in `ActivationActivity`, pinned by `Emit_restores_Activity_Current`. (`PostCommitExecutor` has the same shape but is safe because the null leaks only into that async method's own frame.)
- **Clock skew across replicas** can make `startedAt > now` → clamped to zero duration and tagged `vnext.activation.clock_skew`; alert on the tag, not the number. Skewed episodes are not recorded in the histogram.
- **Rolling deploy:** old producer → new consumer yields a `partial` episode (hop-scoped, tagged); new producer → old consumer ignores the extra JSON fields.
- **Post-commit + enqueued continuation:** guarded by `ContinuationSet.ContinuationEnqueued`; if a future post-commit job type neither hands off to a child nor enqueues, `busy.parked` is the honest fallback.
- **Double emit with a sync child resume:** guarded by `instance.IsBusy` in the post-commit `chainSettled` and the "CAS lost ⇒ no verdict" rule.
- **Waterfall rendering:** a child ending after its parent is drawn today already (documented cosmetic effect); the episode makes it readable. Even if Kibana clipped the axis to the root transaction, the span's label shows the full duration.
- **The Aether Verbose-gating inference rests on the 1.0.37 documentation** (1.0.39 not in the local cache). If 1.0.39 exports `BackgroundJob.Schedule*` in Business, `BackgroundJob.Arm` doubles it — check one trace before merging Task 9.

## Out of scope (deliberate)

- Joining timer/timeout/ack jobs to the origin trace (D1).
- Changes to Aether (suggestion only).
- Attaching the client's state-function poll to the business trace (a separate HTTP request; the `X-Trace-Id` response header already exists).
- Carrying the episode in the cross-domain child-start body (Task 7 note; optional follow-up).
- A new vnext-example integration scenario (telemetry, not behavior, changes; with user approval if needed).

## Self-review notes

- **Spec coverage:** G5 → Task 1, G6 → Task 2, G1/G2 → Tasks 3–7, G3 → Task 8, G4 → Task 9, G7 → Tasks 5c/10/11/13. Decisions honored: D1 (own episode per timer fire), D2 (`EnterChildLane` inherit / `EnterChildLane(Trigger)` restart), D3 (`ActivityKind.Internal`), D5 (lane-borne), D6 (verdict at Settle, emit after commit).
- **Deviations from the original plan, recorded here:** the long-poll ack seed is unconditional (the classify-once rule in `UseEpisode` subsumes the planned `Trigger == Http` gate); `ActivationActivity.Emit`'s shortcut takes the `ActivationVerdict` rather than `(outcome, casFlipped)`; the `Instance.Activation` span additionally carries `vnext.settle.cas` (`flipped|n/a`), `vnext.layer` and `workflow.instance.id`; `ActivationEpisode.FromCarrier` defaults a missing trigger to `http` when a start is present; the post-commit tests live in a new `PostCommitParentMutationServiceActivationTests` file rather than as additions to `PostCommitParentMutationServiceTests`; `Instance.Probe` was not added; `FlowTimeoutJobHandler` names the episode after `WellKnownTransitionKeys.Timeout`.
- **Tests written (all green, run twice to rule out listener cross-talk):** `WorkflowTraceLaneEpisodeTests` (12), `ActivationActivityTests` (8), `TransitionSettlementVerdictTests` (11 — the full verdict matrix incl. `canceled` via the cancel transition and `busy.subflow` via an open correlation), `PostCommitParentMutationServiceActivationTests` (6 — emit-after-commit call order, not-Busy ⇒ nothing, enqueued ⇒ open, handoff ⇒ `busy.subflow`, fault ⇒ `faulted`), `TransitionJobHandlerTests.HandleAsync_WithPayloadEpisode_RestoresItForTheDurationOfTheJob` + `HandleAsync_LegacyPayloadWithoutEpisode_SeedsAPartialEpisode`, `AsyncTransitionStrategyTests.ExecuteAsync_ShouldCarryTheActivationEpisodeOntoPayloadAndOutboxEvent` + `ExecuteAsync_ShouldSpanTheEnqueueAndTheArm`, `InstanceCommandAppServiceBusyFastFailTests.TransitionAsync_BusySnapshot_EmitsTransitionIntakeSpan`, `TraceStampingDistributedEventBusTests.Publish_LaneAwareEventWithoutEpisode_StampsTheAmbientActivationEpisode` + `…PresetEpisode_DoesNotOverwriteIt`, `CurrentUserForwardHeadersHelperTraceHeaderTests` (16). **Listener rule learned here:** an `ActivityListener` is process-wide and xUnit runs classes in parallel, so every span-asserting test starts its own root `Activity` and filters collected spans by that `TraceId` — without it `Transition.Settle` / `Instance.Activation` spans from a neighbouring class leak in and `Single(...)` fails intermittently.
- **Test debt (planned, not written):** `TransitionRunnerPostCommitTests.RunAsync_EmitsActivationAfterCommit_NotBefore` (runner harness), `ActivationEmissionSyncPathTests` (full pipeline fixture), `StartPathSpanTests` for `Instance.Create` / `Instance.Timeout.Schedule` (the start-probe fixture cannot drive `PrepareInstanceAsync` to a commit), `StateNotifyJobHandlerLaneTests`, the timer-handler trigger tests, the `EnqueueContinuationStrategy` episode test. Live verification (above) covers their behavior until they exist.
- **Baseline check:** the broader `Execution|BackgroundJobs|SubFlow|Events|Instances` filter shows 10 failures that fail identically on a clean HEAD worktree (`JobTimeoutRecoveryServiceTests` ×2 — the test mocks `BeginAsync` while the service calls `Begin`; `SubflowTerminalRevertRearmTests` ×2; `InstanceQueryAppServiceVersionTests` ×2; `ViewGetContentAsTypedTests` ×4) — pre-existing, not introduced here. HEAD itself did not compile (`PythonTaskExecutor` still referenced the deleted `IWorkflowMetrics`); the two-line fix is part of this change.
