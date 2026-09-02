# Trace Episode Separation Implementation Plan (Madde 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate message-transport plumbing (outbox worker publish, inbox fact-event deliveries, wakeup nudge, idle polling) into their own linked traces, while flow continuation (relay, lanes, subflows, immediate async commands) stays in the business trace tree — per `/Users/U0B006/Desktop/trace_refactor.md` and the four user-approved decisions.

**Architecture:** Aether's `Outbox.Process` inverts its parenting (new root + `ActivityLink` to the origin, instead of re-joining the origin trace); vnext's `EventTraceScope` gains an explicit per-handler mode — command events (`TransitionContinuationRequested`, `ChildSubflow*Requested`) keep continuing the trace, fact events (the seven `Instance*` events) become linked delivery traces carrying `messaging.message.id` + `vnext.causation.id`. The wakeup nudge's ambient trace context is severed so it stops leaking into business traces, and the workers' idle-poll `Db.SELECT` root-trace noise is suppressed at the source. Lane resets are untouched — a genuine backup-settled resume still anchors into the parent's tree.

**Tech Stack:** .NET 10, Aether SDK (local pack `1.0.39-local`), OpenTelemetry (`SuppressInstrumentationScope`), OpenObserve (verification backend), xUnit + NSubstitute + Shouldly.

**Spec:** `/Users/U0B006/Desktop/trace_refactor.md` + the Analysis of 2026-08-30 (conversation; four decisions approved by user — see Design Summary). The empirical baseline trace is `c4b324894c9f9f8236841b820b09f8e3` in OpenObserve (367 spans, 4 services — the "before" picture).

## Global Constraints

- **NO pushes to origin.** Local commits only, in BOTH repos. Aether continues on `feature/outbox-wakeup-signal`; vnext stays on `feature/trace-span-tree`.
- **Package version MUST bump to `1.0.39-local`** (NOT repack 1.0.38-local — the NuGet global cache already holds extracted 1.0.38-local; same-version repack would be silently ignored). vnext `Directory.Build.props` bumps accordingly.
- **Solution path:** always `/Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx` for vnext restore/build/test.
- **Dirty-worktree staging discipline:** stage ONLY exact files per commit; `orchestration/.../appsettings.json` still carries an unrelated modification — never sweep it in. Untracked `.superpowers/*`, `scripts/trace-profile.py`, `scripts/__pycache__/` stay untouched.
- **vnext logging:** WorkflowLogs `[LoggerMessage]` extensions only. Aether may use plain ILogger.
- **What must NOT change (pinned by the user):** the relay path's same-tree behavior (`Subflow.TerminalRelay` → `SubFlow.Completion` → `SubFlow.Resume` in the flow's trace); lane mechanics (`WorkflowTraceLane`, `FlatLaneActivity`, `EnterChildLane`, job-payload `TraceRoot`/`ParentTraceRoot` carriers); `EventTraceScope`'s **lane Reset** side-effects (only its SPAN parenting policy changes); sync pipeline shape; `StartActivityAsChildWithLink` deferred-job policy.
- **Propagation triple invariant (traceparent + tracestate + baggage):** wherever context crosses a boundary, all three travel together or are severed together. Carriers stay as-is (outbox row `TraceParent`+`TraceState`; event/job payload `TraceParent`/`TraceState`/`RequestId`/lane fields; HTTP via the registered propagators, which include W3C baggage). The nudge severing (A2) must cut all three — `Activity.Current = null` does exactly that (baggage is Activity-chained). LinkedDelivery roots (B3) deliberately do NOT inherit producer baggage through the link (links never carry baggage — known repo trap: explicit parent severs baggage); the handlers' existing per-event baggage re-seeding (`SetBaggage(RootInstanceId)` etc. from event fields) is therefore load-bearing and must be verified intact at every LinkedDelivery call site.
- **Span-duration correctness is an acceptance criterion, not a hope:** the baseline's broken shape (`EventBus.Publish` 1.2 ms with later-starting 20 ms "children") must be impossible after the split — C2 check 8 measures containment.
- vnext test baseline: ~191 pre-existing failures suite-wide; judge by "no NEW failures" (worktree-at-base comparison only if counts move).
- The local stack (docker infra + 4 apps) is running with the madde-1 build; Faz C restarts the 4 apps on the new build.

## Design Summary (the spec)

### The four approved decisions

