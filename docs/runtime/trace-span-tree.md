# End-to-End Trace/Span Tree

## Why this exists

Before 2026-08-25, several time-consuming mechanisms in the transition lifecycle were either
invisible in the trace (script engine work, lock acquire/release, context load, validation,
instance-data persistence) or only visible in **Verbose** mode because their spans were gated
behind `AetherTracingRuntime.IsVerbose` and named with the legacy `[Order] Name` convention —
which Aether's `BusinessSpanFilterProcessor` strips at export in Business mode
(`DisplayName.StartsWith("[")`). A support engineer looking at a slow transition in the default
(Business) detail level saw a shallow, incomplete tree.

This page is the reference for the resulting span tree: every span this plan introduced or
ungated, its `ActivitySource`, its tags, and the registration rule that keeps a new source from
silently going dark in one host.

Full design rationale, decisions, and work breakdown: see
[`docs/superpowers/specs/2026-08-25-trace-span-tree-design.md`](../superpowers/specs/2026-08-25-trace-span-tree-design.md).

## Target span tree

```
TransitionJob.Execute/start-login          the transaction (async path)
   └─ or the HTTP server span (sync path — its route already carries the key)
├─ Lock.Acquire/vnext:{domain}:{flow}:{id}   admission / pipeline status lock
├─ Transition.LoadContext                TransitionContextFactory
│  ├─ Cache.Get/{cacheKey}               CacheSet (L1/L2 tags)
│  └─ Instance.Load                      GetActiveAsync
├─ Transition.Validate                   schema + policy
├─ Step.SetBusy … Step.FinalizeTransition   steps that DID something
│  ├─ Step.ResourceLock
│  │  └─ Script.Execute                  lock script
│  ├─ Step.RunOnExecuteTasks             group span
│  │  └─ Task.Execute.{taskKey}          Aether [Trace] aspect
│  │     ├─ Task.PrepareInput
│  │     │  ├─ Script.Compile            cache-hit tag
│  │     │  └─ Script.Execute            input mapping
│  │     ├─ Task.Invoke
│  │     └─ Task.ProcessOutput           → Script.* children
│  ├─ Step.ChangeState
│  │  └─ Instance.AppendData             data persist
│  ├─ Step.RunOnEntryTasks / Step.RunOnExitTasks   tasks visible one by one
│  └─ Step.HandleSubFlow → Subflow.*     → Script.* mapping children
├─ Transition.{key}                      ONLY for a chained hop (2nd+ in one call)
│  └─ Step.* …                           that hop's own steps
├─ Lock.Release/{lockKey}
└─ PostCommit.Execute                    → job enqueue children
```

Four things worth calling out that the diagram doesn't show directly:

- **The transaction names the transition.** The job span is
  `TransitionJob.Execute/{transitionKey}`, so APM groups by transition instead of showing every
  job under one `TransitionJob.Execute` name. There is no `transition/{key}` span underneath any
  more: it carried nothing its parent lacked, so `TransitionExecutor.ExecuteOneAsync` lost its
  `[Trace]` aspect and `EnrichTelemetry` no longer renames its parent. Its tags still land on the
  transaction. A side benefit: the shape no longer depends on PostSharp weaving being active —
  with weaving off, the old rename landed on the ambient job/server span.
- **A chained hop still gets a node.** When one call runs several transitions (an inline
  auto-chain, or a sync request whose continuations run in-process), hop 2 onwards gets a
  `Transition.{key}` group span from `TransitionPipeline` — the transaction's name only describes
  the first hop. That span is also what `EnrichTelemetry` tags for those hops, so they no longer
  overwrite the transaction's tags.
- **Only steps that did something appear.** A step whose applicability guard did not match
  (no lock defined, no OnEntry tasks, not a subflow state, …) reports
  `StepOutcome.ContinueNoWork()` and its span is dropped from export. Flow control is unchanged —
  `NoWork` behaves exactly like `Continue`. Deliberately still recorded:
  `RunAutomaticTransitionsStep`'s no-winner exit, because it evaluated rules (scripts ran).
- **Names carry their subject.** `Cache.Get/{cacheKey}` and `Lock.Acquire/{lockKey}` put the
  operation's subject in the span name so the tree is readable without opening spans; the
  `cache.key` / `vnext.lock.key` tags stay for querying. A step's children (task spans, subflow
  starts, job enqueues, HttpClient calls) parent to that step's span, which is why the
  parenting/lane invariants (see [Trace Lanes](trace-lanes.md)) are re-verified on every change
  here. `Cache.*` spans appear anywhere a `CacheSet<T>` read/write runs, not only under
  `Transition.LoadContext`.

