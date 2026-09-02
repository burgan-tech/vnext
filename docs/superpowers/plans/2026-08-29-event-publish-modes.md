# Event Publish Simplification: Outbox-Only Events + Subflow Terminal Relay + Wakeup Signal

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the EventHook infrastructure entirely — every distributed event rides the transactional outbox (made near-instant by a Dapr wakeup signal); the three subflow-terminal events additionally get an immediate post-commit **relay** (a command, not an event) so parent resume keeps gap ≈ 0 on both sync and async paths.

**Architecture:** `HookedDistributedEventBus` shrinks to a trace-stamping decorator; all 7 instance events publish plainly to the outbox. The runner, after commit, hands its deferred-event list to `SubflowTerminalRelay`, which relays only `ISubflowTerminalEvent` payloads through `RoutedInstanceCommandGateway` (in-process same-domain, Dapr invocation cross-domain) — the Inbox handlers stay registered as the durable **backup**, deduplicated by the existing `ISubItemTerminalGuard`. Aether gains the wakeup signal: committed outbox writes publish a loss-tolerant nudge that wakes the Outbox worker's poller immediately; the Inbox worker gets an in-process nudge. Polling stays as the safety net.

**Tech Stack:** .NET 10, Aether SDK (local pack `1.0.38-local`), Dapr (pub/sub + service invocation), EF Core / PostgreSQL, xUnit + NSubstitute + Shouldly.

**Spec:** Embedded below (Design Summary). Conversation dates 2026-08-29/30; architecture approved by user (hook infra fully removed; 3 terminal events keep hook *behavior* via relay; all else pure outbox).

## Global Constraints

- **NO pushes to origin.** Local commits only, in BOTH repos (`vnext`, `aether`).
- Aether repo: branch from current `feature/outbox-trace-continuity` HEAD → new branch `feature/outbox-wakeup-signal`. vnext repo: stay on `feature/trace-span-tree`.
- **Hook API is deleted outright** (no `[Obsolete]` bridge): `IEventPublishHook`, `IEventHookInvoker`, `EventHookAttribute`, `EventHookMode` are runtime-internal (domain teams author JSON, they do not compile against Events.Contracts), user approved full removal. The bank's no-breaking-change policy applies to consumer-facing surfaces, which these are not.
- Logging: never raw `logger.Log*` in vnext — add `[LoggerMessage]` extensions in `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` (40xxx = events range; grep the file for the current max and use sequential unused ids). Aether may use plain ILogger (its own convention).
- vnext master test baseline has ~191 pre-existing failures (AmbientServiceProvider parallel-collection leakage). Judge success by: the test files this plan touches pass, and the failure count does not grow.
- **Solution path:** the vnext root contains BOTH `BBT.Workflow.slnx` and `vnext.sln`; bare `dotnet restore/build <repo-dir>` fails with MSB1011. ALWAYS pass `/Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx` explicitly (fallback `vnext.sln` if the SDK rejects `.slnx` — pick one, use it consistently).
- **Dirty-worktree staging discipline:** the worktree carries an unrelated modification in `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json` plus untracked files (`.superpowers/*`, `scripts/trace-profile.py`, `scripts/__pycache__/`). Before EVERY commit: `git status --short`, stage ONLY the exact files the task touched (never `git add src/`), and for `orchestration/.../appsettings.json` stage the wakeup hunk only via `git add -p`.
- **Local-version handoff caveat:** `1.0.38-local` is restorable only on this machine — acceptable for this deliberately local-only branch. Before any future push/PR, a real published Aether prerelease must replace it. Do not "fix" mid-plan.
- `dotnet build` on macOS requires `./scripts/setup-netstandard-ref.sh` to have been run once (usually already done).

## Design Summary (the spec)

### Current behavior being removed

`HookedDistributedEventBus` intercepts publishes by `EventHookMode`:
- `HandledOrFallback` (InstanceCanceledEvent, InstanceCompletedCleanupEvent, InstanceFaultedCleanupEvent, InstanceSubStateChangedEvent): hooks run INLINE pre-commit inside the transition hop; publish to outbox only on hook failure.
- `DurablePostCommit` (InstanceSubCanceledEvent, InstanceSubCompletedEvent, InstanceSubFaultedEvent): outbox row always written; hooks run in `uow.OnCompleted` INSIDE `CommitAsync`, blocking the hop. Every terminal signal is thus delivered twice by design (hook + Inbox) — `ISubItemTerminalGuard` exists to absorb the duplicates.

### Target behavior

**Publish modes (final taxonomy — two rows):**

| Mode | Declared by | Behavior |
|---|---|---|
| **Outbox** (default, ALL events) | nothing | transactional outbox row → wakeup nudge → Outbox worker publish → broker → Inbox worker (in-process nudge) → Inbox handler |
| **Outbox + TerminalRelay** | implementing `ISubflowTerminalEvent` | everything above PLUS: the runner relays the event as a **command** immediately after commit via `SubflowTerminalRelay` → `RoutedInstanceCommandGateway`; the Inbox handler is demoted to durable backup (idempotent via `ISubItemTerminalGuard`) |

**Event classification:**

| Event | Mode |
|---|---|
| `InstanceSubCompletedEvent`, `InstanceSubFaultedEvent`, `InstanceSubCanceledEvent` | Outbox + TerminalRelay (all three — same terminal-settlement semantics, one code path) |
| `InstanceCanceledEvent`, `InstanceCompletedCleanupEvent`, `InstanceFaultedCleanupEvent`, `InstanceSubStateChangedEvent`, `ChildSubflow*`, `TransitionContinuationRequested` | Pure Outbox |

