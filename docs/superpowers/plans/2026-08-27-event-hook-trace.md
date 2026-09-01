# Event Hook Spans & Outbox Trace Continuity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every event hook a named child span (under `Uow.Commit` for post-commit hooks), and connect the outbox hop into the originating trace so the chain publish → outbox drop → outbox publish → inbox handle reads as one tree.

**Architecture:** vnext's `HookedDistributedEventBus.ExecuteHooksAsync` gains a span per hook invocation on a new `BBT.Workflow.Instances.Events` source (a name three of four hosts already list in `AdditionalSources`). Aether's `EfCoreOutboxStore` persists the drop's trace identity into the row's existing `ExtraProperties` and tags the ambient publish span with the message id; Aether's `OutboxProcessor` re-parents its per-message span into that identity — the mirror of what vnext's inbox `EventTraceScope` already does. The inbox side needs no change.

**Tech Stack:** .NET 10, `System.Diagnostics.Activity`, xUnit + Shouldly + NSubstitute. Two repos: vnext and aether (`/Users/U0B006/Documents/repos/burgan-tech/aether`, framework under `framework/src`, tests under `framework/test`).

**Spec:** `docs/superpowers/specs/2026-08-27-event-hook-trace-spec.md`

## Global Constraints

- **Local commits only, in BOTH repos. NEVER `git push`.** No branch/merge/rebase. vnext branch: `feature/trace-span-tree`. aether: commit on its current branch unless it is a main/master branch — in that case create a local branch `feature/outbox-trace-continuity` first and say so.
- **The Aether change is USER-APPROVED (2026-08-27).** Do not re-open that decision; the spec records it.
- **The vnext working tree has uncommitted changes that are NOT yours** — several `launchSettings.json` / `appsettings.json` files and `ScriptConditionEvaluator.cs` are the user's local work. One exception in this plan: Task 1 legitimately edits `execution/.../appsettings.json` (adding one `AdditionalSources` entry); stage that file but verify your diff of it contains ONLY that entry — the user's other in-file edits must ride along untouched, and if the file's diff shows anything you did not add, say so in the report instead of committing blind. Never `git add -A` or `git commit -a`.
- **No behavioral change anywhere.** Hook execution order, hook failure swallowing (post-commit failures are logged, never thrown), outbox retry/lease/dead-letter semantics — all unchanged. Observability only.
- New vnext tag constants go in `TelemetryConstants.TagNames` (`src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`), `vnext.<area>.<thing>` convention, XML `<summary>` saying what it means and when it is set. Aether-side tags follow Aether's existing lowercase dotted style (`event.name`, `outbox.message_id`).
- Public types/members get XML `<summary>` docs; comments explain WHY. Match the voice of the file you edit.
- vnext regression gate: `dotnet build vnext.sln -v q --nologo` → 0 errors; `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` and `dotnet test test/BBT.Workflow.Domain.Tests --nologo -v q` with no NEW failing name versus `/private/tmp/claude-502/-Users-U0B006-Documents-repos-burgan-tech-vnext/771178c9-bba8-4b0b-8de9-3e512a61e4ae/scratchpad/master-failures.txt` (many pre-existing failures live there; also ignore the known `CacheSetL1Tests.Second_latest_read_costs_only_the_generation_read`).
- aether regression gate: build the touched projects and run `BBT.Aether.Infrastructure.Tests`; record any pre-existing failures BEFORE your change (first run = baseline) and require no NEW failing name after.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/BBT.Workflow.Infrastructure/EventBus/HookedDistributedEventBus.cs` | Hook execution — gains the per-hook span | 1 |
| `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` | Three new tag constants | 1 |
| `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json` | Adds the `BBT.Workflow.Instances.Events` source entry | 1 |
| `test/BBT.Workflow.Application.Tests/EventBus/HookedDistributedEventBusSpanTests.cs` | Pins the hook span contract | 1 |
| `docs/runtime/trace-span-tree.md` | Span reference rows + event-chain subsection | 1, 3 |
| aether `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs` | Persists TraceParent/TraceState, tags message id | 2 |
| aether `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs` | Re-parents `Outbox.Process` into the origin trace | 2 |
| aether `framework/test/BBT.Aether.Infrastructure.Tests/...` | Pins both Aether changes | 2 |
| `docs/runtime/event-trace-chain.md` (new, vnext) | The end-to-end event-chain trace story incl. the release gate | 3 |

---

### Task 1: A span per hook invocation (vnext)

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/EventBus/HookedDistributedEventBus.cs` (the `ExecuteHooksAsync` loop, ~line 325-360, and a new static `ActivitySource` field)
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json` (`Telemetry:Tracing:AdditionalSources` gains `"BBT.Workflow.Instances.Events"`)
- Modify: `docs/runtime/trace-span-tree.md`
- Test: `test/BBT.Workflow.Application.Tests/EventBus/HookedDistributedEventBusSpanTests.cs` (create; the test project already references Infrastructure transitively — verify it compiles, and if Infrastructure is not reachable add the explicit `ProjectReference` and say so)

**Interfaces:**
- Consumes: existing `IEventHookInvoker` (`EventType`, `HookName`, `InvokeAsync`), `GetEventHookMode(Type)`, `EventHookResult.IsSuccess`.
- Produces: spans named `EventHook.{shortName}` on source `BBT.Workflow.Instances.Events`; tags `vnext.event.name`, `vnext.hook.name`, `vnext.hook.mode`.

- [ ] **Step 1: Write the failing tests**

Create `test/BBT.Workflow.Application.Tests/EventBus/HookedDistributedEventBusSpanTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Events.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.EventBus;

