# Declarative Long-Poll Termination on State Entry

## Why

A long-polling client repeatedly issues conditional `GET /functions/state` (200/304) until the
instance status changes. There was no declarative way for a state to tell the client *"stop polling
now, render my screen, then let the engine continue."* Teams had to bake that into per-workflow
client logic. This feature moves the decision into the workflow definition.

## Model

A state declares the behavior under a generic, extensible `interaction` container:

```jsonc
"interaction": {
  "longPoll": {
    "terminate": true,
    "fallbackTimeoutSeconds": 60,        // optional, default 60
    "roles": [ { "role": "backoffice.operator", "grant": "allow" } ]  // optional, default-allow
  }
  // future facets (polling, navigation, refresh) are added as siblings here
}
```

The runtime reads only through `State` helpers (`TerminatesLongPollOnEntry`, `LongPollAckRoles`,
`LongPollFallbackTimeoutSeconds`), so future `interaction` facets never touch the pipeline or
State-function code.

## Flow

1. **Pause.** When the pipeline enters a state with `interaction.longPoll.terminate=true`, it runs
   `ChangeState` (50) + `OnEntry` (60), then `HandleLongPollTerminationStep` (order **75**) arms a
   durable `Instance.LongPollAckToken`, schedules a one-shot fallback resume job
   (`fallbackTimeoutSeconds`, default 60s), and pauses by skipping the epilogue
   (`EpilogueMode.Skip` + `MarkTerminal` + `SkipTo(Finalize)`). The instance stays **Busy** — the
   same resting shape as a SubFlow pause.
2. **Signal.** While `LongPollAckToken` is set, the State (long-poll) function returns **HTTP 200**
   (no error code; ETag/304 path unchanged) with an `interaction` object — grouping client directives
   under one key — but only for callers whose role satisfies `interaction.longPoll.roles`
   (default-allow when none). The response still carries the entered state name + view href so the
   client can render.

   ```jsonc
   "interaction": {
     "terminateLongPoll": true,
     "ack": { "href": "/api/core/workflows/account-opening/instances/{id}/longpoll/ack" }
   }
   ```

   The `ack` href follows the same `{ "href": "…" }` shape as `data`/`view`. The `interaction` object
   is omitted entirely when no directive applies.
3. **Acknowledge.** The client stops polling, renders the screen, and `POST`s to
   `…/instances/{instance}/longpoll/ack`. The endpoint role-checks, best-effort cancels the fallback
   job, and resumes the pipeline.
4. **Resume.** Acknowledge (or the fallback timeout) resumes via
   `ExecMode.Resume` + `ResumeFrom = ClearBusyOnResumeStep` + `IsLongPollAckResume`. `ClearBusyOnResumeStep`
   (79) compare-and-clears the token, clears Busy, and the epilogue runs (Schedule → Auto → Finish →
   Finalize → ResolveAvailable) — the pipeline continues exactly where it paused.

## SubFlow chain (nested / cross-domain)

When the entered terminate state belongs to a **subflow**, the instance that pauses and arms
`LongPollAckToken` is the deepest active subflow child — not the top instance the client polls. Both
sides follow the chain:

- **Read (State function).** `GetSubFlowTransitionsAsync` already recurses down via
  `instanceQueryGateway.GetFunctionWithStateAsync` (routed local/remote). The child's `interaction`
  block is bubbled up through `SubFlowStateInfo`, and each level **rewrites the ack href** to its own
  acknowledge endpoint — so the top response always carries the top's `ack.href`. Role filtering
  stays at the child (caller role forwarded via headers).
- **Command (acknowledge).** `AcknowledgeLongPollAsync` descends like `MarkBusyAsync`: at each level,
  if the instance is awaiting → resume here (leaf); else if it has an active SubFlow correlation →
  forward via `IInstanceCommandGateway.AcknowledgeLongPollAsync` to the child (Routed → Local /
  Remote on `IsDomainMatch`), one hop per level. The client always POSTs the top's `longpoll/ack`;
  the chain walk reaches the paused (possibly cross-domain) instance and resumes it.

Only `SubFlowType.SubFlow` correlations are followed (the same set the State function follows);
SubProcess (fire-and-forget) is out of scope. Each level armed its own fallback job, so a failed
descent hop is still covered by the deepest child's fallback.

## Idempotency

Acknowledge and the fallback timeout can both request a resume. The reserved `:lpack` lock
serializes them, and `ClearBusyOnResumeStep` stops as a safe no-op when the token is already cleared
(`!Instance.IsAwaitingLongPollAck`). The fallback handler also no-ops via the same token guard, and
acknowledge on a non-awaiting instance returns `Ok` (no-op).

## Profiles

`LongPollTermination` (75) is excluded from the **ErrorBoundary** and **AutoChain** profiles —
error-boundary and auto-chained transitions must never pause.

## Key Source

- `State` / `StateInteraction` / `LongPollInteraction` — `src/BBT.Workflow.Domain/Definitions/States/`
- `Instance.LongPollAckToken` / `ArmLongPollAck` / `ClearLongPollAck` — `src/BBT.Workflow.Domain/Instances/Instance.cs`
- `HandleLongPollTerminationStep`, `ClearBusyOnResumeStep` — `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/`
- `LongPollAckResumeService`, `LongPollAckTimeoutJobHandler` — `src/BBT.Workflow.Application/`
- State signal — `InstanceQueryAppService.ResolveLongPollTerminationAsync`
- Acknowledge — `InstanceController.AcknowledgeLongPollAsync` → `InstanceCommandAppService.AcknowledgeLongPollAsync`

## Change-Safety

- The pause/resume reuses the SubFlow resume plumbing (`IsInternalResume`); changes to either must
  preserve the shared lock-key, validation-bypass, and busy-confirmation behavior.
- The instance stays Busy during the ack window; do not re-mark Busy on long-poll resume (a redundant
  resume must not strand an already-advanced instance).