**Relay semantics:**
- Runner, after `uow.CommitAsync`, calls `subflowTerminalRelay.RelayAsync(coreOutput.DeferredEvents, ct)`. The relay selects `ISubflowTerminalEvent` payloads and processes them **sequentially** (a hop produces at most one terminal event by domain construction — terminal outcomes are exclusive, pinned by `SubItemTerminalProbe.Conflict`; the loop is defensive).
- Per event: map to the gateway input (the exact mapping code moved verbatim from today's hooks) → `CompleteAsync` / `FaultAsync` / `CancelAsync` on `IInstanceCommandGateway` → routed in-process (same domain) or Dapr service invocation (cross domain). `CallerMode = evt.Sync ? ExecMode.Sync : ExecMode.Async` — identical to today.
- **Sync chain stays sync end-to-end:** the relay is awaited before the stage returns, so the blocked caller's response follows the settled chain, exactly like today's hook. **Async chain relays immediately** in the same job execution.
- **Failure semantics:** relay exceptions are logged and swallowed — the child is already committed as Completed; the response must not lie. The outbox row (written pre-commit, unconditionally) makes the Inbox backup pick the work up (~100–300 ms with the wakeup, worst one idle poll interval). Relay calls are bounded by the gateway's existing invocation timeouts.

**Latency (unvalidated design budget — measured in Faz C):**

| Path | Parent-resume gap |
|---|---|
| Sync (any domain) | 0 — relay awaited before response |
| Async + same domain | ≈ 0 — inline in the same job, post-commit |
| Async + cross domain | ~10–30 ms — direct service invocation (never the outbox loop) |
| Crash between commit and relay (rare) | Inbox backup: ~100–300 ms; lost-nudge tail = one idle poll (currently 5 s idle / 10 s max in vnext config — tunable knob, deliberately unchanged in this plan) |

**Independence guarantees (relay vs outbox modes):**
1. Path independence: relay never touches the outbox table, broker, or workers; worker/broker outage does not affect the relay, and relay failure does not affect the outbox flow (row already committed).
2. Order independence: the relay may finish before the row is even published; the later Inbox delivery is absorbed by the guard (`AlreadySettled`). Pure-outbox events flow at their own pace; a stale `InstanceSubStateChangedEvent` arriving after completion is rejected by the existing monotonic `SubFlowStateChangedAt` guard (`SubflowStateService`).
3. Failure independence: no relay/outbox failure faults the child instance; each mechanism has its own retry (relay → backup; outbox → processor retry; inbox → redelivery).
4. Resource independence: the relay does NOT mark/suppress the outbox row — the event stays published as a fact (future domain event-triggers on `instance.sub.*` topics keep working); the duplicate costs one guard probe.

**Wakeup signal (Aether; user decision: Dapr event, NOT pg_notify; fires only when an outbox write actually happened):**
- `EfCoreOutboxStore.StoreAsync` → `OutboxWakeupCoordinator.OnOutboxMessageStored()`: one `uow.OnCompleted` registration per UoW (ConditionalWeakTable dedupe) whose callback returns immediately and publishes `OutboxWakeupEvent` as a detached task with a 2 s timeout (fire-and-forget by contract; failures logged). No ambient UoW → immediate best-effort send.
- Outbox worker subscribes (bespoke `/dapr/subscribe` + `/internal/outbox-wakeup`) and signals `IPollingWakeSignal<IOutboxProcessor>`; `OutboxBackgroundService` awaits the signal instead of `Task.Delay` (interval as timeout = polling safety net), including the startup offset.
- Inbox worker: `EventsController.ProcessEventAsync` signals `IPollingWakeSignal<IInboxProcessor>` after storing the row (same process).

**Observability model:**
- Relay span: `Subflow.TerminalRelay` via `PipelineStepActivityHelper.StartOperationActivity` (same source as `Events.PublishDeferred` / `Uow.Commit` — hosts already list it). Tags: event name, sub/parent instance ids, `vnext.relay.route = local|remote`, `vnext.relay.sync`, `vnext.relay.outcome = relayed|failed|skipped`. Per trace_refactor.md this is a synchronous command → parent-child in the SAME trace, correctly.
- Inbox backup role: the three sub-terminal Inbox handlers tag their activity `vnext.delivery.role = backup` (+ existing guard outcome). Health signal: backup deliveries that actually SETTLE (relay missed) vs `already-settled` noise.
- WorkflowLogs: relayed / relay-failed entries (40xxx).

**What gets deleted (inventory — verified by grep):** 7 hook classes (`src/BBT.Workflow.Infrastructure/Instances/Events/*EventHook.cs`); `EventHookMode.cs`, `EventHookAttribute.cs`, `IEventPublishHook.cs`, `IEventHookInvoker.cs` (Events.Contracts/Events/Hooks); `EventHookServiceCollectionExtensions.cs`; the 7 `AddEventHook` registrations in `WorkflowInfrastructureModuleServiceCollectionExtensions.cs`; all hook logic inside `HookedDistributedEventBus` (class renamed `TraceStampingDistributedEventBus`, keeps only `StampTraceContext` + delegation); `[EventHook(...)]` attribute lines + hook doc-comments on the 7 event classes; test files `HookedDistributedEventBusTests.cs` and `HookedDistributedEventBusSpanTests.cs` (replaced by slim stamping tests).

*(REVIEW P2 accepted — verified: `.github/workflows/build-and-publish-images.yml` pushes nupkg artifacts, so `BBT.Workflow.Events.Contracts` IS published and repo-internal grep cannot prove the absence of external consumers. Resolution: Task B5 gains a mandatory **Step 0 gate** — confirm with the user / internal feed usage that no external project references the hook types before deleting them; if any consumer exists, the four contract files stay as inert `[Obsolete]` shells for one release while all implementations/registrations are still removed.)*

**Accepted risks (user-approved; documented, not fixed here):**
- Outbox/Inbox workers become the mandatory path for the four pure-outbox instance events (today a successful hook needed no worker). For sub-terminal events the relay keeps subflow progression alive even with workers down; workers remain tier-1 critical for the rest — Helm replicas/probes/alerting note goes in the doc (do NOT edit vnext-helm-charts).
- Throughput: hook-success previously wrote ZERO queue rows; now every event writes outbox+inbox rows and crosses the broker. `InstanceSubStateChangedEvent` is the hottest. Measured in Faz C load test.
- Rolling upgrade: old nodes still run hooks while new nodes relay — both paths are idempotent via the guard; in-flight messages process unchanged.

*(REVIEW P1 accepted — VERIFIED in code: `InstanceCancellationService.ProcessCancellationAsync` catches per-job `TryCancelInSchedulerAsync` exceptions, logs, and still returns `Result.Ok()` — with the Inbox as sole processor the message would be ACKed and uncancelled jobs stranded. Resolution: new **Task B5b** makes partial cleanup failures retryable — winners stay persisted via `MarkManyAsProcessedAsync`, the method returns a transient-mapped `Result.Fail`, the endpoint returns 5xx, the forwarder rethrows, the Inbox redelivers, and the retry only touches the remaining active jobs. Covers canceled + completed-cleanup + faulted-cleanup, which all funnel through this method.)*

Existing guard confirmed (no task needed): out-of-order `InstanceSubStateChangedEvent` is rejected by `SubflowStateService` via `SubFlowStateChangedAt` monotonic comparison (src/BBT.Workflow.Application/SubFlow/Services/SubflowStateService.cs:71-91).

---

# FAZ A — Aether (repo: `/Users/U0B006/Documents/repos/burgan-tech/aether`)

### Task A0: Branch

**Files:** none (git only)

- [ ] **Step 1: Create branch from current HEAD**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/aether checkout -b feature/outbox-wakeup-signal
```

Expected: on new branch, clean status.

### Task A1: PollingWakeSignal primitive

**Files:**
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Polling/IPollingWakeSignal.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Polling/PollingWakeSignal.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/Polling/PollingWakeSignalTests.cs` (create dir if absent; follow the project's existing test-class conventions)

**Interfaces:**
- Produces: `IPollingWakeSignal<TMarker>` with `void Signal()` and `Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)` (returns `true` when woken by a signal, `false` on timeout). Consumed by Tasks A2, A3, A4, B7.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Polling;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.Polling;

public sealed class PollingWakeSignalTests
{
    private interface IMarker;

    [Fact]
    public async Task WaitAsync_ReturnsTrue_WhenSignaled()
    {
        var sut = new PollingWakeSignal<IMarker>();
        sut.Signal();
        (await sut.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    [Fact]
    public async Task WaitAsync_ReturnsFalse_OnTimeout()
    {
        var sut = new PollingWakeSignal<IMarker>();
        (await sut.WaitAsync(TimeSpan.FromMilliseconds(50))).ShouldBeFalse();
    }

    [Fact]
    public async Task Signal_IsCoalesced_NotAccumulated()
    {
        var sut = new PollingWakeSignal<IMarker>();
        sut.Signal();
        sut.Signal(); // must not throw, must not stack
        (await sut.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await sut.WaitAsync(TimeSpan.FromMilliseconds(50))).ShouldBeFalse();
    }

    [Fact]
    public async Task WaitAsync_Honors_Cancellation()
    {
        var sut = new PollingWakeSignal<IMarker>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test /Users/U0B006/Documents/repos/burgan-tech/aether/framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~PollingWakeSignalTests"
```

Expected: compile error — `PollingWakeSignal` not defined.

- [ ] **Step 3: Implement**

`IPollingWakeSignal.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Polling;

/// <summary>
/// A coalescing wake signal for adaptive polling loops. Producers call <see cref="Signal"/> when
/// new work becomes available; the polling loop awaits <see cref="WaitAsync"/> with its normal
/// interval as the timeout so a signal cuts the wait short while polling remains the safety net.
/// The marker type parameter distinguishes independent loops (e.g. outbox vs inbox) in DI.
/// </summary>
/// <typeparam name="TMarker">Marker type identifying the loop this signal wakes.</typeparam>
public interface IPollingWakeSignal<TMarker>
{
    /// <summary>Wakes the loop. Multiple pending signals coalesce into one.</summary>
    void Signal();

    /// <summary>
    /// Waits until <see cref="Signal"/> is called or the timeout elapses.
    /// Returns true when woken by a signal, false on timeout.
    /// </summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
```

`PollingWakeSignal.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Polling;

/// <summary>
/// Default <see cref="IPollingWakeSignal{TMarker}"/> over a bounded <see cref="SemaphoreSlim"/>(0,1):
/// signals coalesce (a second Signal while one is pending is a no-op), so a burst of producers
/// causes exactly one early wake.
/// </summary>
public sealed class PollingWakeSignal<TMarker> : IPollingWakeSignal<TMarker>
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Signal()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending — coalesce.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _semaphore.WaitAsync(timeout, cancellationToken);
}
```

- [ ] **Step 4: Run test to verify it passes** (same command as Step 2; expected 4 PASS)

- [ ] **Step 5: Commit (local only)**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/aether add framework/src/BBT.Aether.Core/BBT/Aether/Polling framework/test/BBT.Aether.Infrastructure.Tests/Polling
git -C /Users/U0B006/Documents/repos/burgan-tech/aether commit -m "feat(polling): add coalescing IPollingWakeSignal for adaptive polling loops"
```

### Task A2: OutboxWakeupEvent + notifier + coordinator + store hook

**Files:**
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/OutboxWakeupEvent.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxWakeupNotifier.cs`
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/DaprOutboxWakeupNotifier.cs`
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/OutboxWakeupCoordinator.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs` (add `WakeupSignalEnabled`, default false)
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/AetherOutboxServiceCollectionExtensions.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/Polling/OutboxWakeupCoordinatorTests.cs`

**Interfaces:**
- Consumes: `ITopicNameStrategy.GetTopicName(Type)`, `AetherEventBusOptions.PubSubName`, `IUnitOfWorkManager.Current`, `IUnitOfWork.OnCompleted(Func<IUnitOfWork,Task>)`, `DaprClient`.
- Produces: `OutboxWakeupEvent` (`[EventName("aether.outbox.wakeup")]`, empty class) — topic name consumed by Task B7. `IOutboxWakeupNotifier { Task NotifyAsync(CancellationToken cancellationToken = default); }`. `OutboxWakeupCoordinator.OnOutboxMessageStored()`. Option `AetherOutboxOptions.WakeupSignalEnabled`.

- [ ] **Step 1: Event + notifier contract**

`OutboxWakeupEvent.cs`:

```csharp
namespace BBT.Aether.Events;

/// <summary>
/// Loss-tolerant wake nudge published directly to pub/sub (never through the outbox) after a unit
/// of work that stored at least one outbox message commits. Subscribing outbox processors treat it
/// as "poll now"; the payload is deliberately empty and delivery is best-effort — the adaptive
/// polling interval remains the safety net for lost or early signals.
/// </summary>
[EventName("aether.outbox.wakeup")]
public sealed class OutboxWakeupEvent;
```

`IOutboxWakeupNotifier.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes the <see cref="OutboxWakeupEvent"/> nudge. Implementations must be fire-and-forget
/// safe: a failed notify is swallowed by callers because polling backstops delivery.
/// </summary>
public interface IOutboxWakeupNotifier
{
    Task NotifyAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Dapr notifier** (mirror `DaprEventBus`'s existing DaprClient/options injection style — open that file first and copy its exact pattern; if `AetherEventBusOptions` is injected as `IOptions<>`, match it):

```csharp
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes <see cref="OutboxWakeupEvent"/> straight to the configured pub/sub component,
/// bypassing the outbox by design (the nudge must not create the work it announces).
/// </summary>
public sealed class DaprOutboxWakeupNotifier(
    DaprClient daprClient,
    ITopicNameStrategy topicNameStrategy,
    AetherEventBusOptions eventBusOptions) : IOutboxWakeupNotifier
{
    private readonly string _topic = topicNameStrategy.GetTopicName(typeof(OutboxWakeupEvent));

    public Task NotifyAsync(CancellationToken cancellationToken = default)
        => daprClient.PublishEventAsync(
            eventBusOptions.PubSubName,
            _topic,
            new OutboxWakeupEvent(),
            cancellationToken);
}
```

- [ ] **Step 3: Option** — in `AetherOutboxOptions.cs`:

```csharp
/// <summary>
/// When true, a unit of work that stored outbox messages publishes a direct pub/sub wake nudge
/// (<see cref="OutboxWakeupEvent"/>) after commit so outbox processors poll immediately instead
/// of waiting out the idle interval. Default false. Requires an IOutboxWakeupNotifier registration.
/// </summary>
public bool WakeupSignalEnabled { get; set; }
```

- [ ] **Step 4: Coordinator (testable; notify is truly fire-and-forget with bounded timeout + log)**

`OutboxWakeupCoordinator.cs`:

```csharp
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events;

/// <summary>
/// Decides when the outbox wakeup nudge fires: once per unit of work that stored at least one
/// outbox message, from the UoW's OnCompleted callback — but WITHOUT extending the commit path:
/// the callback returns immediately and the pub/sub publish runs as an unobserved task with a
/// bounded timeout. A lost or failed nudge is logged and absorbed by the polling safety net.
/// </summary>
public sealed class OutboxWakeupCoordinator(
    AetherOutboxOptions options,
    IUnitOfWorkManager? unitOfWorkManager = null,
    IOutboxWakeupNotifier? wakeupNotifier = null,
    ILogger<OutboxWakeupCoordinator>? logger = null)
{
    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(2);
    private static readonly ConditionalWeakTable<IUnitOfWork, object> WakeupRegistered = new();
    private static readonly object RegisteredSentinel = new();

    /// <summary>Call once per stored outbox message; registration collapses to one per UoW.</summary>
    public void OnOutboxMessageStored()
    {
        if (wakeupNotifier is null || !options.WakeupSignalEnabled)
            return;

        var uow = unitOfWorkManager?.Current;
        if (uow is null)
        {
            // No ambient UoW: the caller flushes on its own SaveChanges, which this coordinator
            // cannot observe — the nudge may land BEFORE the row is visible. This branch is an
            // early best-effort hint EXCLUDED from the latency guarantee (the row then waits for
            // normal polling). Every vnext transition path runs with an ambient UoW, so this is
            // never the latency-critical path.
            NotifyFireAndForget();
            return;
        }

        lock (RegisteredSentinel)
        {
            if (WakeupRegistered.TryGetValue(uow, out _))
                return;
            WakeupRegistered.Add(uow, RegisteredSentinel);
        }

        // OnCompleted callbacks are awaited inside CommitAsync — return a completed task and let
        // the publish run detached so a slow sidecar can never stretch the commit path.
        uow.OnCompleted(_ =>
        {
            NotifyFireAndForget();
            return Task.CompletedTask;
        });
    }

    private void NotifyFireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(NotifyTimeout);
                await wakeupNotifier!.NotifyAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Outbox wakeup nudge failed or timed out; polling will pick the work up");
            }
        });
    }
}
```

In `EfCoreOutboxStore.cs`: extend the **primary constructor** with one trailing optional param (`OutboxWakeupCoordinator? wakeupCoordinator = null`) — keep the back-compat constructor delegating with null. At the end of `StoreAsync` (after `AddAsync`) add:

```csharp
wakeupCoordinator?.OnOutboxMessageStored();
```

*(REVIEW P2 accepted — resolved as a documented contract, not a mechanism change: the no-ambient-UoW branch is an **early best-effort hint explicitly excluded from the latency guarantee** (an early nudge can race the caller's own SaveChanges; the row then waits for normal polling). Every vnext transition path runs with an ambient UoW, so this branch is not on the latency-critical path. Update the code comment in `NotifyFireAndForget`'s caller branch to say exactly this, and Task B9's doc carries the same sentence under the wakeup section.)*

- [ ] **Step 5: Coordinator unit tests** — `OutboxWakeupCoordinatorTests.cs` with substitutes for `IUnitOfWorkManager`/`IUnitOfWork`/`IOutboxWakeupNotifier` covering exactly:
  1. `WakeupSignalEnabled = false` → no OnCompleted registration, no notify.
  2. Two `OnOutboxMessageStored()` calls under the same UoW → exactly ONE `OnCompleted` registration (capture with `Arg.Do`).
  3. Invoking the captured OnCompleted callback → notifier called within a short poll-wait (`Task.Delay` loop up to 1 s — the publish is detached).
  4. Notifier that throws → callback still returns normally, nothing propagates.
  5. No ambient UoW (`Current` returns null) → notifier called without any registration.
  6. Rollback needs no test: the coordinator only registers `OnCompleted`, which Aether fires solely on successful commit.

- [ ] **Step 6: Registration** — in `AddAetherOutbox` (follow the file's existing style — it already builds an options instance to decide registrations):

```csharp
// Scoped, matching the scoped EfCoreOutboxStore + scoped IUnitOfWorkManager it consumes
// (singleton would be a captive dependency). Per-UoW dedupe survives across scoped instances
// because the registration table is static.
services.TryAddScoped<OutboxWakeupCoordinator>();
if (outboxOptions.WakeupSignalEnabled)
{
    services.TryAddSingleton<IOutboxWakeupNotifier, DaprOutboxWakeupNotifier>();
}
services.TryAddSingleton<
    BBT.Aether.Polling.IPollingWakeSignal<IOutboxProcessor>,
    BBT.Aether.Polling.PollingWakeSignal<IOutboxProcessor>>();
```

*(REVIEW P1 accepted — VERIFIED: Aether registers `IUnitOfWorkManager` via `TryAddScoped` and `IOutboxStore` via `AddScoped`; a singleton coordinator would be a captive-dependency/scope-validation failure. Resolution applied to Step 6 below: the coordinator is registered **scoped** (`TryAddScoped<OutboxWakeupCoordinator>()`), matching the store that consumes it; per-UoW dedupe is unaffected because `WakeupRegistered`/`RegisteredSentinel` are `static`. The notifier stays singleton (stateless over DaprClient). Add to Step 7: run the Infrastructure tests AND build one host with `ValidateScopes` enabled (the Aether TestBase/host defaults — verify how existing tests validate scopes and reuse that harness) to prove resolution.)*

If inbox registration lives in a separate `AddAetherInbox` (check sibling files), register `IPollingWakeSignal<IInboxProcessor>` there the same way.

- [ ] **Step 7: Build + test**

```bash
dotnet build /Users/U0B006/Documents/repos/burgan-tech/aether/framework/src/BBT.Aether.Infrastructure
dotnet test /Users/U0B006/Documents/repos/burgan-tech/aether/framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxWakeupCoordinatorTests"
```

- [ ] **Step 8: Commit**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/aether add -A framework/src framework/test
git -C /Users/U0B006/Documents/repos/burgan-tech/aether commit -m "feat(outbox): publish OutboxWakeupEvent nudge after commits that stored outbox messages"
```

### Task A3: Wake-aware polling loops (incl. startup offset)

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxBackgroundService.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/Polling/WakeAwarePollingTests.cs`

**Interfaces:**
- Consumes: `IPollingWakeSignal<IOutboxProcessor>` / `IPollingWakeSignal<IInboxProcessor>` from A1/A2.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.Polling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.Polling;

public sealed class WakeAwarePollingTests
{
    [Fact]
    public async Task OutboxService_PollsImmediately_WhenSignaled()
    {
        var processed = new SemaphoreSlim(0);
        var processor = Substitute.For<IOutboxProcessor>();
        processor.RunAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { processed.Release(); return Task.FromResult(0); });

        var options = new AetherOutboxOptions
        {
            IdlePollingInterval = TimeSpan.FromSeconds(30),
            MaxPollingInterval = TimeSpan.FromSeconds(30),
            BusyPollingInterval = TimeSpan.FromMilliseconds(100)
        };
        var signal = new PollingWakeSignal<IOutboxProcessor>();
        var sut = new OutboxBackgroundService(
            processor, options, NullLogger<OutboxBackgroundService>.Instance, signal);

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        try
        {
            // Startup offset is also wake-aware: signal now, first run must happen fast.
            signal.Signal();
            (await processed.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
            // With a 30s idle interval, only a signal can trigger the next run this fast.
            signal.Signal();
            (await processed.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        }
        finally
        {
            cts.Cancel();
            await sut.StopAsync(CancellationToken.None);
        }
    }
}
```

(Check `PollingDelay.StartupOffset` semantics first; if it derives from `IdlePollingInterval`, the 5 s allowance holds only because the startup wait is wake-aware after this change — which is exactly what the first assertion pins.)

- [ ] **Step 2: Run to verify it fails** (no 4th ctor param yet)

```bash
dotnet test /Users/U0B006/Documents/repos/burgan-tech/aether/framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~WakeAwarePollingTests"
```

- [ ] **Step 3: Implement**

`OutboxBackgroundService`: add optional ctor param:

```csharp
public sealed class OutboxBackgroundService(
    IOutboxProcessor processor,
    AetherOutboxOptions options,
    ILogger<OutboxBackgroundService> logger,
    BBT.Aether.Polling.IPollingWakeSignal<IOutboxProcessor>? wakeSignal = null) : BackgroundService
```

Replace the loop's interval wait:

```csharp
// A wake signal cuts the interval short; timeout keeps polling as the safety net.
if (wakeSignal is null)
    await Task.Delay(PollingDelay.Jitter(delay), stoppingToken).ConfigureAwait(false);
else
    await wakeSignal.WaitAsync(PollingDelay.Jitter(delay), stoppingToken).ConfigureAwait(false);
```

And the startup offset (inside its existing try/catch):

```csharp
// Startup offset: also wake-aware, so a nudge that lands during a rolling restart advances the
// first poll instead of waiting the offset out.
if (wakeSignal is null)
    await Task.Delay(PollingDelay.StartupOffset(options.IdlePollingInterval), stoppingToken).ConfigureAwait(false);
else
    await wakeSignal.WaitAsync(PollingDelay.StartupOffset(options.IdlePollingInterval), stoppingToken).ConfigureAwait(false);
```

Apply the identical two changes to `InboxBackgroundService` with `IPollingWakeSignal<IInboxProcessor>?` (open the file; its loop mirrors the outbox one — adapt to its own options type `AetherInboxOptions`).

- [ ] **Step 4: Run test to verify it passes** (same command)

- [ ] **Step 5: Commit**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/aether add -A framework
git -C /Users/U0B006/Documents/repos/burgan-tech/aether commit -m "feat(polling): outbox/inbox background services wake early on IPollingWakeSignal"
```

### Task A4: Inbox in-process nudge on delivery

**Files:**
- Modify: `framework/src/BBT.Aether.AspNetCore/BBT/Aether/AspNetCore/Events/EventsController.cs`

**Interfaces:**
- Consumes: `IPollingWakeSignal<IInboxProcessor>` (resolved from `HttpContext.RequestServices` — no ctor change, so subclasses stay source-compatible).

- [ ] **Step 1: Implement** — in `ProcessEventAsync`, immediately after `await uow.CommitAsync(cancellationToken);` (before `return Ok();`):

```csharp
// Same-process nudge: the inbox poller and this delivery endpoint share the host,
// so a stored row can be processed immediately instead of waiting out the idle interval.
HttpContext.RequestServices
    .GetService<BBT.Aether.Polling.IPollingWakeSignal<IInboxProcessor>>()
    ?.Signal();
```

Add `using Microsoft.Extensions.DependencyInjection;` if missing. Confirm `IInboxProcessor` namespace (`BBT.Aether.Events`).

- [ ] **Step 2: Build + run Aether AspNetCore tests** (capture the baseline BEFORE editing)

```bash
dotnet build /Users/U0B006/Documents/repos/burgan-tech/aether/framework/src/BBT.Aether.AspNetCore
dotnet test /Users/U0B006/Documents/repos/burgan-tech/aether/framework/test/BBT.Aether.AspNetCore.Tests
```

Expected: build success; results no worse than baseline.

- [ ] **Step 3: Commit**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/aether add -A framework/src/BBT.Aether.AspNetCore
git -C /Users/U0B006/Documents/repos/burgan-tech/aether commit -m "feat(inbox): signal the inbox poller when a delivered event is stored"
```

### Task A5: Pack local package `1.0.38-local`

**Files:** none (pack output only)

- [ ] **Step 1: Full framework build + tests**

```bash
dotnet build /Users/U0B006/Documents/repos/burgan-tech/aether/framework
dotnet test /Users/U0B006/Documents/repos/burgan-tech/aether/framework/test/BBT.Aether.Infrastructure.Tests
```

- [ ] **Step 2: Pack to a local feed**

```bash
mkdir -p /Users/U0B006/Documents/repos/burgan-tech/aether/.local-feed
dotnet pack /Users/U0B006/Documents/repos/burgan-tech/aether/framework -p:PackageVersion=1.0.38-local -o /Users/U0B006/Documents/repos/burgan-tech/aether/.local-feed
```

If directory-level pack fails, pack each `framework/src/*` project individually with the same flags. Verify the feed contains every package id vnext references (grep `AetherPackageVersion` across vnext `*.csproj` for the exact list — at minimum Abstractions, Core, Domain, Infrastructure, AspNetCore, Aspects, Npgsql, Application, HttpClient, AutoMapper, Mapperly, TestBase).

- [ ] **Step 3: Keep the feed out of git**

```bash
echo ".local-feed/" >> /Users/U0B006/Documents/repos/burgan-tech/aether/.git/info/exclude
```

---

# FAZ B — vnext (repo: `/Users/U0B006/Documents/repos/burgan-tech/vnext`, branch `feature/trace-span-tree`)

### Task B1: Consume Aether 1.0.38-local

**Files:**
- Modify: `Directory.Build.props:5` (`AetherPackageVersion` 1.0.37 → 1.0.38-local)

- [ ] **Step 1: Bump version** — `<AetherPackageVersion>1.0.38-local</AetherPackageVersion>`.

- [ ] **Step 2: Restore with the local feed (hydrates the global cache; later plain builds work)**

```bash
dotnet restore /Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx -s /Users/U0B006/Documents/repos/burgan-tech/aether/.local-feed -s https://api.nuget.org/v3/index.json
dotnet build /Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx
```

- [ ] **Step 3: Commit**

```bash
git -C /Users/U0B006/Documents/repos/burgan-tech/vnext add Directory.Build.props
git -C /Users/U0B006/Documents/repos/burgan-tech/vnext commit -m "chore: bump Aether to 1.0.38-local (outbox wakeup signal + polling wake)"
```

### Task B2: Contract — `ISubflowTerminalEvent` marker on the three terminal events

**Files:**
- Create: `src/BBT.Workflow.Events.Contracts/Events/ISubflowTerminalEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCompletedEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubFaultedEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCanceledEvent.cs`

**Interfaces:**
- Produces (consumed by B3, B5):

```csharp
namespace BBT.Workflow.Events;

/// <summary>
/// Declares the "Outbox + TerminalRelay" publish mode: the event still rides the transactional
/// outbox as a durable fact (Inbox handler = backup, deduplicated by ISubItemTerminalGuard), and
/// the transition runner ADDITIONALLY relays it as an immediate post-commit command so the parent
/// settles with gap ≈ 0 — inline for the same domain, one Dapr invocation across domains. The
/// marker interface IS the mode declaration: the terminal set is closed by the subflow protocol,
/// so no attribute/enum registry is warranted.
/// </summary>
public interface ISubflowTerminalEvent
{
    /// <summary>Target (parent) domain the terminal processing routes to.</summary>
    string Domain { get; }

    /// <summary>True when the originating chain executes synchronously end-to-end.</summary>
    bool Sync { get; }

    /// <summary>Parent instance the relay settles.</summary>
    Guid InstanceId { get; }

    /// <summary>Terminal child instance.</summary>
    Guid SubInstanceId { get; }
}
```

- [ ] **Step 1: Create the interface** (code above).

- [ ] **Step 2: Implement on the three events** — add `ISubflowTerminalEvent` to each class's interface list. All three already expose `Domain` and `Sync`; verify `InstanceId`/`SubInstanceId` property names on each (open the files) and add explicit interface implementations where a name differs, e.g. `Guid ISubflowTerminalEvent.InstanceId => ParentInstanceId;`. Do NOT rename existing properties.

- [ ] **Step 3: Build** — `dotnet build src/BBT.Workflow.Events.Contracts`

- [ ] **Step 4: Commit**

```bash
git add src/BBT.Workflow.Events.Contracts/Events/ISubflowTerminalEvent.cs src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCompletedEvent.cs src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubFaultedEvent.cs src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCanceledEvent.cs
git commit -m "feat(events): ISubflowTerminalEvent marker declaring the Outbox+TerminalRelay mode"
```

### Task B3: SubflowTerminalRelay service

**Files:**
- Create: `src/BBT.Workflow.Application/SubFlow/Services/ISubflowTerminalRelay.cs`
- Create: `src/BBT.Workflow.Application/SubFlow/Services/SubflowTerminalRelay.cs`
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs` (register scoped, next to the `ITransitionRunner` registration)
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` (relay log extensions)
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` (new tag names)
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowTerminalRelayTests.cs`

**Interfaces:**
- Consumes: `ISubflowTerminalEvent` (B2), `IInstanceCommandGateway.CompleteAsync/FaultAsync/CancelAsync` (existing), `DomainEventEnvelope.Event` (Aether), `PipelineStepActivityHelper.StartOperationActivity` (existing, Application), mapping bodies moved verbatim from the three hooks (`src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSub{Completed,Faulted,Canceled}EventHook.cs` — `MapToFlowCompletedInput`, `MapToSubFlowFaultedInput`, `Map`→`SubItemCanceledInput`).
- Produces (consumed by B5 runner integration):

```csharp
namespace BBT.Workflow.SubFlow;

/// <summary>
/// Post-commit command relay for subflow terminal events (Outbox + TerminalRelay mode).
/// Selects ISubflowTerminalEvent payloads from the hop's deferred events and settles the parent
/// immediately through the routed gateway — inline for the same domain, one Dapr service
/// invocation across domains. CallerMode follows the event's Sync flag, so a sync chain stays
/// sync end-to-end. Failures are logged and swallowed: the child's commit already stands, and the
/// event's outbox row guarantees the Inbox backup settles the parent shortly after.
/// </summary>
public interface ISubflowTerminalRelay
{
    Task RelayAsync(IReadOnlyList<DomainEventEnvelope> deferredEvents, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class SubflowTerminalRelayTests
{
    // Helper: build a DomainEventEnvelope for an event instance. Open Aether's DomainEventEnvelope
    // (BBT.Aether.Core/BBT/Aether/Events/DomainEventEnvelope.cs) — ctor is (IDistributedEvent, EventMetadata);
    // construct EventMetadata the way the existing runner/tests do (grep for its usage in vnext tests).
    private static DomainEventEnvelope Envelope(IDistributedEvent evt) => /* per above */;

    [Fact]
    public async Task Relays_SubCompleted_Through_Gateway_Complete()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput()));  // adapt to CompleteAsync's REAL return type
        var sut = new SubflowTerminalRelay(gateway, NullLogger<SubflowTerminalRelay>.Instance);

        var evt = new InstanceSubCompletedEvent { /* fill ALL required members incl. Sync = true */ };
        await sut.RelayAsync([Envelope(evt)], CancellationToken.None);

        await gateway.Received(1).CompleteAsync(
            Arg.Is<FlowCompletedInput>(i => i.Sync && i.SubInstanceId == evt.SubInstanceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relays_SubFaulted_And_SubCanceled_To_Their_Gateway_Methods()
    {
        // same pattern: FaultAsync for InstanceSubFaultedEvent, CancelAsync for InstanceSubCanceledEvent
    }

    [Fact]
    public async Task Ignores_NonTerminal_Events()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var sut = new SubflowTerminalRelay(gateway, NullLogger<SubflowTerminalRelay>.Instance);

        var evt = new InstanceSubStateChangedEvent { /* required members */ };
        await sut.RelayAsync([Envelope(evt)], CancellationToken.None);

        gateway.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Gateway_Failure_Is_Swallowed_And_Logged()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<TransitionOutput>>>(_ => throw new InvalidOperationException("boom"));
        var sut = new SubflowTerminalRelay(gateway, NullLogger<SubflowTerminalRelay>.Instance);

        var evt = new InstanceSubCompletedEvent { /* required members */ };
        await Should.NotThrowAsync(() => sut.RelayAsync([Envelope(evt)], CancellationToken.None));
    }

    [Fact]
    public async Task Gateway_ResultFail_Is_Swallowed_And_Logged()
    {
        // CompleteAsync returns Result.Fail(...) → RelayAsync completes normally (backup covers).
    }
}
```

Adapt the substitutes to the REAL gateway method signatures (open `IInstanceCommandGateway`) and the events' `required` members — no invented members. NOTE: the relay ctor also takes `IRuntimeInfoProvider` — pass a substitute (`IsDomainMatch` → `true`) in every test above, and add a sixth test `Tags_RelayRoute_Local_And_Remote` asserting the `vnext.relay.route` tag for a matching and a non-matching domain (capture spans with an `ActivityListener`, the way the old `HookedDistributedEventBusSpanTests` did — reuse its listener setup before that file is deleted in B5).

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowTerminalRelayTests"
```

- [ ] **Step 3: Implement the relay**

```csharp
using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Workflow.Events;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Default <see cref="ISubflowTerminalRelay"/>. Processes terminal events SEQUENTIALLY: a hop
/// produces at most one terminal event by domain construction (terminal outcomes are exclusive —
/// pinned by SubItemTerminalProbe.Conflict); the loop is defensive, and sequential keeps failure
/// attribution deterministic. Concurrency with the Inbox backup is serialized downstream by the
/// per-subInstance lock + ISubItemTerminalGuard probe in the settlement services.
/// </summary>
public sealed class SubflowTerminalRelay(
    IInstanceCommandGateway instanceCommandGateway,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<SubflowTerminalRelay> logger) : ISubflowTerminalRelay
{
    public async Task RelayAsync(
        IReadOnlyList<DomainEventEnvelope> deferredEvents,
        CancellationToken cancellationToken)
    {
        foreach (var envelope in deferredEvents)
        {
            if (envelope.Event is not ISubflowTerminalEvent terminal)
                continue;

            using var activity = PipelineStepActivityHelper.StartOperationActivity("Subflow.TerminalRelay");
            activity?.SetTag(TelemetryConstants.TagNames.EventName, envelope.Event.GetType().Name);
            activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, terminal.InstanceId);
            activity?.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, terminal.SubInstanceId);
            activity?.SetTag(TelemetryConstants.TagNames.RelaySync, terminal.Sync);
            // Same source the gateway routes by — the tag can never disagree with the actual route.
            activity?.SetTag(TelemetryConstants.TagNames.RelayRoute,
                runtimeInfoProvider.IsDomainMatch(terminal.Domain) ? "local" : "remote");

            try
            {
                var outcome = await DispatchAsync(envelope.Event, cancellationToken);
                activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, outcome);
                if (outcome == "relayed")
                    logger.SubflowTerminalRelayed(envelope.Event.GetType().Name, terminal.SubInstanceId, terminal.InstanceId);
            }
            catch (Exception ex)
            {
                // The child's commit already stands; the outbox row guarantees the Inbox backup
                // settles the parent. Never fail the hop for a relay error.
                activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, "failed");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                logger.SubflowTerminalRelayFailed(ex, envelope.Event.GetType().Name, terminal.SubInstanceId, terminal.InstanceId);
            }
        }
    }

    private async Task<string> DispatchAsync(object @event, CancellationToken ct)
    {
        switch (@event)
        {
            case InstanceSubCompletedEvent completed:
            {
                var result = await instanceCommandGateway.CompleteAsync(MapToFlowCompletedInput(completed), ct);
                return HandleResult(result.IsSuccess, @event, result.IsSuccess ? null : result.Error.Message);
            }
            case InstanceSubFaultedEvent faulted:
            {
                var result = await instanceCommandGateway.FaultAsync(MapToSubFlowFaultedInput(faulted), ct);
                return HandleResult(result.IsSuccess, @event, result.IsSuccess ? null : result.Error.Message);
            }
            case InstanceSubCanceledEvent canceled:
            {
                var result = await instanceCommandGateway.CancelAsync(MapToSubItemCanceledInput(canceled), ct);
                return HandleResult(result.IsSuccess, @event, result.IsSuccess ? null : result.Error.Message);
            }
            default:
                return "skipped";
        }
    }

    private string HandleResult(bool success, object @event, string? error)
    {
        if (success)
            return "relayed";
        logger.SubflowTerminalRelayRejected(@event.GetType().Name, error ?? "unknown");
        return "failed";
    }

    // The three Map* methods: MOVE VERBATIM from the hooks —
    //  MapToFlowCompletedInput   ← InstanceSubCompletedEventHook.MapToFlowCompletedInput (incl. TraceRoot/ParentTraceRoot lines)
    //  MapToSubFlowFaultedInput  ← InstanceSubFaultedEventHook.MapToSubFlowFaultedInput
    //  MapToSubItemCanceledInput ← InstanceSubCanceledEventHook.Map
}
```

*(REVIEW P1 accepted — VERIFIED in code: `SubflowCompletionService`'s locked path (the `correlation.IsCompleted` branch) ACKs a duplicate from the flag alone while phase-2 resume may still fail and `RevertCorrelationInNewUowAsync` reopens the correlation — consuming the durable backup. The race PRE-EXISTS (today's hook+Inbox pair had the identical sequence); this plan closes it with the cheapest irreversibility-preserving fix: **re-arm the durable delivery inside the revert UoW** — see new **Task B4b**. A durable `Processing/Settled` third state was considered and rejected: it adds a third write to every completion to protect a rare window that re-arming covers atomically. The requested `phase-1 commit → duplicate → resume failure → revert` sequence is pinned by B4b's tests.)*

Notes for the implementer: (a) adapt `result.IsSuccess/Error` access to the gateway methods' REAL return types; (b) the hop's logging scope (instance ids) is already ambient from the runner — do not rebuild the hooks' BeginScope dictionaries; (c) events keep their `Sync` flag inside the mapped inputs exactly as the hooks mapped them.

- [ ] **Step 4: Telemetry constants** — in `TelemetryConstants.TagNames` add (follow the file's naming style):

```csharp
public const string RelayOutcome = "vnext.relay.outcome";
public const string RelayRoute = "vnext.relay.route";
public const string RelaySync = "vnext.relay.sync";
public const string DeliveryRole = "vnext.delivery.role";
```

*(REVIEW P2 accepted — my inconsistency: the Design Summary promised `vnext.relay.route` but the relay code neither set it nor declared the constant. Resolution applied to this task: inject `IRuntimeInfoProvider` into `SubflowTerminalRelay`, tag `RelayRoute` from the SAME source the gateway routes by (`runtimeInfoProvider.IsDomainMatch(terminal.Domain) ? "local" : "remote"`), add the constant, and add a telemetry test asserting the tag for both a matching and a non-matching domain.)*

- [ ] **Step 5: WorkflowLogs** — add with real sequential unused 40xxx ids:

```csharp
[LoggerMessage(EventId = 40xxx, Level = LogLevel.Information,
    Message = "Subflow terminal {EventName} relayed to parent (sub {SubInstanceId} -> parent {ParentInstanceId})")]