## Span reference

All tag names live in `TelemetryConstants.TagNames` (`BBT.Workflow.Domain/Logging/TelemetryConstants.cs`)
unless noted otherwise. Every span with `span.category=business` renders in the default detail
level; nothing in this table is gated behind `AetherTracingRuntime.IsVerbose`.

| Span name | ActivitySource | Key tags | Notes |
|---|---|---|---|
| `TransitionJob.Execute/{transitionKey}` | `BBT.Workflow.BackgroundJobs` | lane tags (`vnext.trace.lane`, `.anchor`, `vnext.hop.predecessor`, `vnext.lane.seq`) + the payload's job tags | The transaction on the async path (`TransitionJobHandler`). Naming it after the transition is what made the old `transition/{key}` child redundant. |
| `Transition.{key}` | `BBT.Workflow.Pipeline` | `span.category=business` + everything `EnrichTelemetry` stamps (flow, instance, correlation, chain depth) | Group span for a CHAINED hop only — `TransitionPipeline` opens it from hop 2 onwards. Hop 1 needs none: the transaction is already named after it. |
| `Step.{Name}` | `BBT.Workflow.Pipeline` | `vnext.step.order`, `vnext.step.outcome` (continue / stop / skipTo:{order}), `span.category=business` | One per pipeline step **that did work** (`PipelineStepActivityHelper.StartStepActivity`). A step reporting `StepOutcome.ContinueNoWork()` has its span dropped from export (Recorded cleared), so non-applicable steps leave no trace. Trailing `Step` trimmed from the class name for display. |
| `Transition.LoadContext` | `BBT.Workflow.Pipeline` | `span.category=business` | Wraps `TransitionContextFactory`; child `Cache.Get`/`Instance.Load` spans attach automatically. `CreateFromPreloaded` is deliberately left unwrapped (no fresh load happened). |
| `Instance.Load` | `BBT.Workflow.Pipeline` | `span.category=business` | Wraps `instanceRepository.GetActiveAsync`. |
| `Transition.Validate` | `BBT.Workflow.Pipeline` | `span.category=business`, `ActivityStatusCode.Error` + message on failure | Wraps schema validation + `TransitionExecutionPolicy`. |
| `Instance.AppendData` | `BBT.Workflow.Pipeline` | `vnext.data.version`, `vnext.data.size_bytes` | Wraps the v2 append/persist funnel. Size is the serialized UTF-8 byte count — the payload itself is never attached to a span. |
| `Lock.Acquire/{lockKey}` | `BBT.Workflow.Pipeline` | `vnext.lock.key`, `vnext.lock.acquired` (false on Busy/409), `vnext.lock.lease_seconds`, `vnext.lock.kind` (`status` \| `chain`) | Emitted by both `InstanceStatusLock` (`kind=status`) and `TransitionLockScopeFactory` (`kind=chain`). A failed acquire is a span with `acquired=false`, not an exception span. |
| `Lock.Release/{lockKey}` | `BBT.Workflow.Pipeline` | `vnext.lock.key`, `vnext.lock.kind` (`status` \| `chain`) | Fires from the shared `TransitionLockScope.DisposeAsync`. `kind` is carried on the scope from whichever funnel constructed it. |
| `Task.PrepareInput` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type` | Always-on since Task 4. Nests under `Task.Execute.{key}`. |
| `Task.Invoke` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type` | Same as above. |
| `Task.ProcessOutput` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type` | Same as above; its own `Script.*` children (output mapping). |
| `Task.Execute.{taskKey}` | `BBT.Aether.Aspects` | task-key-scoped tags from the aspect | Comes from Aether's `[Trace]` aspect on `TaskExecutionEngine.ExecuteAsync`, not a vNext-owned helper — pre-existed this plan and is the parent that `PrepareInput`/`Invoke`/`ProcessOutput` nest under. |
| `FanOut.Item` | `BBT.Workflow.Tasks` | `vnext.fanout.item.key`, `.index`, `.alias`, `.queue_wait_ms` | Pre-existed; one span per fan-out batch item. |
| `Trigger.Local` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type`, `vnext.trigger.target.domain/flow/instance` | Pre-existed; not gated — the remote branch of the same task type already produces an HTTP/Dapr client span, so this covers the local (in-process) branch, which otherwise leaves no trace at all. Also the intended `WorkflowTraceLane` child-lane anchor for whatever the invocation enqueues. |
| `Script.Compile` | `BBT.Workflow.Scripting` | `vnext.script.cache.hit`, `vnext.script.key` (miss-only, see below), `span.category=business` | Covers one compile call, hits included (sub-ms, tagged rather than skipped). **Reverses** the 2026-08 script-perf decision — see "Compile-span decision reversal" below. `vnext.script.key` (the evaluator's precomputed cache key when the caller has one, else a SHA-256 prefix of the source) is tagged **only when `compilation.Compiled` is true** (an actual cache miss) — the hit hot path never computes or sets it, keeping it allocation-free. |
| `Script.ResolveHelpers` | `BBT.Workflow.Scripting` | `vnext.script.helper.count` | Covers a helper-set resolve + compile — the previously-invisible ~2s cold cost noted in the script-perf work. |
| `Script.Execute` | `BBT.Workflow.Scripting` | `vnext.script.kind` ∈ `lockKey` \| `subflowInputMapping` \| `subflowOutputMapping` | Wraps one script invocation at a call site with no existing delimiting span. Task input/output mappings are deliberately **not** wrapped separately — `Task.PrepareInput`/`Task.ProcessOutput` already delimit them. The `subflowOutputMapping` span also covers the downstream `AppendAsync`/`UpdateAsync` persistence; Task 9's `Instance.AppendData` child now makes that timing decomposable. **Controller ruling**: `Script.Execute` deliberately carries no `vnext.script.key` — the `vnext.script.kind` tag plus the parent span's context (the enclosing step/subflow span) are enough to identify which script ran; only `Script.Compile`'s cold path gets a script-identity tag (see above). |
| `Cache.Get/{cacheKey}` | `BBT.Workflow.Cache` | `cache.key`, `cache.hit`, `cache.l1.hit`, `cache.negative`, `cache.coalesced`, `cache.generation`, `cache.store=dapr`, `cache.component_type`, `span.category=business` | `CacheSet<T>.GetByVersionAsync`/`GetLatestByNameAsync` read path (`GetResolvedAsync` / `GetFullVersionAsync`). See "Cache span verification" below. |
| `Cache.Set/{cacheKey}` | `BBT.Workflow.Cache` | same shape as `Cache.Get` plus `cache.generation` from the bump | `CacheSet<T>.SetAsync`. Also covers the warm-resolutions pass (no separate `Cache.Warmup` span — see note below). |
| `Cache.Remove/{cacheKey}` | `BBT.Workflow.Cache` | `cache.generation` | `CacheSet<T>.InvalidateAsync`. |
| `Subflow.*` | `BBT.Workflow.SubFlow` | subflow-specific | Pre-existed (`SubFlowActivityHelper`: start/forward/complete/fault/cancel). Own `Script.*` children for input/output mapping. |
| `PostCommit.*` | `BBT.Workflow.BackgroundJobs` | job-specific | Pre-existed (`PostCommitExecutor` reuses `BackgroundJobActivityHelper.ActivitySource`). Own job-enqueue children. |

### A constant that doesn't mean a span

`CacheActivityHelper` also declares `OperationWarmup`, `OperationGenerationGet`, and
`OperationGenerationSet` string constants. None of them is currently passed to `StartActivity` —
they are used only as the `operation` label on `ComponentCacheOperationFailed` log lines. The work
they name (warm-resolutions pass, generation get/bump) runs entirely inside an already-open
`Cache.Get`/`Cache.Set`/`Cache.Remove` span, so it is timed as part of that span rather than as a
child. This is intentional (spec §5 item 8: "no new store-level span … double-wrapping is noise"),
not a gap — noted here only so the constant names don't mislead a reader grepping for a
`Cache.Warmup` span that will never appear in a trace.

## Cache span verification (Task 10)

The spec's work area #8 treated the component cache as verify-only: `CacheSet` was expected to
already carry enough of the tree. Verification against `CacheActivityHelper.cs` and `CacheSet.cs`
found:

1. **Gating** — `CacheActivityHelper.StartActivity` returned `null` outright whenever
   `!AetherTracingRuntime.IsVerbose` (line 48, pre-fix), and its `span.category` tag was
   `diagnostic`. Both contradicted the plan's "always-on business span" contract, so cache reads
   were invisible in Business mode exactly like the pre-Task-3 step spans. **Fixed**: the gate was
   removed and the category changed to `business`, following the same precedent
   `PipelineStepActivityHelper` set in Task 3. Span names already had no `[` prefix, so no rename
   was needed.
2. **L1 hit visibility** — `GetResolvedAsync` and `GetFullVersionAsync` call
   `CacheActivityHelper.SetL1Hit(activity, true)` on an L1 hit (`CacheSet.cs:170`, `:324`) — an L1
   hit is a real, tagged span, never suppressed. Passed as-is.
3. **L2 read duration** — when `l1_hit=false`, the L2 (Dapr) read happens via
   `TryGetEnvelopeAsync` → `distributedCache.GetAsync` (`CacheSet.cs:175`), entirely inside the
   `using var activity = CacheActivityHelper.StartActivity(...)` scope that opened before the L1
   check. The `Cache.Get` span's own duration therefore already covers the L2 read; no child span
   or extra wrapping was needed. (Note: `ICacheBackend<T>`/`RuntimeCacheBackend<T>` is the
   **database** fallback used only on a full L1+L2 miss — a third tier, not the L2 Dapr store — and
   is out of this plan's scope; it participates in `Cache.Get`'s duration the same way.)

Only finding 1 required a code change. Tests: see
`test/BBT.Workflow.Application.Tests/Caching/CacheActivityHelperTests.cs`, which pins the always-on
creation rule, the `business` category, and the L1-hit tag — mirroring
`PipelineStepActivityHelperTests`'s pattern.

## AdditionalSources registration rule

**Every new `ActivitySource` must be added to `Telemetry:Tracing:AdditionalSources` in the same
commit that introduces it, in all four hosts' `appsettings.json`** (Orchestration, Execution,
Workers.Inbox, Workers.Outbox — plus `BBT.Workflow.DbMigrator` and
`BBT.Workflow.Monitor.HttpApi.Host` where applicable). This is not optional polish: a source that
isn't registered produces spans that Aether's `ActivitySource.StartActivity` still creates
in-process, but the `TracerProvider` never subscribes to them, so they are silently dropped before
export — no error, no warning, just a gap in the trace.

