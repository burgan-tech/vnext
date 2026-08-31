# Repeated Component Reads in the Transition Pipeline — Trace Analysis

**Date:** 2026-08-25
**Evidence:** trace `c4cd78d327ab73f66b88227360f5141e` (local Elastic APM, `.ds-traces-apm-default-2026.08.25-000007`)
**Shape:** 1 HTTP start + 24 transactions, 665 spans, ~4 flows (login → contract → online + subprocess), nested
subprocess starts, local triggers, script tasks.
**Status:** analysis only — no code changed. Feeds a follow-up plan.

> **Cold-start caveat, read this first.** The runtime was deployed at 13:15:07
> (`labels.deployment_id=Development-vnext-app-20260825-131507`) and the trace starts at 13:15:29 — 22 seconds
> later. Every component's *first* read in this trace is therefore a cold miss, and every script's first compile
> is cold. The **counts** below are steady-state truth; the **cold-miss and compile costs** are one-off per
> process and must not be projected onto a warm system. Where that distinction changes a conclusion, it is
> called out.

## 1. Where the time actually goes

Self-time (span duration minus children), whole trace:

| Mechanism | Count | Self time | Per call | Character |
|---|---:|---:|---:|---|
| `Script.Compile` | 34 | **2552.6 ms** | 75.1 ms | Cold compiles — dominates everything else |
| `Cache.Get` **MISS** (DB load + write-back) | 12 | 986.6 ms | 82.2 ms | One-off per component per process |
| `Instance.Load` (DB) | 30 | **593.4 ms** | 19.8 ms | Steady-state cost, partly duplicated |
| Dapr `GetState` (generation token) | 100 | **369.0 ms** | 3.69 ms | Steady-state cost, **almost all avoidable** |
| `Cache.Get` **L1 hit** | 80 | 94.6 ms | p50 0.49 ms / p90 1.09 ms | Cheap but not free — deserialization |
| Dapr `SaveState` | 32 | 77.2 ms | 2.41 ms | Cache write-back, tied to misses |

**The headline is uncomfortable but important:** repeated *component reads* are **not** the top cost in this
trace — script compilation is, by an order of magnitude. That is a separate, already-known workstream
(`script-perf`). Within component reads, the expensive part is **not** the cached body (L1 hits are ~0.5 ms);
it is the **generation-token round trip that precedes every single resolution**, plus **duplicated instance
DB loads**.

## 2. Redundancy inventory

| # | What is read repeatedly | In this trace | Distinct values | Steady-state pattern | Marginal cost per redundant read |
|---|---|---:|---:|---|---|
| R1 | **Flow definition** (`sys-flows`) | 68 reads | 3 flows / 6 keys | **2× per transition job** (minimum), **5×** on a job that starts a nested flow | 1 gen `GetState` (3.7 ms) + 0.5–1.7 ms deserialize |
| R2 | — same key, worst offender | **31×** `contract-flow:res:…:1.1.0` | 1 | one flow re-resolved 31 times in one business request | 84 ms total for that key alone |
| R3 | **Payload schema** (`sys-schemas`) | 10 reads | 3 | **2× per start / trigger entry** (validated twice) | 1 gen `GetState` + ~0.05–0.5 ms |
| R4 | **Instance row** (`Instance.Load`) | 30 loads | ~8 instances | **2× per async accept**: strategy + job pipeline | **19.8 ms** (DB round trip) |
| R5 | **`Transition.Validate`** | 16 runs | — | **2× per start / trigger entry** | full schema resolve + JSON-schema validation, twice |
| R6 | **`Transition.LoadContext`** | 30 runs | — | 1× per hop under the pipeline **+ 1× at the accept** | flow read + instance load (R1 + R4) |
| R7 | **Generation token** (`GetState`) | 100 reads | ~10 keys | **1 per component resolution, always** | 3.69 ms Redis round trip |
| R8 | Task definitions (`sys-tasks`) | 14 reads | 4 | 1× per task execution — **not** redundant | — |

Per-transaction detail (`flow` = `sys-flows` Cache.Get, `inst` = `Instance.Load`, `gen` = generation
`GetState`, `val` = `Transition.Validate`):