public static partial void SubflowTerminalRelayed(this ILogger logger, string eventName, Guid subInstanceId, Guid parentInstanceId);

[LoggerMessage(EventId = 40xxx, Level = LogLevel.Warning,
    Message = "Subflow terminal relay failed for {EventName} (sub {SubInstanceId} -> parent {ParentInstanceId}); Inbox backup will settle")]
public static partial void SubflowTerminalRelayFailed(this ILogger logger, Exception exception, string eventName, Guid subInstanceId, Guid parentInstanceId);

[LoggerMessage(EventId = 40xxx, Level = LogLevel.Warning,
    Message = "Subflow terminal relay rejected for {EventName}: {Error}; Inbox backup will settle")]
public static partial void SubflowTerminalRelayRejected(this ILogger logger, string eventName, string error);
```

- [ ] **Step 6: Register** — in `PipelineServiceCollectionExtensions`, next to `ITransitionRunner`:

```csharp
services.AddScoped<ISubflowTerminalRelay, SubflowTerminalRelay>();
```

- [ ] **Step 7: Run tests to verify they pass** (Step 2 command)

- [ ] **Step 8: Commit** (exact files only)

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/ISubflowTerminalRelay.cs src/BBT.Workflow.Application/SubFlow/Services/SubflowTerminalRelay.cs src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs test/BBT.Workflow.Application.Tests/SubFlow/SubflowTerminalRelayTests.cs
git commit -m "feat(subflow): post-commit terminal relay — immediate parent settlement as a command"
```

