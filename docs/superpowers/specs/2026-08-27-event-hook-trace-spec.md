# Spec: Event Hook Spans & Outbox Trace Continuity

Date: 2026-08-27 · Status: approved for planning · Owner: platform
Repos: vnext (`/Users/U0B006/Documents/repos/burgan-tech/vnext`) + **aether** (`/Users/U0B006/Documents/repos/burgan-tech/aether`) — **the Aether change is USER-APPROVED (2026-08-27), not a proposal.**

## The ask

1. Event hooks that run after the UoW commit currently show as one undifferentiated block inside
   the `Uow.Commit` span. Make each hook a child span there.
2. Remote requests made by hooks must be attributable in the trace (which hook made which call).
3. When an event drops into the outbox/inbox, record the trace/span identity of the drop, and when
   it is handled, show it in the originating tree.

## Findings (all verified in code, 2026-08-27)

**The publish/commit chain:**
- `TransitionRunner` wraps event staging in `Events.PublishDeferred` and the commit in `Uow.Commit`.
- `HookedDistributedEventBus` (vnext Infrastructure) stamps `TraceParent`/`TraceState` onto
  traceable payloads at publish time (line ~87) — the cross-hop identity ALREADY travels.
- Hooks run in two modes (`EventHookMode`): `HandledOrFallback` (inside `PublishAsync`, i.e. under
  `Events.PublishDeferred`) and `DurablePostCommit` (registered via `ambient.OnCompleted`, invoked
  by `CompositeUnitOfWork.CommitAsync` → `InvokeCompletedHandlersAsync` at
  `CompositeUnitOfWork.cs:313` — i.e. INSIDE `uow.CommitAsync`, hence inside the `Uow.Commit`
  span). Every `sub:*` terminal event is `DurablePostCommit`.
- `ExecuteHooksAsync` (`HookedDistributedEventBus.cs:~274-395`) loops invokers with **no span per
  hook**. This is the user's observed block: hook work (including remote Dapr/HTTP calls, which do
  emit client spans) hangs directly under `Uow.Commit` with nothing attributing it to a hook.

**The outbox/inbox chain:**
- Enqueue: `DistributedEventBusBase.PublishAsync` opens `EventBus.Publish`
  (tags `event.name`, `event.topic`, `event.use_outbox`) → `EfCoreOutboxStore.StoreAsync` builds
  the `OutboxMessage` and fills `ExtraProperties` (`TopicName`, `Version`, `Source`, `Subject`).
  **No trace identity is persisted on the row, and the originating span never learns the outbox
  message id** (the id is born inside the store; `IOutboxStore.StoreAsync` returns `Task`).
- Drain: `OutboxProcessor` opens `Outbox.Process` per message (tags `event.name`,
  `outbox.message_id`, `outbox.retry_count`) — **parented to the worker's own loop**
  (`Activity.Current`), so it lives in a separate trace with no connection to the originating one.
  The payload bytes DO contain the `TraceParent` (stamped by the hooked bus), but the processor
  never reads them.
- Handle: `workers/BBT.Workflow.Workers.Inbox/Tracing/EventTraceScope` — used by **10 of 10**
  handlers — parses the event's `TraceParent`, parents the handler span INTO THE ORIGINATING
  TRACE, and attaches the pub/sub delivery span as an `ActivityLink`. **Ask 3's "handled in the
  originating tree" already works end to end**; live evidence: `InstanceSubFaulted.Handle`
  appeared inside a `document-ready-update` tree ~11 s after commit.

**Packaging constraint:** vnext consumes Aether **from nuget.org only** (`AetherPackageVersion`
1.0.36, no local feed in `NuGet.config`). An Aether code change cannot be exercised through vnext
locally; it becomes visible to vnext at the next Aether release. The team's established flow is
Aether-first-merge (see the outbox Faz-1 precedent).

## Decisions taken

- **One span per hook invocation, named after the hook** (`EventHook.{name}`, trailing
  `EventHook`/`Hook` trimmed for display, full name in a tag). It lands under whatever is ambient —
  `Uow.Commit` for `DurablePostCommit`, `Events.PublishDeferred` for `HandledOrFallback` — with no
  re-parenting tricks. Volume is not a concern: hooks per event are few (contrast with the
  FanOut/memo-counter decision, where per-hit spans were rejected for volume).
- **The hook span's ActivitySource is named `BBT.Workflow.Instances.Events`** — deliberately: that
  name is ALREADY listed in the orchestration, inbox and outbox hosts' `AdditionalSources`, so
  creating the source lights it up with zero config change there. Only the Execution host lacks
  the entry and gets it added.
- **Outbox row carries the drop identity in `ExtraProperties`** (`TraceParent`, `TraceState`),
  written by `EfCoreOutboxStore.StoreAsync` from `Activity.Current` — same mechanism as the
  existing `TopicName` entry, no schema change, no interface change.
- **The originating trace learns the outbox message id via `Activity.Current`** inside
  `StoreAsync` (`outbox.message_id` tag on the ambient `EventBus.Publish` span) — chosen over
  changing `IOutboxStore.StoreAsync`'s return type, which would ripple through every implementor
  for the same information.
- **`Outbox.Process` is re-parented into the originating trace** (parsed from
  `ExtraProperties["TraceParent"]`, `isRemote: true`), with the worker's own loop span attached as
  an `ActivityLink` — the exact mirror of what `EventTraceScope` already does on the inbox side,
  so the whole event chain reads as one tree: publish → outbox drop → outbox publish → inbox
  handle. When the row carries no trace identity (pre-deploy rows, non-traceable events), the
  current worker-loop parenting is kept unchanged.
- **Aether verification is honest about the packaging gap:** the Aether change is unit-tested in
  the aether repo (model: `OutboxProcessorDeadLetterTests`); E2E through vnext is explicitly
  deferred to the next Aether release and recorded as such in the docs.

## Out of scope

- Inbox-side changes: `EventTraceScope` already delivers ask 3's handler half; all 10 handlers use it.
- Event HANDLER spans in the inbox worker (they exist), pub/sub broker instrumentation, and any
  change to `Events.PublishDeferred` / `Uow.Commit` span placement.
- Hook retry/durability semantics — observability only, no behavioral change anywhere.

## Success criteria

1. A trace of a transition whose commit fires a `DurablePostCommit` hook shows
   `Uow.Commit → EventHook.{name} → (the hook's own client spans)`.
2. A hook failure is visible as an error-status span, without changing hook failure semantics
   (post-commit failures stay swallowed-and-logged).
3. After the Aether change: the originating `EventBus.Publish` span carries `outbox.message_id`,
   and the outbox worker's `Outbox.Process` span for that message is in the SAME trace, linked to
   the worker loop. (Verifiable through vnext only after the next Aether release — until then,
   pinned by Aether unit tests.)
4. No behavioral change: hook execution order, failure swallowing, outbox retry/lease semantics
   all unchanged. No new failing test name in either repo's suite.
