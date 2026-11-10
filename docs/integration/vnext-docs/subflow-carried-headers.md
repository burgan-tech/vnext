---
product: vnext-docs
milestone: v0.0.102
status: pending
---

# SubFlow carried headers — Handoff for vnext-docs

## Context

- vNext PR: (link added when the PR opens) — branch `feature/subflow-terminal-carried-headers`
- Milestone: v0.0.102
- Example scenario: vnext-example `tests/Core.IntegrationTests/Tests/SubflowCarriedHeadersLab`

## What changed in the runtime

Before this change, a parent that waited on a SubFlow saw no request headers once the child finished:

- the parent's output mapping had an empty `context.Headers`;
- the parent's resumed hop had empty headers, so every subflow after the first started without the
  caller's identity.

Now the headers of the request that settled the child travel to the parent. "Settled" means it
reached a finish state via complete or exit, was cancelled, or faulted on a request:

- **Output mapping** (completion and fault): `context.Headers` holds them.
- **Resume** (completion, fault, cancel): by default the resumed hop runs with them. This covers auto
  conditions, onEntry tasks (which forward the caller credential), and the next subflow's input
  mapping. A domain opts out with `ApplyToResume=false`.
- **What is carried:** every header except
  - a fixed deny floor: any name containing `authorization`, `cookie`, `token`, `secret` or
    `session`;
  - framework headers (`x-subflow-depth`, parent/root/instance ids, `traceparent`, `content-*`,
    `dapr-*`, `grpc-*`, …).

  `role` is always carried. Keys arrive lower-cased.
- **What is not carried:** a child finished by a timer or timeout, a job-timeout recovery or a
  downward cascade has no caller request, so it carries nothing.

Source of truth: vnext `docs/runtime/event-publish-modes.md` § "Carried headers on sub-item terminal
events" and `docs/architecture/subflow-execution.md` § "Carried Headers from the Child".

## What this product must do

- In the SubFlow / mapping pages, document `context.Headers` in a SubFlow `OutputHandler`: what it
  contains, the deny floor, lower-cased keys, and empty for timer-finished children.
- Document that the parent's resumed hop now sees the finisher's headers by default, and the opt-out.
- Add an "upgrade note" for domain teams: scripts that assumed empty headers after a subflow change
  behaviour.

## Contract and examples

```csharp
// SubFlow OutputHandler on the parent
var sub = context.Headers?["sub"];            // identity of the user who finished the child
var lang = context.Headers?["accept-language"];
// context.Headers never contains authorization / cookie / *token* / *secret* / *session*
```

Configuration (section `Workflow:SubFlow:CarriedHeaders`): `Emit` (true), `ExposeToOutputMapping`
(true), `ApplyToResume` (true), `AdditionalDenied` ([]), `MaxEntries` (64), `MaxValueLength` (4096).

## Acceptance

- A domain developer can find, from the SubFlow page, which headers the parent sees and how to opt
  out.

## Out of scope

- Internal delivery details (relay, Inbox backup, re-arm) stay in the runtime repo docs.