### Task B4: Runner integration

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`

**Interfaces:**
- Consumes: `ISubflowTerminalRelay` (B3), `coreOutput.DeferredEvents` (existing).

- [ ] **Step 1: Wire the relay** — in `ExecuteWithScopeAsync`'s scope callback, resolve once:

```csharp
var terminalRelay = sp.GetRequiredService<ISubflowTerminalRelay>();
```

and AFTER the `Uow.Commit` using-block, BEFORE `return coreResult;`:

```csharp
// Terminal relay: subflow terminal events settle the parent IMMEDIATELY as a command —
// awaited here so a sync chain's response follows the settled chain, and an async job
// relays with gap ≈ 0. The outbox rows written pre-commit stay the durable record; the
// Inbox handlers are the backup and ISubItemTerminalGuard absorbs the duplicate.
await terminalRelay.RelayAsync(coreOutput.DeferredEvents, ct);
```

(The relay itself opens the `Subflow.TerminalRelay` span per event and no-ops instantly when the list holds no terminal event — no `if` needed here.)

- [ ] **Step 2: Build + run runner-adjacent tests**

```bash
dotnet build src/BBT.Workflow.Application
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionRunner"
```

- [ ] **Step 3: Commit**

```bash
git add src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs
git commit -m "feat(runner): relay subflow terminal events immediately after commit"
```

### Task B4b: Re-arm the durable backup when a terminal revert reopens the correlation

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs` (`RevertCorrelationInNewUowAsync`)
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs`, `SubflowCancellationService.cs` — mirror the change wherever the same phase-2 revert exists (grep `RevertCorrelation` in both; skip if a service has no revert path)
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowTerminalRevertRearmTests.cs`

**Why:** the locked path ACKs a duplicate delivery from `correlation.IsCompleted` alone. If that ACK lands between the phase-1 commit and a phase-2 resume failure, the subsequent revert reopens the correlation with the durable backup already consumed — the parent is stranded Busy. Re-publishing the terminal event INSIDE the revert UoW restores the durable delivery atomically with the revert (the outbox row joins the ambient UoW by construction).

**Interfaces:**
- Consumes: `IDistributedEventBus.PublishAsync` (outbox path — the bus is by then the slim stamping decorator), the terminal event contracts, the service's input DTO (carries every field needed to reconstruct the event — the relay/hook mapping is 1:1 and is inverted here).

- [ ] **Step 1: Write the failing tests** — service-level with substitutes, pinning the reviewer's sequence:
  1. `Revert_Republishes_Terminal_Event_In_Same_Uow`: force phase-2 resume to fail (substitute the resume dependency to throw) → assert `RevertAndPersistCorrelationAsync` ran AND `eventBus.PublishAsync` received an `InstanceSubCompletedEvent` reconstructed from the input (same SubInstanceId/InstanceId/Sync), before the revert UoW commits.
  2. `Duplicate_Delivery_Still_Acks_On_Completed_Correlation`: the `IsCompleted` branch behavior is UNCHANGED (returns success) — the safety now comes from re-arming, not from blocking duplicates.
  3. `Rearm_Attempts_Are_Capped`: an event whose `Extensions["rearm_attempt"]` already reads `5` → revert still happens, NO republish, and the exhaustion log fires.