This gap is exactly how the original work-area #1 config fix in the design spec came to exist:
`BBT.Workflow.Tasks`, `BBT.Workflow.SubFlow`, and `BBT.Workflow.BackgroundJobs` had sources emitting
spans well before they were added to `AdditionalSources`. `BBT.Workflow.Pipeline`,
`BBT.Workflow.Scripting`, and `BBT.Workflow.Cache` are registered in all four hosts as of this
plan — check `appsettings.json`'s `Telemetry:Tracing:AdditionalSources` array before assuming a new
source will simply work, and remember to check `vnext-helm-charts` for the corresponding
environment-level values if the host config is templated there.

## Compile-span decision reversal (2026-08-25)

The 2026-08 script-perf work (Katman 0) deliberately chose **not** to add a `Script.Compile` span,
reasoning that the ~300ms cold-compile cost was rare enough to track via the
`ScriptCompileTelemetry` accumulator tags (`vnext.script.compile.count/miss.count/total_ms`,
folded onto the nearest task span) plus a `script.compile` span event, rather than a dedicated
span per compile.

That decision was **reversed on 2026-08-25** as part of this plan: `Script.Compile` now gets a
real span (`ScriptActivityHelper.StartCompileActivity`), covering both plain compiles and
helper-set resolution (`Script.ResolveHelpers`) — the latter being the invisible ~2s cold cost the
script-perf work's own analysis flagged but chose not to instrument with a span. The old
accumulator tags and the `script.compile` event are **kept alongside**, unchanged, for query
compatibility with existing dashboards/alerts built against them.

Rationale for the reversal and the full decision record: §1 ("Decisions taken") of
[`docs/superpowers/specs/2026-08-25-trace-span-tree-design.md`](../superpowers/specs/2026-08-25-trace-span-tree-design.md).

## Related pages

- [Trace Lanes](trace-lanes.md) — the anchor/predecessor split that keeps chained hops and
  subflow handoffs siblings instead of a deep nest; the parenting model every span in this plan's
  tree relies on.
- [Correlation and Tracing](../monitoring/correlation-and-tracing.md) — gateway trace-continuation
  contract, `X-Request-Id` propagation, task-binding header handling.
- [Component Cache Generation Memo](component-cache-generation-memo.md) — the generation-token
  invalidation model the `Cache.*` spans' `cache.generation` tag reflects.