| Transaction | ms | flow | inst | gen | val |
|---|---:|---:|---:|---:|---:|
| `POST …/instances/start` | 2071 | 3 | 1 | 7 | **2** |
| `TransitionJob.Execute/start-login` | 2282 | 5 | 2 | 9 | **2** |
| `TransitionJob.Execute/invoke-next` | 680 | 5 | 2 | 10 | **2** |
| `TransitionJob.Execute/online-to-notify` | 1080 | 5 | 2 | 8 | **2** |
| `TransitionJob.Execute/start-online` | 263 | **2** | 1 | 4 | 0 |
| `TransitionJob.Execute/notify-to-pre-approval` | 139 | **2** | 1 | 2 | 0 |
| `TransitionJob.Execute/login-initial-to-awaiting-ready` | 40 | **2** | 1 | 0 | 0 |
| **Total (25 transactions)** | | **68** | **30** | **100** | **16** |

The `flow=2, inst=1` rows are the **floor**: every transition job resolves its flow twice, no exceptions. The
`flow=5, inst=2, val=2` rows are jobs that also start a nested flow (subprocess/trigger), which pays the
accept-path duplication on top.

## 3. Root causes

### C1 — The workflow is resolved again after it was already resolved and stashed (R1, R2)

`TransitionRunner` runs the job inside `IServiceScopeFactory.ExecuteWithWorkflowAsync`
([TransitionRunner.cs:101/122/149](../../../src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs)),
which loads the flow and publishes it on the scoped `IWorkflowContext`
([ServiceScopeFactoryExtensions.cs:263-268](../../../src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/ServiceScopeFactoryExtensions.cs)):

```csharp
var workflowResult = await componentCacheStore.GetFlowAsync(domain, workflowKey, workflowVersion, ct);   // read #1
workflowContext.SetWorkflow(workflowResult.Value!);
```

Then the pipeline builds its context and resolves the very same flow from scratch
([TransitionContextFactory.cs:65](../../../src/BBT.Workflow.Application/Execution/Transitions/Factory/TransitionContextFactory.cs)):

```csharp
return componentCacheStore.GetFlowAsync(input.Domain, input.WorkflowKey, input.WorkflowVersion, ...)   // read #2
```

`TransitionContextFactory` never consults `IWorkflowContext`. Visible in the trace as the flow `Cache.Get`
at transaction level followed by an identical one inside `Transition.LoadContext`.

**Not a blind fix:** the scoped workflow is not always the flow the transition needs (subflow forward,
cross-flow trigger, parent-vs-child). Reuse has to be conditional on a key+version match.

### C2 — The accept path validates and rebuilds the context that the app service already had (R3, R4, R5, R6)

For a transition request:

1. `InstanceCommandAppService:644` → `ValidateTransitionRequestAsync` → `CreateFromPreloaded` (**zero DB — good**)
   + `CheckAdmission` + `ValidateAsync` → **Validate #1** (resolves the schema)
2. `AsyncTransitionStrategy.ExecuteAsync`
   ([AsyncTransitionStrategy.cs:77-91](../../../src/BBT.Workflow.Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs)):
   ```csharp
   return ctxFactory.CreateAsync(context, cancellationToken)      // flow read + Instance.Load from DB
       .BindAsync(ctx => ValidateAsync(ctx, cancellationToken))   // Validate #2 — same schema again
       .BindAsync(ctx => EnqueueJobAndReturnContextAsync(...));
   ```