- [ ] **Step 2: Implement** — in `RevertCorrelationInNewUowAsync`, after `RevertAndPersistCorrelationAsync(...)` and before `revertUow.CommitAsync(...)`:

```csharp
// The duplicate-ACK window: a backup delivery that arrived after the phase-1 commit was
// acknowledged from the IsCompleted flag and is now consumed, while this revert reopens the
// correlation. Re-publish the terminal event in THIS UoW so a fresh durable delivery commits
// atomically with the revert — the reopened work is never left without a carrier.
var rearmAttempt = ReadRearmAttempt(originalInput);      // from the input's carried extensions; 0 when absent
if (rearmAttempt >= MaxRearmAttempts)                    // const int MaxRearmAttempts = 5;
{
    logger.SubflowTerminalRearmExhausted(parentInstanceId, subInstanceId, rearmAttempt);
}
else
{
    var rearmEvent = BuildTerminalEventFromInput(originalInput); // inverse of the relay's Map*, verbatim field copy
    rearmEvent.Extensions["rearm_attempt"] = (rearmAttempt + 1).ToString();
    await eventBus.PublishAsync(rearmEvent, cancellationToken: cancellationToken);
    logger.SubflowTerminalRearmed(parentInstanceId, subInstanceId, rearmAttempt + 1);
}
```