/// <summary>
/// Pins the per-hook span emitted by <c>HookedDistributedEventBus.ExecuteHooksAsync</c>.
/// <para>
/// Before it existed, DurablePostCommit hooks ran inside <c>Uow.Commit</c> as one
/// undifferentiated block: their remote calls emitted client spans, but nothing attributed a call
/// to a hook. The span lands under whatever is ambient — <c>Uow.Commit</c> for post-commit mode,
/// <c>Events.PublishDeferred</c> for immediate mode — so no re-parenting is involved.
/// </para>
/// </summary>
public sealed class HookedDistributedEventBusSpanTests : IDisposable
{
    private readonly List<Activity> _collected = new();
    private readonly ActivityListener _listener;

    public HookedDistributedEventBusSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "BBT.Workflow.Instances.Events",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _collected.Add
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    [EventHook(EventHookMode.Immediate)]
    private sealed class ProbeEvent { }

    private static IEventHookInvoker StubInvoker(string hookName, EventHookResult result)
    {
        var invoker = Substitute.For<IEventHookInvoker>();
        invoker.EventType.Returns(typeof(ProbeEvent));
        invoker.HookName.Returns(hookName);
        invoker.InvokeAsync(Arg.Any<object>(), Arg.Any<EventHookContext>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return invoker;
    }

    // NOTE for the implementer: the bus's constructor signature and how invokers are resolved
    // (service provider, GetServices<IEventHookInvoker>) must be taken from the real class —
    // read HookedDistributedEventBus's constructor and GetInvokersForEventType, then build the
    // ServiceProvider below to match. The ASSERTIONS are the contract; the arrangement is
    // scaffolding to correct against the real types.
    private static (object bus, Func<object, Task> publish) BuildBus(params IEventHookInvoker[] invokers)
        => throw new NotImplementedException("arrange against the real ctor while implementing");

    [Fact]
    public async Task EachHook_GetsItsOwnNamedSpan_WithModeAndOutcome()
    {
        // Two hooks on one event → two spans, each named after ITS hook, both tagged with the mode.
        // Arrangement: build the hooked bus with two stub invokers for ProbeEvent, publish one
        // ProbeEvent, then assert on _collected:
        //   - exactly 2 spans whose DisplayName starts with "EventHook."
        //   - names carry the TRIMMED hook name (e.g. "InstanceSubFaultedEventHook" → "EventHook.InstanceSubFaulted")
        //   - vnext.hook.name carries the FULL name
        //   - vnext.event.name == "ProbeEvent", vnext.hook.mode == "Immediate"
        //   - both spans report OK / unset status
        await Task.CompletedTask;
        throw new NotImplementedException("implemented alongside BuildBus");
    }

    [Fact]
    public async Task AFailedHook_ProducesAnErrorSpan_WithoutThrowing()
    {
        // One invoker returning a failed EventHookResult (and a second whose InvokeAsync throws):
        //   - the failure span has ActivityStatusCode.Error
        //   - the throwing hook's span also has Error status and the exception message
        //   - the publish call itself does NOT throw (hook failures stay swallowed) and the
        //     remaining hooks still ran (span count proves it)
        await Task.CompletedTask;
        throw new NotImplementedException("implemented alongside BuildBus");
    }

    [Fact]
    public async Task TheHookSpan_ParentsToTheAmbientActivity()
    {
        // Start an ambient activity named like the real enclosing span, publish, and assert the
        // hook span's ParentId equals that ambient activity's Id — this is the property that puts
        // post-commit hooks under Uow.Commit without any re-parenting machinery.
        await Task.CompletedTask;
        throw new NotImplementedException("implemented alongside BuildBus");
    }
}
```

The three test bodies and `BuildBus` are deliberately specified as assertions-plus-arrangement-notes: the bus's constructor and invoker resolution must be read from the real class (`HookedDistributedEventBus` resolves invokers via the service provider / `AmbientServiceProvider`), and inventing that arrangement in the plan risks pinning a wrong shape. **The listed assertions are the contract — implement all of them; a test that asserts less is a spec gap.**

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~HookedDistributedEventBusSpanTests" --nologo -v q`
Expected: FAIL (NotImplementedException at first; after arranging `BuildBus`, FAIL because no `EventHook.*` span exists yet). Report the final pre-implementation failure — the one proving the span is missing.

- [ ] **Step 3: Add the tag constants**

In `TelemetryConstants.TagNames`, next to the other `vnext.*` event constants:

```csharp
/// <summary>Short CLR name of the event whose hook is executing (e.g. <c>InstanceSubFaultedEvent</c>).</summary>
public const string EventName = "vnext.event.name";

/// <summary>Full name of the executing hook (untrimmed, e.g. <c>InstanceSubFaultedEventHook</c>).</summary>
public const string HookName = "vnext.hook.name";

/// <summary>Hook execution mode: <c>Immediate</c> (at publish, under Events.PublishDeferred) or <c>DurablePostCommit</c> (inside the UoW commit, under Uow.Commit).</summary>
public const string HookMode = "vnext.hook.mode";
```

If a constant named `EventName` already exists in that class, reuse it instead of duplicating — check first.

- [ ] **Step 4: Implement the span in `ExecuteHooksAsync`**

In `HookedDistributedEventBus`, add the source as a static field:

```csharp
/// <summary>
/// Source for per-hook execution spans. The name is deliberately the one three hosts already
/// list in Telemetry:Tracing:AdditionalSources ("BBT.Workflow.Instances.Events"), so creating
/// the source lights those hosts up without config changes; the Execution host's entry is
/// added alongside this change.
/// </summary>
private static readonly ActivitySource HookActivitySource = new("BBT.Workflow.Instances.Events");
```

Wrap the invoker loop body. The existing structure (`foreach (var invoker in invokers) { try { var result = await invoker.InvokeAsync(...); ... } catch ... }`) becomes:

```csharp
foreach (var invoker in invokers)
{
    var hookName = invoker.HookName;

    // One span per hook, named after the hook and parented to whatever is ambient — Uow.Commit
    // for DurablePostCommit (OnCompleted runs inside CommitAsync), Events.PublishDeferred for
    // Immediate. This is what attributes a hook's remote calls to the hook: those client spans
    // become THIS span's children instead of hanging directly under the commit.
    using var hookActivity = HookActivitySource.StartActivity(
        $"EventHook.{TrimHookSuffix(hookName)}",
        ActivityKind.Internal,
        Activity.Current?.Context ?? default);
    hookActivity?.SetTag(TelemetryConstants.TagNames.EventName, eventType.Name);
    hookActivity?.SetTag(TelemetryConstants.TagNames.HookName, hookName);
    hookActivity?.SetTag(TelemetryConstants.TagNames.HookMode, GetEventHookMode(eventType)?.ToString());
    hookActivity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);

    try
    {
        var result = await invoker.InvokeAsync(@event, context, cancellationToken);
        // ... existing success/metadata/failure counting stays EXACTLY as it is ...
        if (!result.IsSuccess)
            hookActivity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
    }
    catch (Exception ex)
    {
        hookActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        // ... existing catch body stays EXACTLY as it is ...
    }
}
```

Read the real loop before editing: the success/failure counting, `ExtraMetadata` merge and logging must remain byte-for-byte in behavior — you are wrapping, not rewriting. `EventHookResult`'s failure/message member names must be taken from the real type (the snippet's `IsSuccess`/`ErrorMessage` are best-effort). Add the trimming helper next to the loop:

