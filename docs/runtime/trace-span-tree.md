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
│  │     │  ├─ Script.Compile/{identity}  cache-hit tag, identity in the name
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
| `Transition.Continuation/{mode}` | `BBT.Workflow.Pipeline` | `vnext.continuation.mode` (Inline \| Enqueue), `vnext.continuation.has_next`, error status on failure | What happens between one hop finishing and the next starting: an Enqueue writes the job row and arms the scheduler, an Inline hands the next context to the loop. Was the largest unattributed stretch inside the pipeline. |
| `Transition.Settle` | `BBT.Workflow.Pipeline` | `vnext.settle.status` | The resting-status flip that closes a transition — status write, its lock, the state notification. |
| `Uow.Commit` | `BBT.Workflow.Pipeline` | — | The transaction commit in `TransitionRunner`; sat outside every span, so a slow commit read as time spent nowhere. |
| `Events.PublishDeferred` | `BBT.Workflow.Pipeline` | — | Staging deferred domain events onto the bus before the commit. |
| `Cache.GenerationGet/{redisKey}` | `BBT.Workflow.Cache` | `cache.component_type`, `cache.store` | The generation-token read that precedes EVERY component resolution. The caller's `Cache.Get` sits after it, not around it, so this round trip was previously attributed to nothing. |
| `Db.{VERB}` | `OpenTelemetry.Instrumentation.EntityFrameworkCore` | `db.statement` (text, `@p0` placeholders — parameter VALUES stay behind `OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS`, false unless set) | One span per EF Core command, so a DB region resolves into the commands it actually ran. `VERB` is `SELECT`/`INSERT`/`UPDATE`/`DELETE`/`MERGE`, or `Query` when the first token is none of those. Renamed from the default DisplayName, which is the **database name** — a single transition showed fifteen siblings all called `Aether_WorkflowDb`. Reads as a check on the documented include strategy: `Instance.Load` should contain exactly three `Db.SELECT` children (instance + `DataList` + `ChildCorrelations`, split queries). |
| `Transition.ValidatePolicy` | `BBT.Workflow.Pipeline` | `span.category=business`, error status + message on failure | Wraps `ValidatePolicyAsync`. No I/O, but it runs on every auto-chain hop — without it a trace shows the schema-bearing validation and nothing for the later hops. |
| `Invoke.{taskType}/{taskKey}` | `BBT.Workflow.Execution.Invokers` | `vnext.task.key`, `vnext.task.type`, error status on a failed invocation | Execution-side, at `TaskInvokerRegistry.InvokeAsync` — the one place every task type passes through. A cache-aside MISS calls back into the registry for its source task, so that inner task gets its own span here without per-invoker instrumentation. |
| `CacheAside.Read/{cacheKey}` | `BBT.Workflow.Execution.Invokers` | `cache.hit` | The hit/miss decision, made explicit instead of inferred from whether a source-task span follows. |
| `CacheAside.Write/{cacheKey}` | `BBT.Workflow.Execution.Invokers` | — | Best-effort write-back after a miss. |
| `Task.PrepareInput` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type` | Always-on since Task 4. Nests under `Task.Execute.{key}`. |
| `Task.Invoke` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type` | Same as above. |
| `Task.ProcessOutput` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type` | Same as above; its own `Script.*` children (output mapping). |
| `Task.Execute.{taskKey}` | `BBT.Aether.Aspects` | task-key-scoped tags from the aspect | Comes from Aether's `[Trace]` aspect on `TaskExecutionEngine.ExecuteAsync`, not a vNext-owned helper — pre-existed this plan and is the parent that `PrepareInput`/`Invoke`/`ProcessOutput` nest under. |
| `FanOut.Item` | `BBT.Workflow.Tasks` | `vnext.fanout.item.key`, `.index`, `.alias`, `.queue_wait_ms`; the batch's own `Task.Invoke` span carries `vnext.fanout.item.count`, `.succeeded.count`, `.failed.count`, `.timed_out` | Pre-existed; one span per fan-out batch item. |
| `Trigger.Local` | `BBT.Workflow.Tasks` | `vnext.task.key`, `vnext.task.type`, `vnext.trigger.target.domain/flow/instance` | Pre-existed; not gated — the remote branch of the same task type already produces an HTTP/Dapr client span, so this covers the local (in-process) branch, which otherwise leaves no trace at all. Also the intended `WorkflowTraceLane` child-lane anchor for whatever the invocation enqueues. |
| `Script.Compile/{identity}` | `BBT.Workflow.Scripting` | `vnext.script.cache.hit`, `vnext.script.key` (miss-only, see below), `span.category=business` | Covers one compile call, hits included (sub-ms, tagged rather than skipped). **Reverses** the 2026-08 script-perf decision — see "Compile-span decision reversal" below. Named `Script.Compile/{identity}` on every compile (miss, wait, and hit alike) so a trace with several rules evaluating in one transition shows WHICH script is behind each span without opening it — `identity` is `ScriptCode.TraceIdentity`, resolved authored-first: an explicit `location` wins when present; a reference-encoded script with no `location` falls back to the `Reference.ToString()` (`{Domain}/{Flow}/{Key}/{Version}`); only a truly anonymous inline script falls through to an `inline:{hash8}` prefix. The raw-string `CompileToInstanceAsync(string, ...)` overload has no `ScriptCode` to draw an identity from and keeps the bare `Script.Compile` name. **`identity` is a readable LABEL, not a unique key** — `Location` is a component-relative path, not namespaced by domain or flow, so two unrelated scripts can produce the same span name: in vnext-example today, `./src/AlwaysTrueRule.csx` resolves to two distinct script bodies (`subflow-orchestration` vs. `chain-busy`/`contract-signing`, the latter two identical to each other), and `./src/UserSessionMapping.csx` likewise resolves to two distinct bodies (`account-opening`'s Extension vs. its Workflow) — two confirmed location-string collisions with differing content in that repo alone. Do not treat the span NAME as a cache or dedup key. The mitigation already exists on the miss path (where the cost is): `vnext.script.key` still disambiguates precisely there, because it IS the evaluator's cache key (or a source hash prefix) — so a miss span can always be told apart exactly, even when two miss spans share the same name. The NAME can afford to carry identity on every compile (not just misses) because `Location` is already a materialized string — no hashing, no allocation beyond the interpolation. `vnext.script.key` is unchanged by this: still miss-only, still the precise evaluator cache key (or a source hash prefix) — the tag stays the exact cache identity, while the name stays the cheap, readable one; the two serve different purposes. `vnext.script.key` (the evaluator's precomputed cache key when the caller has one, else a SHA-256 prefix of the source) is tagged **only when `compilation.Compiled` is true** (an actual cache miss) — the hit hot path never computes or sets it, keeping it allocation-free. |
| `Script.ResolveHelpers` | `BBT.Workflow.Scripting` | `vnext.script.helper.count` | Covers a helper-set resolve + compile — the previously-invisible ~2s cold cost noted in the script-perf work. |
| `Script.Execute` | `BBT.Workflow.Scripting` | `vnext.script.kind` ∈ `lockKey` \| `subflowInputMapping` \| `subflowOutputMapping` | Wraps one script invocation at a call site with no existing delimiting span. Task input/output mappings are deliberately **not** wrapped separately — `Task.PrepareInput`/`Task.ProcessOutput` already delimit them. The `subflowOutputMapping` span also covers the downstream `AppendAsync`/`UpdateAsync` persistence; Task 9's `Instance.AppendData` child now makes that timing decomposable. **Controller ruling**: `Script.Execute` deliberately carries no `vnext.script.key` — the `vnext.script.kind` tag plus the parent span's context (the enclosing step/subflow span) are enough to identify which script ran; `Script.Compile` carries identity in its NAME on every compile, and its `vnext.script.key` tag remains cold-path-only (see above). |
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

## Three memo layers on the script path, three ways of reporting a hit

The script path has three caches, and a reader following a trace needs a different signal from
each of them to tell "this work was skipped" from "this work never happened":

| Memo | Miss | Hit |
|---|---|---|
| Compile cache (`ScriptEvaluator`'s type cache) | `Script.Compile/{identity}` span | Same `Script.Compile/{identity}` span, `vnext.script.cache.hit = true` |
| Per-transition `ScriptContext` memo (`TransitionExecutionContext.GetOrBuildScriptContextAsync`) | The `ScriptContext.Build` span tree | No span — `vnext.script.context.memo.hits` incremented on `Activity.Current` (the enclosing span) |
| Per-execution mapping-factory memo (`TaskExecutorBase.GetOrCompileMappingAsync`) | `Script.Compile` span (the engine ran) | No span — `vnext.script.mapping.memo.hits` incremented on `Activity.Current` (the enclosing span) |

The compile cache can afford a span on both branches because it already has one on the miss path,
and tagging it a second way costs nothing extra. The other two only ever had a span for the miss:
before this counter, a hit produced no evidence at all — a trace showing no `ScriptContext.Build`
child and no `Script.Compile` span was ambiguous between "this reused work" and "this work was
never required in the first place."

A span per hit was considered and rejected: a 100-item FanOut batch reusing the same compiled
mapping would add 100 near-instant hit spans to the tree for a fact that a single number already
answers — "how often did we avoid the work?" `Activity.IncrementCounterTag` (see
`BBT.Workflow.Domain/Logging/ActivityCounterExtensions.cs`) sets that number on the span that was
already there, starting at 1 and accumulating on repeat calls within the same span.

## EF Core instrumentation: the worker-poll cost (measured)

Enabling `AddEntityFrameworkCoreInstrumentation` buys the DB layer inside every pipeline span, and
charges for it outside them. Measured on a local run, 100 **idle** seconds with no traffic:

| | |
|---|---|
| EF spans nested correctly under pipeline spans | all of them, in `vnext-app` |
| EF commands that became their own ROOT transaction | 21 — **entirely** `vnext-inbox-worker` / `vnext-worker-outbox` |
| Idle rate | ~13 root traces/min ≈ **18k/day per pod-set** |

The cause is not health checks. The Inbox and Outbox workers poll their tables on a timer with no
ambient `Activity`, so each poll's `SELECT` starts a trace of its own — a single-span trace that
says a poll happened. In the transaction list these outrank real work by name frequency.

Nothing is wrong with the data; the question is whether it is worth storing. Two options, neither
applied here because dropping spans is the environment owner's call:

- `EntityFrameworkInstrumentationOptions.Filter` — skip commands whose `Activity.Current` is null,
  i.e. instrument DB work that belongs to a traced operation and never *start* a trace for one.
- Leave it, and filter at the collector by `service.name` + span name, the same way the Dapr
  internals are handled (see [Trace Lanes](trace-lanes.md)).

## Related pages

- [Trace Lanes](trace-lanes.md) — the anchor/predecessor split that keeps chained hops and
  subflow handoffs siblings instead of a deep nest; the parenting model every span in this plan's
  tree relies on.
- [Correlation and Tracing](../monitoring/correlation-and-tracing.md) — gateway trace-continuation
  contract, `X-Request-Id` propagation, task-binding header handling.
- [Component Cache Generation Memo](component-cache-generation-memo.md) — the generation-token
  invalidation model the `Cache.*` spans' `cache.generation` tag reflects.