Adapt to the service's real signature/fields: the revert helper already receives `subInstanceId`/`parentInstanceId`; thread the full input through to it (it is available in the calling scope). Check whether `FlowCompletedInput` carries the event's extension bag — if not, add a nullable `RearmAttempt` int to the input DTO and to the relay/Inbox mapping instead of abusing extensions (pick whichever round-trips through BOTH the relay path and the Inbox forward path; the input DTO field is the safer carrier — decide by reading `FlowCompletedInput` and the Inbox handler's body mapping).

- [ ] **Step 3: WorkflowLogs** (real 40xxx ids):

```csharp
[LoggerMessage(EventId = 40xxx, Level = LogLevel.Warning,
    Message = "Subflow terminal settlement reverted; durable delivery re-armed (attempt {Attempt}) for sub {SubInstanceId} -> parent {ParentInstanceId}")]
public static partial void SubflowTerminalRearmed(this ILogger logger, Guid parentInstanceId, Guid subInstanceId, int attempt);

[LoggerMessage(EventId = 40xxx, Level = LogLevel.Error,
    Message = "Subflow terminal re-arm budget exhausted ({Attempt}) for sub {SubInstanceId} -> parent {ParentInstanceId}; manual intervention required")]
public static partial void SubflowTerminalRearmExhausted(this ILogger logger, Guid parentInstanceId, Guid subInstanceId, int attempt);
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowTerminalRevertRearm"
```

- [ ] **Step 5: Commit** (exact files)

```bash
git commit -m "fix(subflow): re-arm durable terminal delivery when a resume failure reverts the correlation"
```

### Task B5: Delete the EventHook infrastructure; reduce the bus to trace stamping

**Files:**
- Delete: `src/BBT.Workflow.Infrastructure/Instances/Events/InstanceCanceledEventHook.cs`, `InstanceCompletedCleanupEventHook.cs`, `InstanceFaultedCleanupEventHook.cs`, `InstanceSubStateChangedEventHook.cs`, `InstanceSubCompletedEventHook.cs`, `InstanceSubFaultedEventHook.cs`, `InstanceSubCanceledEventHook.cs` *(delete AFTER B3 moved the three Map* bodies)*
- Delete: `src/BBT.Workflow.Events.Contracts/Events/Hooks/EventHookMode.cs`, `EventHookAttribute.cs`, `IEventPublishHook.cs`, `IEventHookInvoker.cs`
- Delete: `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/EventHookServiceCollectionExtensions.cs`
- Delete: `test/BBT.Workflow.Infrastructure.Tests/EventBus/HookedDistributedEventBusTests.cs`, `test/BBT.Workflow.Application.Tests/EventBus/HookedDistributedEventBusSpanTests.cs`
- Rename+gut: `src/BBT.Workflow.Infrastructure/EventBus/HookedDistributedEventBus.cs` → `TraceStampingDistributedEventBus.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/EventBusHookServiceCollectionExtensions.cs` (decorator lambda; keep the public extension-method name to avoid host churn — check call sites first)
- Modify: `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/WorkflowInfrastructureModuleServiceCollectionExtensions.cs` (remove the 7 `AddEventHook` lines)
- Modify: the 7 event classes in `src/BBT.Workflow.Events.Contracts/Instances/Events/` (remove `[EventHook(...)]` lines and hook-registration doc comments)
- Modify: `src/BBT.Workflow.Domain/SubFlow/ISubItemTerminalGuard.cs` (doc comment: "delivered twice by design: once by the post-commit terminal relay and once through the Inbox backup" — replaces the DurablePostCommit wording)
- Create: `test/BBT.Workflow.Infrastructure.Tests/EventBus/TraceStampingDistributedEventBusTests.cs`

**Interfaces:**
- Produces: `TraceStampingDistributedEventBus : IDistributedEventBus` — keeps ONLY: ctor `(IDistributedEventBus inner, ILogger<TraceStampingDistributedEventBus> logger, ICorrelationIdProvider? correlationIdProvider = null)`, the `StampTraceContext` method VERBATIM (traceparent/tracestate/RequestId + `ILaneAwareDistributedEvent` lane stamping — this is load-bearing for cross-hop traces), and all four `PublishAsync`/`PublishEnvelopeAsync` members as `StampTraceContext(payload)` (where a payload exists) + delegate to `_inner`.

- [ ] **Step 0: External-consumer gate (BLOCKING).** `BBT.Workflow.*` nupkgs are published by CI (`.github/workflows/build-and-publish-images.yml`), so external consumers of the hook types cannot be ruled out from this repo. STOP and confirm with the user (who can check internal feed download/usage stats) that no external project references `IEventPublishHook`/`EventHookAttribute`/`EventHookMode`/`IEventHookInvoker`. If a consumer exists: keep those four contract files as inert `[Obsolete("EventHook infrastructure removed; events ride the outbox — see docs/runtime/event-publish-modes.md")]` shells for one release, and still delete every implementation, registration, and bus branch. If none: delete outright as planned.

- [ ] **Step 1: Write the stamping tests first** (they pin what must survive):

```csharp
// TraceStampingDistributedEventBusTests
// 1. Publish with an active Activity → ITraceableDistributedEvent.TraceParent == activity.Id,
//    TraceState propagated, RequestId taken from ICorrelationIdProvider when null.
// 2. Pre-set TraceParent is NEVER overwritten.
// 3. ILaneAwareDistributedEvent gets TraceRoot/ParentTraceRoot from WorkflowTraceLane when null.
// 4. Every overload delegates to inner with identical arguments (inner Received with same payload/subject/useOutbox).
// 5. A hook-less plain event publishes straight through (no exception, no filtering).
// Port assertions 1-3 from the deleted HookedDistributedEventBusTests where they exist — check the
// old file for its StampTraceContext coverage before deleting, and carry those cases over.
```

Write these as real tests using the old test file's fixture style, then delete the old files.

- [ ] **Step 2: Gut + rename the bus.** Keep `StampTraceContext` byte-for-byte. Delete: `EventHookModeCache`, `GetEventHookMode`, `HookActivitySource`, `ExecuteHooksAsync`, `ExecutePostCommitHooksSafelyAsync`, `GetInvokersForEventType`, `TrimHookSuffix`, `TryEnrichEventMetadata`, `HookExecutionResult`, the `IUnitOfWorkManager`/`IServiceProvider` ctor params (no longer needed — verify nothing else in the class uses them), and every mode branch. Each publish overload becomes:

```csharp
public async Task PublishAsync<TEvent>(TEvent payload, string? subject = null, bool useOutbox = true, CancellationToken cancellationToken = default)
    where TEvent : class
{
    if (payload == null) throw new ArgumentNullException(nameof(payload));
    StampTraceContext(payload);
    await _inner.PublishAsync(payload, subject, useOutbox, cancellationToken);
}
```

(and the metadata overload likewise; `PublishEnvelopeAsync` stays a pure delegate). Update the class XML summary: "Decorator that stamps W3C trace context, the originating request id, and trace-lane anchors onto traceable events at publish time, then delegates to the inner bus."

- [ ] **Step 3: Update the DI extension** — in `EventBusHookServiceCollectionExtensions` keep the extension method name (grep its call sites in the hosts first), change the decorator construction to the new slim ctor, and delete hook-related XML docs. Remove the 7 `AddEventHook` calls from `WorkflowInfrastructureModuleServiceCollectionExtensions`.

- [ ] **Step 4: Strip the 7 event classes** — remove `[EventHook(...)]` lines and `services.AddEventHook<...>()` doc-comment blocks. Build `src/BBT.Workflow.Events.Contracts` to catch stragglers.

- [ ] **Step 5: Update `ISubItemTerminalGuard` doc comment** (wording above — the guard itself is UNCHANGED and still load-bearing).

- [ ] **Step 6: Full-solution build; fix every dangling reference the compiler finds** (there will be some — e.g. usings of `BBT.Workflow.Events.Hooks`). Then run:

```bash
dotnet build /Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx
dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~TraceStampingDistributedEventBusTests"
```

- [ ] **Step 7: Commit** (stage the deletions + renames + edits explicitly; `git add -A src/BBT.Workflow.Infrastructure/EventBus src/BBT.Workflow.Infrastructure/Instances/Events src/BBT.Workflow.Events.Contracts/Events/Hooks` plus the individual modified files)

```bash
git commit -m "refactor(events)!: remove EventHook infrastructure — all events ride the outbox; bus reduced to trace stamping"
```

### Task B5b: Retryable partial cleanup failures

**Files:**
- Modify: `src/BBT.Workflow.Application/Instances/Managers/InstanceCancellationService.cs` (`ProcessCancellationAsync`)
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` (if a new log line is needed; reuse existing ones where they fit)
- Test: `test/BBT.Workflow.Application.Tests/Instances/InstanceCancellationServicePartialFailureTests.cs`

**Why (verified):** per-job `TryCancelInSchedulerAsync` exceptions are caught, logged, and the method still returns `Result.Ok()`. With the Inbox as the sole processor for cleanup events, that ACKs the message and strands uncancelled scheduler jobs with no retry. All three cleanup events (canceled / completed-cleanup / faulted-cleanup) funnel through this method via the `/cancel-cleanup`-style internal endpoints.

- [ ] **Step 1: Write the failing tests**
  1. `PartialSchedulerFailure_Returns_RetryableFail_And_Persists_Winners`: two active jobs; scheduler substitute cancels job A, throws for job B → `MarkManyAsProcessedAsync` received `[A]` only, AND the method returns `Result.Fail` (not Ok).
  2. `Retry_Run_Only_Touches_Remaining_Jobs`: second call where only job B is still active → cancels B, returns Ok.
  3. `AllJobsCancelled_Returns_Ok` (pins the happy path unchanged).

- [ ] **Step 2: Implement** — track failures in the existing loop:

```csharp
var failedCount = 0;
foreach (var job in jobs)
{
    try
    {
        if (await TryCancelInSchedulerAsync(job, instance.Id, cancellationToken))
        {
            cancelledJobIds.Add(job.Id);
        }
    }
    catch (Exception ex)
    {
        failedCount++;
        logger.InstanceJobDeletionFailed(ex, job.JobId, instanceId);
    }
}

await instanceJobRepository.MarkManyAsProcessedAsync(cancelledJobIds, cancellationToken);
logger.InstanceCanceledJobsProcessed(instanceId, cancelledJobIds.Count);

if (failedCount > 0)
{
    // Retryable: winners are already persisted above, so the Inbox redelivery this failure
    // triggers only retries the jobs that are still active. Returning Ok here would ACK the
    // message and strand the uncancelled scheduler entries forever.
    return Result.Fail(WorkflowErrors.InstanceCancellationFailed(
        instanceId, $"{failedCount} scheduler job cancellation(s) failed; delivery will be retried"));
}

return Result.Ok();
```

- [ ] **Step 3: Verify the retry loop end-to-end wiring** — the fix only works if the failure round-trips as a RETRYABLE delivery: check what HTTP status `FromResult(Result.Fail(WorkflowErrors.InstanceCancellationFailed(...)))` produces in `InstanceController.CancelCleanupAsync` (open the `FromResult` helper and the error's catalog entry). `DaprOrchestrationForwarder` rethrows ONLY transient statuses (5xx family via `TransientHttpStatus`) and ACK-drops 4xx. If the error maps to 4xx, change the mapping for this error (or introduce a dedicated error code) so it lands as 500. Record the verified status code in the task's commit message.

- [ ] **Step 4: Run the tests; commit** (exact files)

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~InstanceCancellationServicePartialFailure"
git commit -m "fix(instances): partial scheduler-cancel failures are retryable instead of silently ACKed"
```

### Task B6: Inbox backup-role observability

**Files:**
- Modify: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCompletedEventHandler.cs`, `InstanceSubFaultedEventHandler.cs`, `InstanceSubCanceledEventHandler.cs`

- [ ] **Step 1: Tag the backup role** — in each handler, right after its `EventTraceScope.Start(...)` line:

```csharp
// This delivery is the durable BACKUP of the post-commit terminal relay: in the normal case the
// relay already settled the parent and the settlement path answers AlreadySettled via the
// pre-lock probe. Dashboards separate primary vs backup deliveries on this tag.
System.Diagnostics.Activity.Current?.SetTag(TelemetryConstants.TagNames.DeliveryRole, "backup");
```

- [ ] **Step 2: Build the Inbox worker** — `dotnet build workers/BBT.Workflow.Workers.Inbox`

- [ ] **Step 3: Commit**

```bash
git add workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCompletedEventHandler.cs workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubFaultedEventHandler.cs workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCanceledEventHandler.cs
git commit -m "feat(inbox): tag subflow terminal deliveries as backup role for relay observability"
```

### Task B7: Outbox worker — waker subscription + config enablement

**Files:**
- Create: `workers/BBT.Workflow.Workers.Outbox/Controllers/OutboxWakeupController.cs`
- Create: `workers/BBT.Workflow.Workers.Outbox/Controllers/DaprSubscribeController.cs`
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json` (`Aether:Outbox:WakeupSignalEnabled: true`)
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json` (add `"WakeupSignalEnabled": true` to its outbox section — **stage this hunk alone with `git add -p`**)
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json` (same key IF it registers `AddAetherOutbox` — verify with `rg -l "AddAetherOutbox" orchestration/ execution/ workers/ src/`; skip hosts that don't)

**Interfaces:**
- Consumes: `IPollingWakeSignal<IOutboxProcessor>` (A2 registration), `ITopicNameStrategy`, `AetherEventBusOptions`, `OutboxWakeupEvent` (A2).

- [ ] **Step 1: Wakeup endpoint**

```csharp
using BBT.Aether.Events;
using BBT.Aether.Polling;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Outbox.Controllers;

/// <summary>
/// Receives the <see cref="OutboxWakeupEvent"/> nudge from pub/sub and wakes the outbox poller
/// immediately. Deliberately NOT inbox-backed: the nudge is loss-tolerant (polling backstops it)
/// and duplicate-tolerant (signals coalesce), so durability machinery would only add latency.
/// </summary>
[ApiController]
public sealed class OutboxWakeupController(
    IPollingWakeSignal<IOutboxProcessor> wakeSignal) : ControllerBase
{
    [HttpPost("internal/outbox-wakeup")]
    public IActionResult Wake()
    {
        wakeSignal.Signal();
        return Ok();
    }
}
```

- [ ] **Step 2: Subscription declaration** — the Outbox worker registers no `IEventHandler`s, so the registry-driven discovery (Inbox worker's pattern) would return nothing; declare the single subscription directly. Mirror the Inbox worker's `[Route("dapr")]` + `MapSubscribeHandler()` coexistence exactly (`workers/BBT.Workflow.Workers.Inbox/Controllers/DaprEventDiscoveryController.cs`):

```csharp
using BBT.Aether.Events;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Outbox.Controllers;

/// <summary>
/// Dapr subscription discovery for the Outbox worker: exactly one subscription — the outbox
/// wakeup nudge. Topic name comes from the same ITopicNameStrategy the publisher uses, so
/// environment prefixing stays consistent by construction.
/// </summary>
[Route("dapr")]
public sealed class DaprSubscribeController(
    ITopicNameStrategy topicNameStrategy,
    AetherEventBusOptions eventBusOptions) : ControllerBase
{
    [HttpGet("subscribe", Order = int.MinValue)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Subscribe()
        => new JsonResult(new[]
        {
            new
            {
                pubsubname = eventBusOptions.PubSubName,
                topic = topicNameStrategy.GetTopicName(typeof(OutboxWakeupEvent)),
                route = "/internal/outbox-wakeup"
            }
        });
}
```

(Check how `AetherEventBusOptions` is registered — raw instance vs `IOptions<>` — and inject accordingly.)

- [ ] **Step 3: appsettings** — add `"WakeupSignalEnabled": true` inside each verified writer host's existing outbox config section (the Outbox worker binds `Aether:Outbox`; verify each host's actual section path by grepping `GetSection` near its `AddAetherOutbox` call — do not guess). The Inbox worker registers only `AddAetherInbox` (verified) — do NOT touch its appsettings.

- [ ] **Step 4: Build** — `dotnet build workers/BBT.Workflow.Workers.Outbox`

- [ ] **Step 5: Commit** (exact staging; orchestration hunk via `git add -p`)

```bash
git status --short
git add workers/BBT.Workflow.Workers.Outbox/Controllers/OutboxWakeupController.cs workers/BBT.Workflow.Workers.Outbox/Controllers/DaprSubscribeController.cs workers/BBT.Workflow.Workers.Outbox/appsettings.json
git add -p orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json   # ONLY the WakeupSignalEnabled hunk
git commit -m "feat(outbox-worker): subscribe to outbox wakeup nudge; enable wakeup signal in writer hosts"
```

(add the execution host appsettings to the staging list only if actually modified in Step 3.)

### Task B8: Full build + regression sweep

**Files:** none

- [ ] **Step 1:** `dotnet build /Users/U0B006/Documents/repos/burgan-tech/vnext/BBT.Workflow.slnx`

- [ ] **Step 2:** Run the touched test projects and compare failure counts against the baseline captured on this branch BEFORE Task B2 (run once up front; suite-wide master baseline ~191). Requirement: no NEW failures.

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests
dotnet test test/BBT.Workflow.Application.Tests
dotnet test test/BBT.Workflow.Domain.Tests
```

- [ ] **Step 3:** Commit any fixes (`fix(events): <specific regression>`), exact files only.

### Task B9: Documentation + convention updates

**Files:**
- Create: `docs/runtime/event-publish-modes.md`
- Modify: `docs/README.md` (runtime docs navigation/overview grouping)
- Modify: `.claude/rules/dotnet-coding-standards.md` (the "Domain Events (Dual Processing)" section)
- Modify: `CLAUDE.md` (root — the "Domain Events (dual-processing pattern)" bullet)

- [ ] **Step 1: Write `docs/runtime/event-publish-modes.md`** — English, covering: the two-mode taxonomy table verbatim from the Design Summary; the event classification table; relay semantics (sequential, swallow-and-log, Sync→CallerMode, gateway routing); the four independence guarantees; the wakeup-signal mechanism (gating: only commits that stored outbox rows publish the nudge; fire-and-forget with 2 s bound; polling backstop) ; latency table **explicitly labelled "unvalidated design budget — measured in verification"**; observability contract (`Subflow.TerminalRelay` span tags, `vnext.delivery.role=backup`, and the health signal: backup deliveries that SETTLE mean the relay is failing — `already-settled` is normal noise); accepted risks verbatim from the Design Summary (worker criticality + Helm replicas/probes note for vnext-helm-charts — flag for the user, do not edit that repo; queue-row throughput; rolling-upgrade coexistence); the new config key `Aether:Outbox:WakeupSignalEnabled` (Helm values reminder); known gap: `directly:true`-style arming windows do NOT apply here (no jobs in this design) — instead the crash-before-relay window is covered by the Inbox backup.

- [ ] **Step 2: Update the convention docs** — in `.claude/rules/dotnet-coding-standards.md` replace the dual-processing section: every distributed event now requires (1) contract in Events.Contracts with `[EventName]`, (2) an Inbox `IEventHandler` (async, distributed), (3) WorkflowLogs entries; hooks NO LONGER EXIST — subflow terminal events additionally implement `ISubflowTerminalEvent` for the relay. Mirror the same change in root `CLAUDE.md`'s dual-processing bullet. Keep the edits surgical — do not rewrite unrelated sections.

- [ ] **Step 3: Commit**

```bash
git add docs/runtime/event-publish-modes.md docs/README.md .claude/rules/dotnet-coding-standards.md CLAUDE.md
git commit -m "docs(runtime): outbox-only event publishing, subflow terminal relay, wakeup signal"
```

---

# FAZ C — Doğrulama (integration + load; core-process change ⇒ mandatory per CLAUDE.local.md)

### Task C1: Integration test run against local runtime

**Files (vnext-example repo — `/Users/U0B006/Documents/repos/burgan-tech/vnext-example`):**
- Modify: `tests/Core.IntegrationTests/test.runsettings` (uncomment `<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>`)

- [ ] **Step 1: Infra** — check first (`docker ps --format '{{.Names}}' | head -20`); start only if absent: `cd etc/docker && ./run-docker.sh`.

- [ ] **Step 2: No migration needed** (no schema changes — outbox/inbox tables already exist). Skip DbMigrator.

- [ ] **Step 3: Start the 4 apps** (each in its own terminal, ALWAYS with `--launch-profile http`):

```bash
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
```

```bash
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http
```

```bash
dotnet run --project workers/BBT.Workflow.Workers.Inbox --launch-profile http
```

```bash
dotnet run --project workers/BBT.Workflow.Workers.Outbox --launch-profile http
```

- [ ] **Step 4: Run the subflow + chain suites** (sub-terminal resume, cancel cascade, sub-state notifications):

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~Subflow|FullyQualifiedName~ChainBusy"
```

Expected: green modulo the known pre-existing lab stuck-Busy red (recorded in memory). Investigate ANY new red — specifically watch: parent resume after async child completion (validates relay), sync chain end-to-end result (validates awaited relay), cancel flows (cancel cleanup now rides outbox+wakeup), sub-state notifications arriving after completion (validates the monotonic guard + correlation-closed behavior — flagged verification item).

- [ ] **Step 5: Targeted behavior probes** (manual, log-based):
  - Relay primary path: grep orchestration logs for `Subflow terminal ... relayed` and confirm the Inbox worker's corresponding backup delivery logs `AlreadySettled`-style outcomes (probe absorbing the duplicate).
  - Wakeup path: with the apps idle, trigger a flow that emits only pure-outbox events (e.g. instance completion cleanup) and measure commit→outbox-worker-publish log-timestamp delta: expected sub-second (vs 0–5 s before). Record numbers (indicative only).
  - Kill-the-worker resilience: stop the Outbox worker, run a subflow completion — parent must STILL resume instantly via relay; restart the worker and confirm the backlog drains.

### Task C2: Subflow-heavy load test (throughput + latency histogram)

**Files (vnext-example repo):**
- Create: `api-tests/subflow-orchestration/terminal-relay-load.py`
- Modify: `api-tests/subflow-orchestration/README.md` (run instructions per CLAUDE.local.md §2.2)
- Modify: `TEST-SCENARIOS.md` (new row: feature set = "subflow terminal relay + outbox wakeup signal; event publish modes", reason = this plan, date)

- [ ] **Step 1: Write the script** — modeled on the existing `updatedata-concurrency-test.py` in the same folder (read it first for conventions: argparse base URL/concurrency/iterations, requests session, summary printing). It must:
  1. Start N parent instances that spawn a subflow and drive the child to completion (async path).
  2. **Primary histogram from server-side persisted timestamps, NOT client polling:** after the run, query PostgreSQL (psycopg2/psql, connection string as a script parameter) for each pair — the child's terminal transition record completion timestamp vs the parent's resume transition record timestamp (identify the exact columns/tables by inspecting the transition-record schema first; the child terminal record and the parent's next transition record after the correlation share the instance ids). Compute p50/p95/p99/max of `parent_resume_ts - child_terminal_ts` from these rows.
  3. Client polling of the parent state function remains ONLY as a black-box stuck check (bounded wait per instance), never as the latency source.
  4. Count `sys_queues` outbox+inbox rows produced during the run (same DB session) to quantify the queue-row cost of the pure-outbox events.
  5. Explicit pass/fail printed at the end: **same-domain async relay gap p99 ≤ 250 ms** (the design objective — hard threshold), p95 > 1 s ⇒ FAIL, any instance stuck > 30 s ⇒ FAIL. Report all three verdicts separately.

*(REVIEW P2 accepted — measurement methodology corrected in Step 1 below: the histogram comes from SERVER-SIDE persisted timestamps, client polling remains only a black-box stuck check, and the pass/fail thresholds are explicit including the `p99 ≤ 250 ms` objective.)*

- [ ] **Step 2: README** — dependencies + install, full run command with parameters, what it measures, failure thresholds, how to read the output (per CLAUDE.local.md §2.2 requirements).

- [ ] **Step 3: Run it against the local stack**; record results.

- [ ] **Step 4: TEST-SCENARIOS.md row in the SAME commit** as the scenario files (repo convention).

### Task C3: Result report + memory update

- [ ] **Step 1:** Append a "Verification" section to `docs/runtime/event-publish-modes.md`: integration outcomes, indicative latency numbers (labelled as such), load-test p50/p95/p99, queue-row counts, worker-kill resilience result.
- [ ] **Step 2:** Update memory `event-publish-refactor-plan.md` (madde 1 implemented with final architecture; hook infra removed; relay + wakeup landed; pending: madde 2 trace refactor) and mark `subflow-dual-processing-design` memory as superseded by the relay+backup model.
- [ ] **Step 3:** Final local commits in both repos; **do not push**.

---

## Self-Review Notes

- Spec coverage: hook infra fully deleted (B5); all events outbox-published via the slim stamping bus (B5); 3 terminal events keep hook behavior via marker + relay (B2/B3/B4) with sync-stays-sync (relay awaited pre-return) and async immediate; mode declared by `ISubflowTerminalEvent` (closed set — no attribute registry, per user-approved design detail); parallel-processing question answered by sequential relay + guard-serialized duplicate handling; observability contract (relay span/outcome tags, backup role tag, health signal) in B3/B6/B9; independence guarantees documented (Design Summary + B9); wakeup signal Dapr-event-based, gated on committed outbox writes (A2), wake-aware loops incl. startup (A3), inbox nudge (A4); local commits only.
- Carried review resolutions from the previous plan revision (all verified against code): explicit `BBT.Workflow.slnx` path everywhere; exact-file staging + `git add -p` for the pre-modified orchestration appsettings; `1.0.38-local` handoff caveat; coordinator extracted for testability with detached bounded notify; startup offset wake-aware; Inbox worker gets NO outbox config (registers only `AddAetherInbox`); latency numbers labelled unvalidated until Faz C measures them.
- Judgment calls an executor must NOT "fix": the relay does not mark/suppress the outbox row (event stays a published fact; duplicate costs one guard probe); relay failures never fail the hop; Inbox handlers for ALL 7 events stay registered (backup for terminals, primary for the rest); `IdlePollingInterval` stays at current config (tunable knob, out of scope); the deleted hook API gets no `[Obsolete]` bridge (user-approved).
- Type-consistency: `ISubflowTerminalEvent` (Events.Contracts, `BBT.Workflow.Events`), `ISubflowTerminalRelay.RelayAsync(IReadOnlyList<DomainEventEnvelope>, CancellationToken)` (Application, `BBT.Workflow.SubFlow`), `TraceStampingDistributedEventBus` (Infrastructure), `IPollingWakeSignal<TMarker>.WaitAsync(TimeSpan, CancellationToken)` (Aether), `OutboxWakeupCoordinator.OnOutboxMessageStored()` (Aether) — used identically across tasks.

## Review Resolutions — round 2 (2026-08-30 user review; all 7 notes evaluated, verified against code)

| # | Note | Verdict | Where resolved |
|---|---|---|---|
| 1 | P2 — deleting public hook types may break external package consumers (CI publishes nupkgs — verified) | Accepted | B5 Step 0 blocking gate: user confirms feed usage; consumer exists ⇒ `[Obsolete]` shells for contracts, implementations still deleted |
| 2 | P1 — cleanup swallows partial scheduler-cancel failures, Inbox ACKs, jobs stranded (verified in `ProcessCancellationAsync`) | Accepted | New Task B5b: retryable `Result.Fail` on partial failure; winners persisted so retries touch only leftovers; Step 3 verifies the 5xx round-trip through `FromResult`/forwarder |
| 3 | P2 — no-ambient-UoW wakeup can fire before the caller's SaveChanges | Accepted as documented contract | A2 code comment + B9 doc: early best-effort hint, excluded from the latency guarantee; all transition paths have ambient UoW |
| 4 | P1 — singleton coordinator over scoped `IUnitOfWorkManager` (verified: TryAddScoped) = captive dependency | Accepted | A2 Step 6: coordinator registered SCOPED (static dedupe table keeps per-UoW semantics); scope-validation check added to Step 7 |
| 5 | P1 — blocking-SubFlow duplicate ACKed from `IsCompleted` while phase-2 resume may revert ⇒ durable backup consumed (verified in `SubflowCompletionService:159-180`; pre-existing race) | Accepted with fix | New Task B4b: re-arm the terminal event inside the revert UoW (atomic outbox row), capped at 5 attempts with exhaustion alarm; duplicate-ACK branch deliberately unchanged; reviewer's sequence pinned by tests |
| 6 | P2 — promised `vnext.relay.route` tag never set, constant missing | Accepted (my inconsistency) | B3: `IRuntimeInfoProvider` injected, tag derived from the same `IsDomainMatch` the gateway routes by, constant added, local/remote telemetry test added |
| 7 | P2 — client-polling histogram can't validate a 10–30 ms budget; `p99 ≤ 250 ms` needs explicit pass/fail | Accepted | C2 Step 1: histogram from server-side transition-record timestamps; polling demoted to stuck-check; explicit thresholds incl. p99 ≤ 250 ms hard verdict |
