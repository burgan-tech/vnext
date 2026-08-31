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

> **Stale note:** the `EventHook.{name}` nodes below (and the `EventHook.{name}` row in
> [Span reference](#span-reference)) describe the per-event `EventHook`/`HookedDistributedEventBus`
> model, which has since been removed outright — every distributed event now rides the
> transactional outbox instead. See
> [Event Publish Modes § Purpose](event-publish-modes.md#purpose) for the current model. Kept here
> as historical record of the tree at the time this page was written, not as current behavior.

```
TransitionJob.Execute/start-login          the transaction (async path)
   └─ or the HTTP server span (sync path — its route already carries the key)
├─ Lock.Acquire/vnext:{domain}:{flow}:{id}   admission / pipeline status lock
├─ Transition.LoadContext                TransitionContextFactory
│  ├─ Cache.Get/{cacheKey}               CacheSet (cache.source = l1|l2|backend)
│  │  └─ Cache.Write/{cacheKey}         write-back, ONLY on a backend miss
│  └─ Instance.Load                      GetActiveAsync
│     ├─ Instance.Query.Prepare          names the pre-SELECT leading gap
│     └─ Db.SELECT × 3                   instance + DataList + ChildCorrelations
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
├─ Events.PublishDeferred                staging deferred events onto the bus before the commit
│  └─ EventHook.{name}                   HandledOrFallback hooks — run at publish time
├─ Uow.Commit                            transaction commit
│  └─ EventHook.{name}                   DurablePostCommit hooks — run after the commit
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

## Reading the descent ladder

A built-in function that lands on an instance with an open subflow correlation forwards the whole
request to the child, and repeats that at every level. Each level gets one `Subflow.Descend` span, so
the chain reads as a ladder and each level's `Cache.*` and `Db.*` spans nest under the level that
paid for them:

```
GET .../instances/{id}/functions/state    server span (level 0)
├─ Auth.ResolveRoles
├─ Cache.Get/state-fn:v7:…                level 0's own cache read
└─ Subflow.Descend/chain-busy-middle      depth=1  transport=local
   ├─ Cache.Get/state-fn:v7:…
   └─ Subflow.Descend/chain-busy-leaf     depth=2  transport=local
      └─ Cache.Get/state-fn:v7:…
```

Three things this is built to answer, none of which were answerable before:

- **Which level paid for a cache miss or a `Db.SELECT`.** Before these spans a three-level read was
  one flat region under the server span, and the only way to attribute a cache read was to decode its
  key by hand.
- **Whether the hop was in-process or over the network** (`vnext.descent.transport`). A same-domain
  descent produced no spans at all, while a cross-domain one was visible through HttpClient
  instrumentation — so the cheap hop was the invisible one and the expensive hop was the traced one.
- **How deep the chain went** (`vnext.subflow.depth`). Nothing bounds the descent, so an unexpected
  depth is itself the finding.

`vnext.subflow.depth` is carried in-process by an `AsyncLocal` (`SubflowDescentContext`) and across a
domain boundary by the `X-Subflow-Depth` header — stamped on the way out by
`CurrentUserForwardHeadersHelper` and read on the way in by `ParentInstanceIdEnrichmentMiddleware`.
An absent or malformed header degrades to 0, so an older peer keeps working and only the numbering
restarts.

**Implicit parenting is load-bearing here.** These spans use the `StartActivity(name, kind)` overload,
not the one taking an explicit `ActivityContext`. The explicit overload sets `ParentSpanId` but leaves
`Activity.Parent` null, and baggage is inherited through the Activity *chain* — so an explicitly
parented descent span severs baggage for everything under it, including the cross-domain read one
level down that forwards `X-Root-Instance-Id` by reading that baggage back out. A test pins it
(`ADescent_InheritsTheCallersBaggage`).

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
| `Instance.Query.Prepare` | `BBT.Workflow.Pipeline` | `span.category=business` | Wraps the `WithDetailsAsync()` awaited by `EfCoreInstanceRepository.FindByIdentifierAsync` / `FindActiveByKeyAsync` / `FindByIdentifierAsReadOnlyAsync` (via the shared private `PrepareDetailedQueryAsync` helper) — child of `Instance.Load`, sibling and immediate predecessor of the `Db.SELECT` spans. Started with `PipelineStepActivityHelper.ActivitySource.StartActivity` directly (implicit-parent overload), not `StartOperationActivity` — the same explicit-parent-context defect noted for `Discovery.Resolve` applies here too and fixing it is out of scope for this span. See "Reading the Instance.Query.Prepare span" below. |
| `Transition.Validate` | `BBT.Workflow.Pipeline` | `span.category=business`, `ActivityStatusCode.Error` + message on failure | Wraps schema validation + `TransitionExecutionPolicy`. |
| `Instance.AppendData` | `BBT.Workflow.Pipeline` | `vnext.data.version`, `vnext.data.size_bytes` | Wraps the v2 append/persist funnel. Size is the serialized UTF-8 byte count — the payload itself is never attached to a span. |
| `Lock.Acquire/{lockKey}` | `BBT.Workflow.Pipeline` | `vnext.lock.key`, `vnext.lock.acquired` (false on Busy/409), `vnext.lock.lease_seconds`, `vnext.lock.kind` (`status` \| `chain`) | Emitted by both `InstanceStatusLock` (`kind=status`) and `TransitionLockScopeFactory` (`kind=chain`). A failed acquire is a span with `acquired=false`, not an exception span. |
| `Lock.Release/{lockKey}` | `BBT.Workflow.Pipeline` | `vnext.lock.key`, `vnext.lock.kind` (`status` \| `chain`) | Fires from the shared `TransitionLockScope.DisposeAsync`. `kind` is carried on the scope from whichever funnel constructed it. |
| `Transition.Continuation/{mode}` | `BBT.Workflow.Pipeline` | `vnext.continuation.mode` (Inline \| Enqueue), `vnext.continuation.has_next`, error status on failure | What happens between one hop finishing and the next starting: an Enqueue writes the job row and arms the scheduler, an Inline hands the next context to the loop. Was the largest unattributed stretch inside the pipeline. |
| `Transition.Settle` | `BBT.Workflow.Pipeline` | `vnext.settle.status` | The resting-status flip that closes a transition — status write, its lock, the state notification. |
| `Uow.Commit` | `BBT.Workflow.Pipeline` | — | The transaction commit in `TransitionRunner`; sat outside every span, so a slow commit read as time spent nowhere. |
| `Events.PublishDeferred` | `BBT.Workflow.Pipeline` | — | Staging deferred domain events onto the bus before the commit. |
| `EventHook.{name}` | `BBT.Workflow.Instances.Events` | `vnext.event.name`, `vnext.hook.name`, `vnext.hook.mode` | **Stale — `HookedDistributedEventBus`/`EventHook` no longer exist; see the note under [Target span tree](#target-span-tree) and [Event Publish Modes § Purpose](event-publish-modes.md#purpose).** One span per hook invocation (`HookedDistributedEventBus.ExecuteHooksAsync`), named after the hook with the conventional `EventHook`/`Hook` suffix trimmed (`vnext.hook.name` keeps the untrimmed name). Its parent tells you the mode: under `Uow.Commit` means `DurablePostCommit` (hook ran after the ambient UoW committed); under `Events.PublishDeferred` means `HandledOrFallback` (hook ran at publish time). No re-parenting — the span simply opens under whatever is ambient. Error status + message on a failed or throwing hook; the failure still stays swallowed (hooks never fail the publish). |
| `Subflow.Descend/{targetFlow}` | `BBT.Workflow.Instances.Read` | `vnext.subflow.depth`, `vnext.descent.transport` (`local` \| `remote`), `vnext.descent.function` (`state` \| `master` \| `schema` \| `view` \| `extensions` \| `authorize`), target `vnext.domain`/`vnext.flow.key`/`vnext.instance.id`, `vnext.parent.instance.id`, `vnext.descent.outcome` (only when the descent produced no usable answer) | One level of a built-in function's walk into an active subflow. Emitted by the five `InstanceQueryAppService` descent helpers, `AuthorizeAppService`'s subflow forward and `InstanceRetryAppService`. See "Reading the descent ladder" below. |
| `Auth.ResolveRoles` | `BBT.Workflow.Authorization` | `vnext.auth.provider`, `vnext.auth.memo.hit`, `vnext.auth.roles.count`, `vnext.auth.outcome` (`resolved` \| `empty` \| `failed`), `vnext.auth.position`, `sub`, `act.sub` | Caller-role resolution through an external provider. Emitted on BOTH the provider call and the request-scope memo hit, with the difference in `memo.hit` — the compile cache's rule. Only providers that do I/O are instrumented; the default provider reads `ICurrentUser` in-process. |
| `Cache.GenerationGet/{redisKey}` | `BBT.Workflow.Cache` | `cache.component_type`, `cache.store` | The generation-token read that precedes EVERY component resolution. The caller's `Cache.Get` sits after it, not around it, so this round trip was previously attributed to nothing. |
| `Db.{VERB}` | `OpenTelemetry.Instrumentation.EntityFrameworkCore` | `db.statement` (text, `@p0` placeholders — parameter VALUES stay behind `OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS`, false unless set) | One span per EF Core command, so a DB region resolves into the commands it actually ran. `VERB` is `SELECT`/`INSERT`/`UPDATE`/`DELETE`/`MERGE`, or `Query` when the first token is none of those. Renamed from the default DisplayName, which is the **database name** — a single transition showed fifteen siblings all called `Aether_WorkflowDb`. Reads as a check on the documented include strategy: `Instance.Load` should contain exactly three `Db.SELECT` children (instance + `DataList` + `ChildCorrelations`, split queries). |
| `Transition.ValidatePolicy` | `BBT.Workflow.Pipeline` | `span.category=business`, error status + message on failure | Wraps `ValidatePolicyAsync`. No I/O, but it runs on every auto-chain hop — without it a trace shows the schema-bearing validation and nothing for the later hops. |
| `Discovery.Resolve/{domain}` | `BBT.Workflow.Pipeline` | `vnext.discovery.domain`, `vnext.discovery.endpoint_kind`, `span.category=business`; error status + message on disabled discovery, 404, or a transport/HTTP failure | Wraps `DomainDiscoveryResolver.GetEndpointAsync`, called from 32 sites (the trigger task executors, the `RemoteInstance*AppService`s, `RemoteAuthorizeAppService`, `RemoteRelatedInstanceReader`). Reuses the pipeline's `ActivitySource` rather than a new one — parents to whatever is ambient at the call site with no per-call-site edits, and turns the discovery HTTP call from an unattributed HttpClient span into this span's child. The endpoint cache was deliberately removed, so discovery is queried on **every** resolution — this span's rate IS the true cross-domain resolution rate, not a cache-miss rate. |
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
| `Cache.Get/{cacheKey}` | `BBT.Workflow.Cache` | `cache.key`, `cache.hit`, `cache.l1.hit`, `cache.source` (`l1` \| `l2` \| `backend`), `cache.negative`, `cache.coalesced`, `cache.generation`, `cache.store=dapr`, `cache.component_type`, `span.category=business` | `CacheSet<T>.GetByVersionAsync`/`GetLatestByNameAsync` read path (`GetResolvedAsync` / `GetFullVersionAsync`). See "Cache span verification" below. |
| `Cache.Write/{cacheKey}` | `BBT.Workflow.Cache` | same shape as `Cache.Get`; `ActivityStatusCode.Error` + the exception when the distributed write fails | The **write-back of a cache-aside miss** (`CacheSet<T>.TryWriteAsync`), so it appears as a child of the `Cache.Get` that missed. Deliberately a different name from `Cache.Set`: this is traffic the cache creates for itself on the read path, not a caller publishing. The write failure is swallowed by design — a cache that cannot be written is still a correct read — and this span is the only signal of it outside the log. |
| `Cache.Set/{cacheKey}` | `BBT.Workflow.Cache` | same shape as `Cache.Get` plus `cache.generation` from the bump | `CacheSet<T>.SetAsync`. Also covers the warm-resolutions pass (no separate `Cache.Warmup` span — see note below). |
| `Cache.Remove/{cacheKey}` | `BBT.Workflow.Cache` | `cache.generation` | `CacheSet<T>.InvalidateAsync`. |
| `Subflow.*` | `BBT.Workflow.SubFlow` | subflow-specific | Pre-existed (`SubFlowActivityHelper`: start/forward/complete/fault/cancel). Own `Script.*` children for input/output mapping. |
| `PostCommit.*` | `BBT.Workflow.BackgroundJobs` | job-specific | Pre-existed (`PostCommitExecutor` reuses `BackgroundJobActivityHelper.ActivitySource`). Own job-enqueue children. |

### Reading the `Instance.Query.Prepare` span

**Why it exists.** Live measurement across 300 `Instance.Load` spans found the gap to its
`Db.SELECT` children almost entirely **leading** (mean lead 0.60ms, between-children 0.26ms,
**trail 0.03ms**) — not materialization, not a miscalculation. `Db.SELECT` is EF's
command-level instrumentation (`CommandExecuting`→`CommandExecuted`) and simply does not start
until the command is issued, so everything before the first command — DbContext/connection
acquisition — was invisible. The lead was p50 0.63ms but p90 2.40ms and max 88ms, with the large
values scattered over time rather than clustered at startup, consistent with connection
acquisition under pool pressure — a hypothesis the spans up to this point could not confirm.
`Instance.Query.Prepare` wraps exactly that window (`WithDetailsAsync()`, before any query
executes), splitting the leading gap into "DbContext/connection acquisition" (this span) versus
"everything else" (whatever lead remains between this span's end and the first `Db.SELECT`).

**How to read a measurement once this span is live:**

- **`Instance.Query.Prepare` dominates the lead** (its own duration accounts for most of the gap
  between `Instance.Load`'s start and the first `Db.SELECT`) → the cost is DbContext/connection
  acquisition. The connection-pool hypothesis stands; the follow-up is Npgsql pool metrics plus
  pool sizing, not more spans.
- **`Instance.Query.Prepare` is near zero and a lead still remains** between its end and the
  first `Db.SELECT` → the cost is EF query compilation, or a connection opened lazily at
  execution time rather than during `WithDetailsAsync()`. The follow-up is a compiled-query or
  pool-warmup investigation, not more spans.

**Baseline (record here so a future reader can tell whether things moved):** measured 2026-08-28,
300 live `Instance.Load` spans, pre-`Instance.Query.Prepare` — lead p50 **0.63ms**, p90
**2.40ms**, max **88ms**; trail **0.03ms**. Once this span has meaningful production volume, redo
the same percentile breakdown on `Instance.Query.Prepare` itself (not just the residual lead) and
update this baseline with the split.

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

## EF Core instrumentation: the worker-poll cost (RESOLVED)

Enabling `AddEntityFrameworkCoreInstrumentation` buys the DB layer inside every pipeline span, and
charges for it outside them. Measured on a local run, 100 **idle** seconds with no traffic, before
the fix below existed:

| | |
|---|---|
| EF spans nested correctly under pipeline spans | all of them, in `vnext-app` |
| EF commands that became their own ROOT transaction | 21 — **entirely** `vnext-inbox-worker` / `vnext-worker-outbox` |
| Idle rate | ~13 root traces/min ≈ **18k/day per pod-set** |

The cause is not health checks. The Inbox and Outbox workers poll their tables on a timer with no
ambient `Activity`, so each poll's `SELECT` starts a trace of its own — a single-span trace that
says a poll happened. In the transaction list these outrank real work by name frequency.

**Resolved** by `IdlePollSpanProcessor` (`src/BBT.Workflow.HttpApi.Shared/Telemetry/`), an
OpenTelemetry `BaseProcessor<Activity>` that clears `ActivityTraceFlags.Recorded` on any span whose
`DisplayName` starts with `Db.` **and** has no parent (`activity.Parent is null &&
activity.ParentSpanId == default`) — the same export-drop technique
`PipelineStepActivityHelper.SetStepOutcome` already uses for no-work pipeline steps: exporters skip
the span, but it stays valid in-process, so nothing downstream misbehaves. It is registered
(`WorkflowApiBaseServiceCollectionExtensions`) only where
`Telemetry:Tracing:DropRootDbSpans` is `true` in that host's config — currently the two worker
`appsettings.json` files (`workers/BBT.Workflow.Workers.Inbox`, `workers/BBT.Workflow.Workers.Outbox`)
— so Orchestration and Execution, which have no idle poll loop, are left untouched. The processor is
a blunt, host-scoped filter by design: it drops every rootless `Db.*` span in a gated host, not just
poll-loop ones, on the reasoning that a rootless DB command in these two hosts is idle noise by
construction once real work roots its own episode (`Outbox.Process`, see
[Related pages](#related-pages) below) rather than running ambient. This fix has since been measured; see [Verification](#verification-2026-08-30-local-stack) for post-cutover results.

The two options this section previously weighed — an `EntityFrameworkInstrumentationOptions.Filter`
skipping commands with no ambient `Activity`, or a collector-side filter by `service.name` + span
name (the pattern already used for Dapr internals, see [Trace Lanes](trace-lanes.md)) — were both
superseded by the processor-level drop above, which needed no exporter-side collector change and no
loss of in-process validity for the span.

## Verification (2026-08-30, local stack)

Acceptance run against the local four-host stack, all apps restarted onto the new build at
**2026-08-30T15:30:14Z** — that instant is the cutover used to split "pre-change" from
"post-cutover" evidence below.

Traffic: the vnext-example Subflow+ChainBusy integration subset (22/22 green) plus a
15-instance/5-concurrency `terminal-relay-load.py` run (15/15 completed; relay gap p50 53.4 ms /
p95 63.6 ms / p99 69.4 ms — all verdicts PASS, unchanged from the pre-change baseline of p99
65.9 ms, i.e. the trace work cost no measurable latency).

All nine acceptance checks passed:

1. **Business-trace purity** — 5 sampled traces containing `TransitionJob.Execute*`: zero
   `Outbox.Process` / `EventBus.PublishEnvelope` / `EventBus.PublishToBroker` /
   `POST internal/outbox-wakeup` / fact `*.Handle` spans leaked into any of them.
2. **`Outbox.Process`** — 328 spans, all trace roots (0 parented), all carrying links.
3. **Fact deliveries** — 327 `Instance*.Handle` spans, all roots, all with links.
4. **Command continuation** — `TransitionContinuationRequested` never fires in the flows
   available to this run (0 occurrences in 24h; the runtime takes the direct Dapr job-payload
   path instead), so continuation was verified on its sibling `ContinueTrace` command,
   `ChildSubflowCancelRequested.Handle`: post-cutover it has a parent and shares its trace with
   the producing app's spans. Recorded here honestly: this check passed via the substitute
   command, not via the originally-named `TransitionContinuationRequested` path, which this run
   had no traffic to exercise.
5. **Relay same-tree** — 3 sampled relay traces each contain `Subflow.TerminalRelay` ×2,
   `SubFlow.Completion` ×2, `SubFlow.Resume` ×2, and zero `*.Handle` spans: the flow's own
   settlement work stayed inside the flow trace while the duplicate backup delivery moved out.
6. **Idle noise** — 2-minute buckets: pre-cutover every bucket had exactly 12 root `Db.*` spans
   per worker (and those were the only worker spans present); post-cutover every bucket has
   **zero** root `Db.*` spans, including during heavy traffic.

   A first idle measurement (11.1 minutes) recorded zero spans from every service, but the worker
   processes' liveness across that specific window was not independently established, so "no spans"
   and "no process" were not distinguishable from that run alone. It was superseded on 2026-08-31
   by a **controlled** measurement: a 5.7-minute window (05:27:22Z–05:33:07Z) with both workers
   returning `health=200` at **both** ends and demonstrably exporting telemetry throughout (28
   outbox-worker and 54 inbox-worker spans in the window, none of them root `Db.*`), compared
   against an equal-length idle window on the previous build (2026-08-30 15:17:00Z–15:22:45Z),
   which produced **69** root `Db.*` spans (36 outbox + 33 inbox). Same duration, workers alive in
   both cases: **69 → 0**.
7. **Wakeup isolation** — 0 `POST internal/outbox-wakeup` spans post-cutover. The Dapr
   **sidecar**'s `pubsub/…aether.outbox.wakeup…` spans still exist (241 of them), but 10/10
   sampled are standalone traces with zero occurrences inside business traces — the documented
   collector-filter knob remains the way to remove them at the source.
8. **Duration containment** — the recorded pre-change baseline trace
   (`c4b324894c9f9f8236841b820b09f8e3`, 367 spans) had 14 violations where a child span started
   7–44 ms after its parent had already ended, every one of them event-plumbing (`*.Handle`
   under `Events.PublishDeferred`, `Outbox.Process` under `EventBus.Publish`). After the change:
   0 plumbing violations across 5 business traces and 3 delivery traces. Nine remaining
   "violations" are the deliberate trace-lane flattening (`PostCommit.*` / `TransitionJob.*` /
   `SubFlow.Resume` anchored to their lane anchor rather than nested, see [Trace
   Lanes](trace-lanes.md)) — by design, unchanged by this work, and present in the baseline
   shape too. Noting this explicitly so a future reader does not mistake it for a regression.
9. **Identity tags** — delivery-trace roots carry `messaging.message.id` and
   `vnext.causation.id` (both the CloudEvent envelope id), `vnext.delivery.role=backup`, domain,
   flow, instance id, and parent/subflow instance ids; `vnext.delivery.attempt` is correctly
   absent when the event's `RearmAttempt` is null.

**Methodology note for future measurers**: in OpenObserve's trace stream, `start_time`/`end_time`
are **nanoseconds** while `duration` is **microseconds** — mixing the two units silently produces
nonsense containment results. This run hit that trap mid-measurement and corrected it; the
corrected containment math (check 8 above) was validated against `end_time`, not the `duration`
field.

## Related pages

- [Event Trace Chain](event-trace-chain.md) — how `EventHook.{name}` (this page) connects to the
  outbox → pub/sub → inbox handoff, verified live evidence, and the current state of outbox-side
  trace continuity: `Outbox.Process` now **roots its own trace** and attaches the originating
  transition's context as an `ActivityLink` rather than re-parenting onto it — a deliberate
  linked-root model, not the rejoin this page's evidence was gated on. The Inbox side mirrors this
  split: command events keep re-parenting onto the producer's trace (`EventTraceMode.ContinueTrace`),
  while the seven `Instance*` fact events now root their own delivery trace and link the producer
  instead (`EventTraceMode.LinkedDelivery`). See
  [Event Publish Modes § Observability contract](event-publish-modes.md#observability-contract) for
  the full tag reference.
- [Trace Lanes](trace-lanes.md) — the anchor/predecessor split that keeps chained hops and
  subflow handoffs siblings instead of a deep nest; the parenting model every span in this plan's
  tree relies on.
- [Correlation and Tracing](../monitoring/correlation-and-tracing.md) — gateway trace-continuation
  contract, `X-Request-Id` propagation, task-binding header handling.
- [Component Cache Generation Memo](component-cache-generation-memo.md) — the generation-token
  invalidation model the `Cache.*` spans' `cache.generation` tag reflects.