1. **`Outbox.Process` re-join is inverted** (partially reverting Aether's `outbox-trace-continuity` behavior): the span becomes the ROOT of a new trace; the origin context from the row's `ExtraProperties["TraceParent"/"TraceState"]` becomes an `ActivityLink` (plus the worker-loop ambient as a second link when present). Rationale: trace_refactor — "Outbox publish: kaynak transition span'ının child'ı yapılmamalı; ayrı worker execution'ıdır."
2. **Inbox split policy:** command events continue the trace (immediate async command = same trace, producer→consumer); fact events start a NEW delivery trace with an `ActivityLink` to the origin. Classification:
   - **ContinueTrace (commands):** `TransitionContinuationRequested`, `ChildSubflowCancelRequested`, `ChildSubflowFaultRequested` (imperative "do X to instance Y" messages that continue a flow).
   - **LinkedDelivery (facts):** `InstanceCanceledEvent`, `InstanceCompletedCleanupEvent`, `InstanceFaultedCleanupEvent`, `InstanceSubStateChangedEvent`, `InstanceSubCompletedEvent`, `InstanceSubFaultedEvent`, `InstanceSubCanceledEvent` (the sub-terminal three are backup deliveries — the primary settlement already lives in the flow tree via the relay).
   - Lane `Reset` behavior inside `EventTraceScope` is UNCHANGED for both modes — a rare backup-settled `SubFlow.Resume` still anchors to `ParentTraceRoot` and lands in the parent's tree while the delivery plumbing stays in its own trace.
3. **Wakeup + poller noise suppressed entirely:** the nudge publish severs ambient trace context (no more `POST internal/outbox-wakeup` inside business traces — 421 observed in the baseline window) and the worker's wakeup endpoint is excluded from server-span export; the workers' idle-poll `Db.SELECT` single-span root traces (~13/min idle) are dropped before export (Task B0 — see its ruling note: implemented in vnext's telemetry pipeline rather than inside Aether, because the OTel SDK is not referenced from `BBT.Aether.Infrastructure`).
4. **Acceptance is measured in OpenObserve** with explicit SQL checks and thresholds (Faz C) — same discipline as madde 1.

### Identity attributes (trace_refactor table — gap closure)

Add `TagNames` constants and stamp where the carrier exists:
- `messaging.message.id` — CloudEvent envelope id, stamped by `EventTraceScope` on every handler span.
- `vnext.causation.id` — the message/event that directly caused this work: same envelope id on handler spans; on `Outbox.Process` the existing `outbox.message_id` already serves this role (Aether keeps neutral naming — no vnext.* names in Aether).
- `vnext.delivery.attempt` — stamped where a counter exists: the terminal events' `RearmAttempt` (handler spans; null → omit).
- Promote existing raw strings `vnext.chain.id` and `vnext.pipeline.profile` (`TransitionExecutor.cs:181-182`) to `TagNames` constants.

### Empirical acceptance targets (checked in Faz C against OpenObserve)

| Check | Target |
|---|---|
| Business transition traces (traces containing `TransitionJob.Execute*` or `Step.*`) | contain ZERO spans named `Outbox.Process`, `EventBus.PublishEnvelope`, `EventBus.PublishToBroker`, `*.Handle` (fact events), `POST internal/outbox-wakeup` |
| `Outbox.Process` spans | all trace ROOTS; each has ≥1 link when the row carried a TraceParent |
| Fact `*.Handle` spans | trace roots with ≥1 link + `messaging.message.id` tag |
| `TransitionContinuationRequested.Handle` | still SAME trace as its producer (continuation preserved) |
| Relay same-tree | a child-terminal hop's trace still contains the parent's `SubFlow.Completion` + `SubFlow.Resume` (relay path) and does NOT contain the backup copy |
| Idle noise | 10 idle minutes produce 0 new root `Db.SELECT` traces from `vnext-worker-outbox`/`vnext-inbox-worker` |

---

# FAZ A — Aether (repo: `/Users/U0B006/Documents/repos/burgan-tech/aether`, branch `feature/outbox-wakeup-signal`)

### Task A1: Invert `Outbox.Process` parenting (root + links)

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs` (the span-start block, ~lines 87-138)
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/` — check for existing OutboxProcessor tests first (`rg -l "OutboxProcessor" framework/test`); if a harness exists, extend it; if not, create `Processing/OutboxProcessorTracingTests.cs` testing the span-construction logic (see Step 1 note).

**Interfaces:**
- Consumes: the row's `ExtraProperties["TraceParent"/"TraceState"]` (written by `EfCoreOutboxStore.StoreAsync`), `InfrastructureActivitySource.Source`.
- Produces: `Outbox.Process` as a NEW ROOT span (`parentContext: default`), `ActivityKind.Producer`, links = `[originContext (isRemote:true, when parseable), loopContext (when ambient present)]`, existing tags kept (`event.name`, `event.topic`, `outbox.message_id`, `outbox.retry_count`).

- [ ] **Step 1: Locate and read the current block.** Today (verified in inventory): `parentContext = originContext` when the row's TraceParent parses, with the loop ambient attached as a link; fallback `parentContext = loopContext`. If the span construction is inline in `ProcessOutboxMessagesAsync`, first extract it into an internal static helper `StartProcessSpan(OutboxMessage message, ActivityContext loopContext)` returning the `Activity?` — that makes the parenting policy unit-testable without a DbContext. (If a test harness for the processor already exists, skip the extraction only if the existing tests can pin the new policy directly.)

- [ ] **Step 2: Write the failing tests** (ActivityListener-based; sample-all listener on `InfrastructureActivitySource.Source`):

```csharp
// OutboxProcessorTracingTests
// 1. Row WITH parseable TraceParent → span has: no parent (span.ParentSpanId == default),
//    Links contains one link whose TraceId == the origin traceparent's TraceId.
// 2. Row WITHOUT TraceParent → span is root; links contain only the loop context (when provided).
// 3. Ambient loop activity present → it appears as a link, never as the parent.
// 4. Tags event.name / outbox.message_id / outbox.retry_count still present.
```

Write these as real tests against the extracted helper (construct an `OutboxMessage` in memory with `ExtraProperties`).

- [ ] **Step 3: Implement the inversion.** The new policy, replacing the old parent selection:

```csharp
// Separate worker execution by design: the publish episode is its own trace. The originating
// transition is causally related, not structurally the parent — a link preserves the relation
// without stretching the origin trace across the worker hop (trace_refactor: outbox publish
// must not be a child of the source transition span).
var links = new List<ActivityLink>(2);
if (TryParseOrigin(message, out var originContext))   // existing TraceParent/TraceState parse, isRemote:true
    links.Add(new ActivityLink(originContext));
if (loopContext != default)
    links.Add(new ActivityLink(loopContext));

var activity = InfrastructureActivitySource.Source.StartActivity(
    "Outbox.Process",
    ActivityKind.Producer,
    parentContext: default,     // new root — always
    links: links);
```

Keep every existing tag. Delete the old origin-as-parent branch and its fallback (the fallback's loop-parent behavior is subsumed: loop is now always just a link).

- [ ] **Step 4: Run the tests; run the full Infrastructure test project** (no NEW failures vs its current 187-pass state).

- [ ] **Step 5: Commit (local only)**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/aether add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs framework/test/BBT.Aether.Infrastructure.Tests
git -C /Users/U0B006/Documents/repos/burgan-tech/aether commit -m "feat(outbox)!: Outbox.Process roots its own trace and links the origin instead of re-joining it"
```

### Task A2: Sever the wakeup nudge's ambient trace context

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/OutboxWakeupCoordinator.cs` (`NotifyFireAndForget`)
- Test: extend `framework/test/BBT.Aether.Infrastructure.Tests/Polling/OutboxWakeupCoordinatorTests.cs`

**Interfaces:**
- Consumes: `OpenTelemetry.SuppressInstrumentationScope` if available — FIRST verify `BBT.Aether.Infrastructure` references the OpenTelemetry SDK package (`rg "OpenTelemetry" framework/src/BBT.Aether.Infrastructure/*.csproj` + transitive via Directory.Packages.props). 

- [ ] **Step 1: Implement the severing** inside the `Task.Run` closure, FIRST lines:

```csharp
_ = Task.Run(async () =>
{
    // The nudge is infrastructure, not business flow: sever the ambient Activity captured via
    // ExecutionContext so the publish's client span (and the delivery it causes on the worker)
    // can never attach to — or propagate the traceparent of — the committing business trace.
    Activity.Current = null;
    try
    {
        using var cts = new CancellationTokenSource(NotifyTimeout);
        await wakeupNotifier!.NotifyAsync(cts.Token);
    }
    ...
});
```

PLUS, if the OTel SDK is referenced (Step 0 check): wrap the notify in `using var _ = OpenTelemetry.SuppressInstrumentationScope.Begin();` (after `Activity.Current = null`) so the gRPC/HTTP client auto-instrumentation emits no span at all for the nudge. If the SDK is NOT referenced by Infrastructure, do NOT add the package (dependency-weight decision) — `Activity.Current = null` alone already stops business-trace pollution; the nudge's client span then becomes a tiny standalone root trace, which Task B4's collector note covers. Record which branch applied in the report.

- [ ] **Step 2: Test** — extend the coordinator tests: start an ambient `Activity` (from a listener-registered test source), invoke the captured OnCompleted callback, and assert the notifier was invoked with `Activity.Current == null` inside the call (capture via a substitute notifier that records `Activity.Current` at invoke time). 

- [ ] **Step 3: Run coordinator tests (all green) + commit (local only)**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/aether add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/OutboxWakeupCoordinator.cs framework/test/BBT.Aether.Infrastructure.Tests/Polling/OutboxWakeupCoordinatorTests.cs
git -C /Users/U0B006/Documents/repos/burgan-tech/aether commit -m "fix(outbox): wakeup nudge publishes without ambient trace context"
```

### Task A4: Pack `1.0.39-local`

- [ ] **Step 1:** `dotnet build /Users/U0B006/Documents/repos/burgan-tech/aether/framework` + Infrastructure tests.
- [ ] **Step 2:** `dotnet pack /Users/U0B006/Documents/repos/burgan-tech/aether/framework -p:PackageVersion=1.0.39-local -o /Users/U0B006/Documents/repos/burgan-tech/aether/.local-feed` — verify all 8 vnext-referenced ids present at the NEW version.
- [ ] **Step 3:** No commit needed (feed is git-excluded).

---

# FAZ B — vnext (repo: `/Users/U0B006/Documents/repos/burgan-tech/vnext`, branch `feature/trace-span-tree`)

### Task B0: Suppress idle-poll span noise (replaces the original Aether-side Task A3)

> **Controller ruling (2026-08-30, plan defect):** the original A3 required `OpenTelemetry.SuppressInstrumentationScope` inside `BBT.Aether.Infrastructure`. Task A2 verified the OTel SDK is **not** referenced from that project (resolved `project.assets.json` has zero OpenTelemetry packages; a compile probe returned CS0103). Adding the SDK to a core framework assembly would push an OTel dependency onto every Aether consumer for a logging concern — rejected. The suppression moves to vnext's telemetry configuration, which already owns the OTel SDK and already registers a custom span processor, and is config-gated so only the two worker hosts enable it. Cost if wrong: a genuinely useful ROOT `Db.*` span inside a worker would be dropped — acceptable, because after Task A1 every real unit of worker work is parented (under `Outbox.Process` / a handler span), so a root `Db.*` span in those hosts is idle-poll noise by construction.

**Files:**
- Create: `src/BBT.Workflow.HttpApi.Shared/Telemetry/IdlePollSpanProcessor.cs`
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs` (register the processor in the tracing block, next to the existing `RequestIdSpanProcessor` registration, gated on config)
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json`, `workers/BBT.Workflow.Workers.Inbox/appsettings.json` (`Telemetry:Tracing:DropRootDbSpans: true`)
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/IdlePollSpanProcessorTests.cs` (or the project where `RequestIdSpanProcessor` is already tested — grep first and follow that placement)

**Interfaces:**
- Consumes: `OpenTelemetry.BaseProcessor<Activity>` (already available in this project), `System.Diagnostics.Activity`.
- Produces: `IdlePollSpanProcessor : BaseProcessor<Activity>` — in `OnEnd`, clears `ActivityTraceFlags.Recorded` on spans that are trace roots AND whose `DisplayName` starts with `"Db."`. Same drop technique the repo already uses in `PipelineStepActivityHelper.SetStepOutcome` for no-work steps.

- [ ] **Step 1: Write the failing tests**

```csharp
// IdlePollSpanProcessorTests — construct Activity objects via a listener-backed ActivitySource:
// 1. Root span named "Db.SELECT"      → after OnEnd, (activity.ActivityTraceFlags & Recorded) == 0
// 2. CHILD span named "Db.SELECT"     → Recorded still set (real work under Outbox.Process must survive)
// 3. Root span named "Outbox.Process" → Recorded still set (only Db.* roots are noise)
// 4. Root span named "Db.INSERT"      → dropped (verb-agnostic: any Db.* root)
```

Build the activities with a real `ActivitySource` + `ActivityListener` (`Sample = AllData`) so `ActivityTraceFlags` behave as in production; assert on the flag, not on a mock.

- [ ] **Step 2: Run to verify they fail** (type does not exist yet).

- [ ] **Step 3: Implement**

```csharp
using System.Diagnostics;
using OpenTelemetry;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Drops the root <c>Db.*</c> spans the Outbox/Inbox worker poll loops mint on every idle cycle.
/// A poll that finds nothing produces one parentless EF span, which the backend stores as a
/// complete one-span trace — measured at roughly 13 root traces per minute per worker, purely
/// from idling. Real work is unaffected: after the outbox processor roots its own
/// <c>Outbox.Process</c> episode, every database command that belongs to actual processing runs
/// UNDER a span, so a parentless <c>Db.*</c> span in these hosts is idle noise by construction.
/// Clearing <see cref="ActivityTraceFlags.Recorded"/> is the same export-drop technique
/// <c>PipelineStepActivityHelper</c> uses for no-work steps.
/// Registered only where <c>Telemetry:Tracing:DropRootDbSpans</c> is true (the two workers).
/// </summary>
public sealed class IdlePollSpanProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Parent is null
            && activity.ParentSpanId == default
            && activity.DisplayName.StartsWith("Db.", StringComparison.Ordinal))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
```

Register it in the tracing block (read the existing `.AddProcessor(serviceProvider => new RequestIdSpanProcessor(...))` call and follow its shape), gated on the config flag:

```csharp
// Worker hosts only: see IdlePollSpanProcessor. Other hosts have no idle poll loop, so the
// processor would only add a per-span branch for nothing.
if (configuration.GetValue("Telemetry:Tracing:DropRootDbSpans", false))
{
    tracing.AddProcessor(new IdlePollSpanProcessor());
}
```

Verify the exact config key path against how the file reads other `Telemetry:Tracing:*` values (it binds a Telemetry section — if binding rather than `GetValue` is the local idiom, follow the local idiom and add the property to whatever options type carries `ExcludedPaths`).

- [ ] **Step 4: Add the flag to BOTH worker appsettings** under their existing `Telemetry:Tracing` sections.

- [ ] **Step 5: Run the tests (green) + build the solution (0 errors). Commit exact files:**

```bash
git commit -m "fix(telemetry): drop root Db.* spans minted by worker idle polling"
```

### Task B1: Consume Aether 1.0.39-local

**Files:** `Directory.Build.props` (1.0.38-local → 1.0.39-local)

- [ ] **Step 1:** Bump; restore with both sources; build:

```bash
dotnet restore /Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx -s /Users/U0B006/Documents/repos/burgan-tech/aether/.local-feed -s https://api.nuget.org/v3/index.json
dotnet build /Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx
```

- [ ] **Step 2:** Commit `Directory.Build.props` only: `chore: bump Aether to 1.0.39-local (trace episode separation)`

### Task B2: Identity-attribute constants + raw-string promotion

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` (TagNames additions)
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/TransitionExecutor.cs:181-182` (raw strings → constants)

- [ ] **Step 1:** Add to `TagNames` (match file style):

```csharp
public const string ChainId = "vnext.chain.id";
public const string PipelineProfile = "vnext.pipeline.profile";
public const string CausationId = "vnext.causation.id";
public const string MessagingMessageId = "messaging.message.id";
public const string DeliveryAttempt = "vnext.delivery.attempt";
```

(Check first — some may exist; reuse, never duplicate values.)

- [ ] **Step 2:** In `TransitionExecutor.EnrichTelemetry`, replace the raw `"vnext.pipeline.profile"` / `"vnext.chain.id"` literals with the constants. NO value/behavior change.

- [ ] **Step 3:** Build Domain+Application; run `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionExecutor"` if such tests exist (else build suffices). Commit exact files: `refactor(telemetry): promote chain/profile tags to constants; add causation/message-id/delivery-attempt tags`

### Task B3: `EventTraceScope` mode split (command vs fact)

**Files:**
- Modify: `workers/BBT.Workflow.Workers.Inbox/Tracing/EventTraceScope.cs`
- Modify: ALL 10 handler call sites under `workers/BBT.Workflow.Workers.Inbox/Handlers/**` (grep `EventTraceScope.Start`)

**Interfaces:**
- Produces:

```csharp
/// <summary>How a consumed event's handler span relates to the producer's trace.</summary>
public enum EventTraceMode
{
    /// <summary>Immediate async COMMAND: the consumer continues the producer's trace
    /// (parent = the event's TraceParent). Same policy as before this change.</summary>
    ContinueTrace,

    /// <summary>FACT delivery: the handler roots its own delivery trace; the producer's
    /// TraceParent becomes an ActivityLink. Lane Reset side-effects are identical in both
    /// modes — a genuine backup-settled resume still anchors into the parent's tree.</summary>
    LinkedDelivery
}
```

`EventTraceScope.Start(string activityName, TEvent evt, ICorrelationIdProvider provider, EventTraceMode mode, string? messageId = null)` — no default for `mode` (every call site decides explicitly).

- [ ] **Step 1: Read `EventTraceScope.cs` fully** (it already: parses `evt.TraceParent` → explicit parent; links ambient on mismatch; restores RequestId; Resets the lane). 

- [ ] **Step 2: Implement the mode:**
- `ContinueTrace`: EXACTLY today's behavior, byte-for-byte.
- `LinkedDelivery`: start the span with `parentContext: default` (new root); when `evt.TraceParent` parses, add it as an `ActivityLink` **built from BOTH fields** (`ActivityContext.TryParse(evt.TraceParent, evt.TraceState, isRemote: true, ...)` — tracestate must ride the link, not just traceparent); the ambient pub/sub delivery span (if any) is also linked (today's mismatch-link logic generalizes). Tag `TelemetryConstants.TagNames.MessagingMessageId` and `TagNames.CausationId` with `messageId` when provided. **Identity tags:** the delivery root is the trace's entry point, so it must be findable by instance — stamp `TagNames.Domain`, `TagNames.Flow`, and the event's instance id(s) (`InstanceId`; for sub-terminal events also `SubflowInstanceId`/`ParentInstanceId`) from the event fields inside `EventTraceScope` (all consumed events expose Domain/Flow/instance ids — verify per event type and adapt the property access). Everything else (RequestId restore, `WorkflowTraceLane.Reset(...)` from lane-aware fields or the new span id) UNCHANGED in both modes. **Baggage:** a new root inherits nothing — after starting the span, verify each LinkedDelivery handler still performs its existing per-event baggage re-seed (e.g. `SetBaggage(RootInstanceId, ...)`); where a handler relied on inherited baggage rather than re-seeding, add the re-seed from event fields and note it in the report.
- Add the optional `int? deliveryAttempt` stamp: if the event exposes a `RearmAttempt` (pattern-match `ISubflowTerminalEvent` events' property via the concrete types or a small interface check — simplest: an optional `int? deliveryAttempt` parameter the handler passes), tag `TagNames.DeliveryAttempt`.

- [ ] **Step 3: Update the 10 call sites** — each handler passes its classification + `envelope.Id` as `messageId`:
- `ContinueTrace`: `TransitionContinuationRequestedEventHandler`, `ChildSubflowCancelRequestedEventHandler`, `ChildSubflowFaultRequestedEventHandler`.
- `LinkedDelivery`: `InstanceCanceledEventHandler`, `InstanceCompletedCleanupEventHandler`, `InstanceFaultedCleanupEventHandler`, `InstanceSubStateChangedEventHandler`, `InstanceSubCompletedEventHandler` (+ pass `deliveryAttempt: eventData.RearmAttempt`), `InstanceSubFaultedEventHandler` (idem), `InstanceSubCanceledEventHandler` (idem).
(Verify the actual handler list with the grep — if more/fewer than 10, classify by the same command/fact rule and note it in the report.)

- [ ] **Step 4: Tests.** The Inbox worker has no dedicated test project (verify with `ls test/`); behavior is pinned in Faz C's OpenObserve acceptance checks instead. Compensating unit coverage: if extracting the parenting decision into a small pure helper inside `EventTraceScope` is trivial (`static (ActivityContext parent, IEnumerable<ActivityLink> links) ResolveParenting(EventTraceMode, string? traceParent, string? traceState, ActivityContext ambient)`), do it and cover it from `test/BBT.Workflow.Infrastructure.Tests` via a compile-include or — if project boundaries make that awkward — leave a `// pinned by Faz C acceptance` comment and report the gap explicitly. Do NOT create a new test project for this.

- [ ] **Step 5: Build the Inbox worker; commit exact files:** `feat(inbox): fact events root linked delivery traces; commands keep continuing the producer trace`

### Task B4: Wakeup endpoint span exclusion + sidecar-noise note

**Files:**
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json` (`Telemetry:Tracing:ExcludedPaths` — add `"^/internal/outbox-wakeup$"`)
- Modify: `docs/runtime/event-publish-modes.md` (observability section: one paragraph noting (a) the nudge publishes context-severed, (b) the worker excludes the endpoint's server span, (c) Dapr SIDECAR spans for the wakeup topic (`pubsub/{env}.aether.outbox.wakeup.v1`) remain as tiny standalone traces — if they bother dashboards, an OTel-collector filter dropping that span name is the knob; flag for vnext-helm-charts/otel config owners, do NOT edit the collector config in this plan).

- [ ] **Step 1:** Add the ExcludedPaths entry (verify the section's exact key list format in that file — the pattern list already exists with `^/health$` etc.).
- [ ] **Step 2:** Doc paragraph.
- [ ] **Step 3:** Build the worker; commit the two files: `fix(outbox-worker): exclude wakeup endpoint from server-span export; document sidecar-noise knob`

### Task B5: Documentation + memory alignment

**Files:**
- Modify: `docs/runtime/trace-span-tree.md` (the outbox/inbox/poller sections: re-join → linked-root model; idle-poll noise section marked RESOLVED with the suppression mechanism)
- Modify: `docs/runtime/event-trace-chain.md` (the live-verified same-trace join example must be re-labelled: that behavior now applies ONLY to command events; fact events show the new linked-delivery shape)
- Modify: `docs/runtime/event-publish-modes.md` (observability contract: delivery traces + causation/message-id/delivery-attempt tags)

- [ ] **Step 1:** Update the three docs surgically — only the sections describing the changed behaviors; keep everything else.
- [ ] **Step 2:** Commit docs: `docs(runtime): trace episode separation — linked delivery traces, suppressed infra noise`

---

# FAZ C — Doğrulama (OpenObserve acceptance, measured)

### Task C1: Restart apps on the new build + traffic generation

- [ ] **Step 1:** Stop the 4 running app processes (they run the madde-1 build). Rebuild the solution. Restart all four with `--launch-profile http` (`--no-build` after the fresh build). Verify health 4201/4202.
- [ ] **Step 2:** Generate traffic: run the subflow integration subset (`--filter "FullyQualifiedName~Subflow|FullyQualifiedName~ChainBusy"`, vnext-example, runsettings already pointing at localhost:4201; MockLab must be up) AND one `terminal-relay-load.py --instances 15 --concurrency 5` run (venv from the previous session or recreate: `python3 -m venv .../venv && pip install psycopg2-binary requests`). All tests green; load verdicts PASS (p99 ≤ 250 ms threshold unchanged).
- [ ] **Step 3:** Let the stack idle 10 minutes (for the idle-noise check) — schedule the acceptance queries after that window.

### Task C2: Acceptance queries (OpenObserve, org `default`, stream `vnext`, creds root@example.com / Complexpass#@123, window = since the C1 restart)

Run each; record PASS/FAIL. SQL via `POST /api/default/_search?type=traces` (columns: `trace_id, span_id, reference_parent_span_id, operation_name, service_name, links, start_time`):

- [ ] **1. Business-trace purity:** pick 5 traces containing `operation_name LIKE 'TransitionJob.Execute%'` from the load window; for each `trace_id`, assert zero rows with `operation_name IN ('Outbox.Process','EventBus.PublishEnvelope','EventBus.PublishToBroker','POST internal/outbox-wakeup')` and zero fact-`*.Handle` rows (`InstanceSub%.Handle`, `InstanceCanceled.Handle`, `Instance%Cleanup.Handle`, `InstanceSubStateChanged.Handle`).
- [ ] **2. Outbox roots+links:** all `Outbox.Process` spans in the window have empty `reference_parent_span_id` AND non-empty `links`.
- [ ] **3. Fact deliveries:** fact `*.Handle` spans are roots with non-empty `links`; spot-check one for the `messaging.message.id` column (flattened as `messaging_message_id`).
- [ ] **4. Command continuation:** `TransitionContinuationRequested.Handle` spans share their `trace_id` with a producer-side span (join on trace_id → the trace also contains `vnext-app` spans).
- [ ] **5. Relay same-tree:** one child-terminal trace contains `Subflow.TerminalRelay` AND `SubFlow.Completion%` AND `SubFlow.Resume%` (app service), and does NOT contain any `*.Handle` span.
- [ ] **6. Idle noise:** in the 10-minute idle window, zero NEW root `Db.SELECT` spans from `vnext-worker-outbox`/`vnext-inbox-worker` (compare counts at window start/end).
- [ ] **7. Wakeup isolation:** `POST internal/outbox-wakeup` spans in the window: zero (excluded), and no business trace contains the sidecar `pubsub/%aether.outbox.wakeup%` span.
- [ ] **8. Duration containment (span süre doğruluğu):** for the 5 sampled business traces from check 1 plus 3 sampled delivery traces, assert NO span starts after its parent's end (`child.start_time > parent.start_time + parent.duration`, small clock-skew tolerance ≤ 5 ms; compute by joining each span to its `reference_parent_span_id` within the trace). The baseline trace `c4b32489…` FAILS this check today (late-starting worker/consumer "children" under `EventBus.Publish`); post-change traces must PASS.
- [ ] **9. Identity/tag coverage:** every sampled delivery-trace root carries `messaging_message_id` + `vnext_causation_id` + domain/flow/instance-id columns; sub-terminal delivery roots also show `vnext_delivery_attempt` when the event carried `RearmAttempt`; the origin publish spans still carry `outbox_message_id` (cross-trace correlation path: origin span → message id → delivery root, and delivery link → origin trace).

Any FAIL → treat as a defect, fix loop (subagent), re-run the failed check.

### Task C3: Report + memory + closure

- [ ] **Step 1:** Append the acceptance table (checks + PASS/FAIL + one sample before/after trace id pair) to `docs/runtime/trace-span-tree.md` under a "Verification (2026-08-30)" heading; commit.
- [ ] **Step 2:** Update memory `trace-span-tree-work.md` (episode-separation landed; manual verification now OpenObserve-based, acceptance queries recorded) and `event-publish-refactor-plan.md` (madde 2 done). Mark `dapr-sidecar-trace-export.md`'s open orphan note if the linked-root model changes its status (executor: judge from what C2 shows).
- [ ] **Step 3:** Final local commits both repos; **no push**. Pre-push gates now include: real Aether prerelease (1.0.39), helm notes (WakeupSignalEnabled + collector filter knob).

---

## Self-Review Notes

- Spec coverage: decision 1 → A1; decision 2 → B3 (classification table exhaustive over the 10 handlers; lane Reset pinned unchanged); decision 3 → A2 + B0 (was A3, moved to vnext by controller ruling) + B4; decision 4 → C2's nine measured checks. Identity attributes → B2+B3 (+Aether keeps neutral names). Relay/lane/sync invariants listed as MUST-NOT-CHANGE in Global Constraints and pinned by C2 checks 4-5.
- Version bump to 1.0.39-local is load-bearing (cache collision) — Global Constraint + A4/B1.
- Known judgment calls the executor must NOT "fix": `ContinueTrace` keeps byte-for-byte legacy behavior; Aether uses neutral tag names (no vnext.*); sidecar wakeup spans are only documented, not filtered here; no new test project for the Inbox worker.
- A3 has an explicit BLOCKED path if the OTel SDK isn't referenced from Aether.Infrastructure — controller rules on package-add vs collector-filter fallback.
- Type consistency: `EventTraceMode { ContinueTrace, LinkedDelivery }`, `EventTraceScope.Start(name, evt, provider, mode, messageId?, deliveryAttempt?)`, TagNames `ChainId/PipelineProfile/CausationId/MessagingMessageId/DeliveryAttempt` — used identically across tasks.
- User-added requirements (2026-08-30): propagation triple invariant (Global Constraints), tracestate riding the ActivityLink + identity tags + baggage re-seed verification (B3), duration-containment and tag-coverage acceptance checks (C2 checks 8-9).