```csharp
/// <summary>
/// Trims the conventional "EventHook"/"Hook" suffix for the span NAME only — the display name
/// reads as the subject ("EventHook.InstanceSubFaulted"), while vnext.hook.name keeps the full
/// class name for querying. Mirrors the Step-span convention of trimming the "Step" suffix.
/// </summary>
private static string TrimHookSuffix(string hookName) =>
    hookName.EndsWith("EventHook", StringComparison.Ordinal) ? hookName[..^"EventHook".Length]
    : hookName.EndsWith("Hook", StringComparison.Ordinal) ? hookName[..^"Hook".Length]
    : hookName;
```

- [ ] **Step 5: Add the Execution host's source entry**

In `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json`, `Telemetry:Tracing:AdditionalSources`, append `"BBT.Workflow.Instances.Events"`. Orchestration, Inbox and Outbox already list it — verify with grep and do NOT touch them.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~HookedDistributedEventBusSpanTests" --nologo -v q`
Expected: PASS (3 tests).

- [ ] **Step 7: Run the regression gate**

`dotnet build vnext.sln -v q --nologo` → 0 errors; both suites → no NEW failing name versus the baseline named in Global Constraints.

- [ ] **Step 8: Document**

In `docs/runtime/trace-span-tree.md`: add an `EventHook.{name}` row to the span table (source `BBT.Workflow.Instances.Events`, the three tags, and the note that its parent tells you the mode — `Uow.Commit` means post-commit, `Events.PublishDeferred` means immediate). Update the target-tree diagram's `Uow.Commit` line to show the child.