The app service deliberately built a DB-free context and validated with it, and then the strategy throws that
away: it re-reads the instance from the database and re-runs the identical validation. For start, the same
shape appears as `ValidateStartTransitionAsync` (in-memory context, **Validate #1**) followed by the
strategy's **Validate #2**.

Both call sites carry doc comments asserting they exist so sync and async callers share one validation error
contract — so this is a *deliberate* double-validation whose second run is the redundant one, not an accident.

**Good news the trace also proves:** the pipeline itself does **not** re-validate the schema. It calls
`ValidatePolicyAsync` ([TransitionPipeline.cs:118/413](../../../src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs)),
policy-only. No third schema read. (It is invisible in traces because only `ValidateAsync` carries a span —
worth adding for symmetry, but not a cost.)

### C3 — Generation-token memoization is off by default (R7)

Every component resolution reads a generation token from Redis before touching L1
([ComponentGenerationProvider.cs:35-47](../../../src/BBT.Workflow.Application/Caching/ComponentGenerationProvider.cs)).
An in-process memo exists but is gated:

```csharp
if (options.Value.GenerationMemoSeconds <= 0)   // ComponentCacheOptions
    return false;                                // → always miss the memo
```

and the shipped config sets it to zero
([appsettings.json:261-263](../../../orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json)):

```json
"ComponentCache": { "GenerationMemoSeconds": 0 }
```

Result: **100 Redis round trips (369 ms) in one business request** to check tokens that cannot change during
it. This default was a deliberate correctness choice (a hotfix publish must be visible immediately) — see
[[component-version-floating-resolution]] — so raising it globally trades publish-visibility latency for
speed. A **request-scoped** memo would get nearly all of the win with no cross-request staleness.

### C4 — An L1 hit still deserializes the whole flow body (R1 amplifier)

L1 hit p50 is 0.49 ms and p90 1.09 ms, but the split by component size is stark: small components
(`sys-schemas`, `sys-tasks`) hit in **0.05 ms**, while flow bodies take **0.5–1.7 ms** (worst observed
48 ms). L1 stores bytes, so every "hit" pays a full JSON deserialize of the workflow definition. This is what
makes C1's duplicate flow read cost real rather than free — and it is why de-duplicating *within a request*
matters more than making the cache faster.

## 4. What is worth fixing (ranked, with honest sizing)

Estimates are per-hop steady-state, derived from the measured per-call costs above.

| Rank | Change | Removes | Est. saving | Risk |
|---|---|---|---:|---|
| 1 | **Request/context-scoped component resolution memo** (flow + schema + task, keyed by domain/key/version, bound to the scope) | C1, C3, C4 in one move: 2nd..Nth resolution of anything within a request | ~4–5 ms per redundant resolution; **~100 ms** across this trace's 22 jobs; kills most of the 369 ms token traffic | Low–medium. Must be scope-bound (never static) and must key on version so `latest` vs pinned stay distinct. |
| 2 | **Carry the accept-path context into the strategy** instead of rebuilding it | C2's duplicate `Instance.Load` (19.8 ms) + duplicate `Validate` | ~20–25 ms per async accept; **~160–200 ms** across this trace | Medium. Must not weaken the "one validation contract" guarantee — the second validation should be *skipped as already-done*, not deleted. |
| 3 | **Let `TransitionContextFactory` reuse `IWorkflowContext`** when key+version match | C1 specifically, on every job | ~4 ms per transition job | Low, **if** the match check is strict. A wrong reuse would run a transition against the wrong definition — the one genuinely dangerous item here. |
| 4 | Make `ValidatePolicyAsync` observable (span) | nothing — visibility only | 0 | None. |
| — | *Not recommended:* raising `GenerationMemoSeconds` globally | C3 | same as #1 for tokens | Trades cluster-wide publish visibility; #1 achieves it without that trade. |

**Deliberately out of scope of this analysis:** `Script.Compile` (2.5 s / 34 calls) dominates the trace and is
owned by the script-perf workstream. Any plan built on this document should say explicitly that it is *not*
addressing the largest number on the board, so nobody mistakes a 200 ms win for a fix to a 2.5 s problem.

## 5. What still needs verification

1. **A warm trace.** Every number here comes from a process that started 22 seconds earlier. The counts are
   structural and will hold, but the miss/compile costs will not. Re-running the same vnext-example flows
   against a warmed runtime would confirm the steady-state profile and is the right baseline for measuring
   any fix.
2. **Does the duplicate flow read ever resolve to a *different* definition?** If `latest` floats between the
   two reads within one request, they can legitimately differ — that is exactly the case C3's default protects.
   A plan must state which of the two wins.
3. **Subflow/parallel shapes.** This trace covers nested subprocess starts and local triggers. Fan-out
   (`FanOutTask`) and cross-domain subflow forwarding are not represented; their per-item component reads
   should be sampled before assuming the same 2× pattern.
4. **`gen=0` rows.** `login-initial-to-awaiting-ready` shows 2 flow reads but 0 generation `GetState`. Either
   the token read was served from somewhere else or those spans were not recorded — worth one focused check,
   because it may reveal an existing memo path that could simply be widened.
