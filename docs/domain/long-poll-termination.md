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
`LongPollRule`, `LongPollFallbackTimeoutSeconds`), so future `interaction` facets never touch the
pipeline or State-function code.

### Rule-based authorization (issue #936)

Instead of `roles`, a state may gate the interaction with a **condition rule** — the same
`IConditionMapping` script contract view and notification rules use, compiled and cached by the
shared script engine:

```jsonc
"interaction": {
  "longPoll": {
    "terminate": true,
    "rule": { "location": "./InteractionGate.csx", "code": "<base64>" }  // IConditionMapping
  }
}
```

```csharp
// InteractionGate.csx — admit only mobile-channel callers.
// NOTE: context.Headers is DYNAMIC — index it and cast; `TryGetValue(out var …)`
// does not compile against a dynamic receiver (CS8197).
using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class InteractionGate : IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        try
        {
            if (context.Headers == null)
                return Task.FromResult(false);
            string channel = (string)context.Headers["x-channel"];
            return Task.FromResult(channel == "mobile");
        }
        catch (Exception)
        {
            return Task.FromResult(false); // missing key ⇒ deny, matching the gate's fail-closed posture
        }
    }
}
```

- **One arm or the other, never both.** `roles` and `rule` are alternatives; `WorkflowValidator`
  rejects a `longPoll` declaring both, validates the rule's script body like every compilable slot,
  and validates the `roles` grants' dynamic-role syntax. The `vnext-schema` contract enforces
  exactly-one at authoring time.
- **One rule per interaction.** Unlike `views[]`, there is no rule list and no fallback entry — the
  single rule decides.
- **Both surfaces, one gate.** The State function's signal emit and the acknowledge endpoint admit
  through the same `ILongPollInteractionGate`, which owns the whole arm selection (rule, else roles,
  else allow) — so their verdicts cannot diverge. Caller roles are resolved lazily through a
  surface-supplied factory, only when the roles arm applies. The rule's script context carries the
  workflow, the instance, request headers and query parameters (the acknowledge endpoint forwards
  headers only) — e.g. *"admit when header `x-channel` is `mobile`"*. Read instance data through
  `context.Instance.Data` (materialized lazily, only when the rule touches it). **`context.Body` is
  deliberately NOT populated** — unlike a view rule's context, this surface has no request payload,
  and pre-filling Body with the latest data cost a full serialize+parse on every poll even for
  header-only rules. A view rule reused as an interaction rule must switch `context.Body.*` reads to
  `context.Instance.Data.*`, or it will throw and deny (fail-closed).
- **Fail-closed.** A rule returning `false`, throwing, or failing to compile denies: the signal is
  not emitted and the acknowledge answers `403`. A broken rule cannot strand the instance — the
  fallback-timeout job resumes the pipeline regardless of callers.

**Caching posture.** A rule's inputs (headers, query parameters, instance data) are not part of
`CallerScopeHash`, so a state body whose interaction is rule-gated is **never stored in the shared
body cache** (the same applies conservatively to a bubbled subflow interaction, whose gating arm the
parent cannot see). The fingerprint 304 fast path is unchanged, which leaves one accepted gap,
same class as the scheduled-entries gap (#864): a caller whose rule inputs change while the state
fingerprint does not can keep receiving 304 with the previous verdict until the next state/status
change. Enforcement is never stale — the acknowledge evaluates the rule fresh on every request.

## Flow

1. **Pause.** When the pipeline enters a state with `interaction.longPoll.terminate=true`, it runs
   `ChangeState` (50) + `OnEntry` (60), then `HandleLongPollTerminationStep` (order **75**) arms a
   durable `Instance.LongPollAckToken`, schedules a one-shot fallback resume job
   (`fallbackTimeoutSeconds`, default 60s), and pauses by skipping the epilogue
   (`EpilogueMode.Skip` + `MarkTerminal` + `SkipTo(Finalize)`). The instance stays **Busy** — the
   same resting shape as a SubFlow pause.
2. **Signal.** While `LongPollAckToken` is set, the State (long-poll) function returns **HTTP 200**
   (no error code; ETag/304 path unchanged) with an `interaction` object — grouping client directives
   under one key — but only for callers admitted by the interaction's authorization arm: the
   `roles` grants (default-allow when none) or the condition `rule` (see *Rule-based authorization*).
   The response still carries the entered state name + view href so the client can render.

   ```jsonc
   "interaction": {
     "terminateLongPoll": true,
     "ack": { "href": "/api/core/workflows/account-opening/instances/{id}/longpoll/ack" }
   }
   ```

   The `ack` href follows the same `{ "href": "…" }` shape as `data`/`view`. The `interaction` object
   is omitted entirely when no directive applies.

   **Presence follows `Instance.IsAwaitingLongPollAck`, not the state declaration.** A state may
   declare `interaction.longPoll` and the instance still not be parked on it — the token is armed
   only when the pipeline actually pauses at step 75, and the acknowledge or the fallback timeout
   clears it again. Emitting the block from the declaration alone told the client to acknowledge
   something no longer pending; nothing broke loudly, because the endpoint answers `Ok()`
   idempotently there and `authorize?ack=true` answers *allowed* for the same reason, so the client
   simply posted an ack on every poll of that state and read success back. `ResponseShapeVersion` was
   bumped (v10 → v11) in the same change: a client parked behind a 304 must not keep being served the
   old presence rule.
3. **Acknowledge.** The client stops polling, renders the screen, and `POST`s to
   `…/instances/{instance}/longpoll/ack`. The endpoint runs the same authorization arm as the signal
   (role grants or rule), best-effort cancels the fallback job, and resumes the pipeline.
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
- State signal — `InstanceQueryAppService.ResolveInteractionAsync`
- Acknowledge — `InstanceController.AcknowledgeLongPollAsync` → `InstanceCommandAppService.AcknowledgeLongPollAsync`
- Interaction gate (both surfaces; rule/roles/allow arm selection) — `LongPollInteractionGate` — `src/BBT.Workflow.Application/Execution/LongPoll/`

## Change-Safety

- The pause/resume reuses the SubFlow resume plumbing (`IsInternalResume`); changes to either must
  preserve the shared lock-key, validation-bypass, and busy-confirmation behavior.
- The instance stays Busy during the ack window; do not re-mark Busy on long-poll resume (a redundant
  resume must not strand an already-advanced instance).

## Parent override

When the instance is a SubFlow child, its parent may override `fallbackTimeoutSeconds` and `roles`
per child state (`overrides.states.<state>.interaction.longPoll`, field-level). Every reader goes
through `Instance.ResolveEffectiveLongPoll` — never `State.LongPollFallbackTimeoutSeconds` /
`LongPollAckRoles` directly. `terminate` and `rule` are not overridable. Details:
[SubFlow Overrides](subflow-overrides.md).