- [ ] **Step 9: Commit (vnext)**

```bash
git add src/BBT.Workflow.Infrastructure/EventBus/HookedDistributedEventBus.cs \
        src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs \
        execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json \
        test/BBT.Workflow.Application.Tests/EventBus/HookedDistributedEventBusSpanTests.cs \
        docs/runtime/trace-span-tree.md
git commit -m "feat(telemetry): a span per event hook, under the commit that runs it"
```

Before committing, `git diff --staged execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json` and confirm the only hunk is the one source entry (the user has unrelated edits in that file — they must not be reverted, and anything unexpected in the hunk gets reported, not committed).

---

### Task 2: Outbox trace continuity (aether — USER-APPROVED)

**Repo:** `/Users/U0B006/Documents/repos/burgan-tech/aether`. Local commit only; if the current branch is main/master, create local branch `feature/outbox-trace-continuity` first.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs` (`StoreAsync`, ~lines 43-65)
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs` (the per-message span, ~lines 88-95)
- Test: extend/add under `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/` — the harness model is the existing `Processing/OutboxProcessorDeadLetterTests.cs` (fake stores/bus); read it first and follow its arrangement style.

**Interfaces:**
- Consumes: `OutboxMessage.ExtraProperties` (`Dictionary<string, object>`, already round-trips `TopicName` via `TryGetValue` + `ToString()`), `Activity.Current`.
- Produces: `ExtraProperties["TraceParent"]` / `["TraceState"]` on newly stored rows; `outbox.message_id` tag on the ambient span at store time; `Outbox.Process` parented to the stored trace context when present.

- [ ] **Step 1: Baseline the aether test suite**

Run `dotnet test framework/test/BBT.Aether.Infrastructure.Tests --nologo -v q` BEFORE any change and save the failing-name list — that is your no-new-failures baseline.

- [ ] **Step 2: Write the failing tests**

Two test surfaces, modeled on `OutboxProcessorDeadLetterTests`:

**(a) Store side** — a test that calls `EfCoreOutboxStore.StoreAsync` inside an started `Activity` and asserts, on the stored `OutboxMessage`:
- `ExtraProperties["TraceParent"]` equals the ambient activity's `Id` (W3C traceparent);
- `ExtraProperties["TraceState"]` present only when the activity has one;
- the ambient activity gained tag `outbox.message_id` equal to the stored row's id;
- with NO ambient activity: no `TraceParent`/`TraceState` keys are written (absence, not nulls), and nothing throws.

**(b) Processor side** — a test that feeds the processor a message whose `ExtraProperties["TraceParent"]` is a valid W3C value from a DIFFERENT trace, with an `ActivityListener` capturing `Outbox.Process`:
- the captured span's `TraceId` equals the stored traceparent's trace id (re-parented into the origin trace), and its parent span id is the stored one;
- the span carries an `ActivityLink` to the worker-loop's ambient span (start an ambient activity around the processor call to give it one);
- a message WITHOUT `TraceParent` (or with garbage) keeps today's behavior: parented to the ambient worker span, no link, no throw;
- existing tags (`event.name`, `outbox.message_id`, `outbox.retry_count`) still present in both shapes.

`Outbox.Process` is started via `ActivitySource.StartActivity(name, kind, parentContext, links: …)` semantics — links must be supplied AT START (they cannot be added after), which will force the implementation shape in Step 3.

- [ ] **Step 3: Implement**

**`EfCoreOutboxStore.StoreAsync`** — after the `outboxMessage` object initializer (which already writes `TopicName`/`Version`/`Source`/`Subject`):

```csharp
// The drop's trace identity, persisted the same way TopicName is. The payload bytes already
// carry a TraceParent for traceable events, but the processor publishes them opaquely — these
// row-level copies are what let Outbox.Process re-join the originating trace without
// deserializing the envelope. Absent (not null) when nothing is ambient, so pre-existing rows
// and non-traced writes keep today's behavior.
if (Activity.Current is { } ambient)
{
    outboxMessage.ExtraProperties["TraceParent"] = ambient.Id!;
    if (!string.IsNullOrEmpty(ambient.TraceStateString))
        outboxMessage.ExtraProperties["TraceState"] = ambient.TraceStateString;

    // The originating trace's only chance to learn which row the event became: the id is born
    // here, and widening IOutboxStore.StoreAsync's return type for one tag is not worth the
    // ripple through every implementor. Ambient here is the EventBus.Publish span.
    ambient.SetTag("outbox.message_id", outboxMessage.Id.ToString());
}
```

**`OutboxProcessor`** — replace the per-message `StartActivity` call:

```csharp
// Re-join the originating trace when the row carries its drop identity (written by
// EfCoreOutboxStore since <this change>): the per-message span parents to the stored context
// and LINKS back to the worker loop — the same shape the inbox side's EventTraceScope uses,
// so publish → outbox drop → outbox publish → inbox handle reads as one tree. Rows without
// the identity (pre-deploy rows, untraced writes) keep the worker-loop parent unchanged.
var loopContext = Activity.Current?.Context ?? default;
ActivityContext parentContext = loopContext;
IEnumerable<ActivityLink>? links = null;
if (message.ExtraProperties.TryGetValue("TraceParent", out var tpObj) &&
    ActivityContext.TryParse(
        tpObj?.ToString(),
        message.ExtraProperties.TryGetValue("TraceState", out var tsObj) ? tsObj?.ToString() : null,
        isRemote: true,
        out var originContext))
{
    parentContext = originContext;
    if (loopContext != default)
        links = new[] { new ActivityLink(loopContext) };
}

using var activity = InfrastructureActivitySource.Source.StartActivity(
    "Outbox.Process", ActivityKind.Producer, parentContext, links: links);
```

Keep every existing tag line (`event.name`, `event.topic`, `outbox.message_id`, `outbox.retry_count`) and the whole publish/outcome flow untouched. Note `ExtraProperties` values may round-trip as `JsonElement` — that is exactly why the read goes through `?.ToString()`, the same pattern the `TopicName` read above it uses.

- [ ] **Step 4: Run the tests to verify they pass**

The new tests pass; the full `BBT.Aether.Infrastructure.Tests` run has no NEW failing name versus Step 1's baseline. Report both verbatim.

- [ ] **Step 5: Commit (aether, local)**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/
git commit -m "feat(outbox): carry the drop's trace identity and re-join Outbox.Process to it"
```

---

### Task 3: Live verification (vnext half) + the event-chain document

**Files:**
- Create: `docs/runtime/event-trace-chain.md` (vnext)
- Modify: `docs/runtime/trace-span-tree.md` (link the new page)

**What can and cannot be verified live** — say this in the document, not just here: vnext consumes Aether from nuget.org (1.0.36, no local feed), so Task 2's effect on a live trace is NOT observable until the next Aether release. Task 1's hook spans ARE observable now. The document records both, with the release gate explicit.

- [ ] **Step 1: Bring the environment up and run a hook-firing flow**

Check `docker ps` first — do NOT wholesale-restart infrastructure that is already up; if absent, `cd etc/docker && docker compose up -d`, and MockLab from `/Users/U0B006/Documents/repos/burgan-tech/vnext-example` (`docker compose up -d`). Build ONCE sequentially (`dotnet build vnext.sln -v q --nologo` — four racing `dotnet run` builds fail on PostSharp MSB4018), then start orchestration/execution/inbox/outbox staggered in the background with `--launch-profile http --no-build`, waiting for `http://localhost:4201/health` → 200. From vnext-example run a flow that completes a SUBFLOW (the `sub:*` terminal events are the DurablePostCommit hooks): `dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~FuturePayTests" --nologo -v q` (FuturePay drives bureau/collateral subflows). If it does not go green or produces no hook spans, `MoneyTransferTests` plus a manually cancelled instance is the fallback — report what you actually used.

- [ ] **Step 2: Verify the hook spans in Elastic**

Elastic `http://localhost:9200`, indices `.ds-traces-apm*,traces-apm*`, query with Python `urllib` (curl is blocked for Elastic here; parse `timestamp.us`, not `@timestamp`). Find a trace containing `Uow.Commit` with `EventHook.*` children and paste the subtree into the document:
- the hook span's parent IS the `Uow.Commit` span (post-commit mode) — this is the user's original ask, prove it with span ids;
- any client span the hook emitted (HttpClient/Dapr) is a CHILD of the hook span;
- tags `vnext.event.name` / `vnext.hook.name` / `vnext.hook.mode` present.
Also confirm the previously-working inbox continuation still holds: an `*.Handle` span in the same originating trace (regression guard for `EventTraceScope`).

- [ ] **Step 3: Write `docs/runtime/event-trace-chain.md`**

Structure: (1) the chain diagram — `Events.PublishDeferred → EventBus.Publish [outbox.message_id after Aether vNext] → Uow.Commit → EventHook.{name} → …` and `Outbox.Process` / `{Event}.Handle` on the worker side; (2) which repo owns which span; (3) the verified evidence from Step 2 (trace id + subtree); (4) **the release gate**: what only becomes true after the next Aether release (`outbox.message_id` on the origin span, `Outbox.Process` re-joined), with a pointer to the aether commit from Task 2; (5) the pre-deploy note — rows stored before the Aether change carry no `TraceParent` and keep worker-loop parenting, by design.

- [ ] **Step 4: Stop the apps, commit (vnext)**

`pkill -f "dotnet run"`, then `pkill -f "BBT.Workflow"`; leave the containers running.

```bash
git add docs/runtime/event-trace-chain.md docs/runtime/trace-span-tree.md
git commit -m "docs(runtime): the event-chain trace story, verified hook spans, Aether release gate"
```

---

## Self-Review

**Spec coverage:** ask 1 (hooks under `Uow.Commit`) → Task 1 + Task 3 Step 2 proof. Ask 2 (attributable remote calls) → same span, child-parenting asserted in Task 3. Ask 3 (drop identity + handled-in-tree) → drop identity is Task 2 (both directions: row carries TraceParent, origin span carries message id); handled-in-tree already works via `EventTraceScope` (spec Finding), regression-guarded in Task 3 Step 2. Success criterion 3's release gate → Task 3's document. No-behavior-change constraint → stated in every task; hook wrap explicitly "wrapping, not rewriting"; processor fallback keeps today's parenting.

**Known soft spots, stated rather than hidden:** (a) Task 1's `BuildBus` arrangement and `EventHookResult` member names are to be corrected against the real types — the assertions are the contract, and the step says a test asserting less is a spec gap; (b) Task 2's `ExtraProperties` round-trip type (`JsonElement` vs string) is handled by the same `?.ToString()` pattern the existing `TopicName` read uses, called out inline; (c) Task 3's flow choice (FuturePay) may need the stated fallback — the step demands reporting what actually ran.

**Type consistency:** `TrimHookSuffix` defined and used only in Task 1. Aether tag literals (`outbox.message_id`) match the processor's existing literal. `ExtraProperties` keys `TraceParent`/`TraceState` are written in Task 2's store change and read in the same task's processor change — one task, no cross-task drift possible. No task references a symbol another task did not define.
