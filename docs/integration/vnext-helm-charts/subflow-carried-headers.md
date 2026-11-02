---
product: vnext-helm-charts
milestone: v0.0.102
status: pending
---

# SubFlow carried headers — Handoff for vnext-helm-charts

## Context

- vNext PR: (link added when the PR opens) — branch `feature/subflow-terminal-carried-headers`
- Milestone: v0.0.102
- Decision: council `2026-10-29-subflow-terminal-headers` + requester amendments RA-1/RA-2/RA-3

## What changed in the runtime

When a SubFlow child settles, the orchestration host carries the headers of the request that settled
it to the parent. A new configuration section `Workflow:SubFlow:CarriedHeaders` controls this. All
keys have code defaults, so a chart that sets nothing gets the default behaviour.

**Upgrade behaviour change:** with the default `ApplyToResume=true`, the parent's resumed hop now runs
with the finisher's headers instead of none. That covers auto conditions, onEntry tasks,
caller-credential forwarding and the next subflow's input mapping. A domain that depends on the old
empty resume must set `ApplyToResume=false`.

Source of truth: `src/BBT.Workflow.Application/SubFlow/Headers/SubFlowCarriedHeadersOptions.cs` and
`docs/runtime/event-publish-modes.md` § "Carried headers on sub-item terminal events".

## What this product must do

- Expose the section as optional per-environment values on the **orchestration** deployment. Leave the
  values unset by default so the code defaults apply.
- Document the opt-out (`ApplyToResume=false`) and `AdditionalDenied` in the chart README or values
  comments. `AdditionalDenied` is for domains with proprietary credential headers, such as
  `x-api-key`.
- No change for the execution, inbox or outbox deployments. The policy runs in orchestration, both for
  the in-process relay and for the Inbox→HTTP backup that lands on orchestration.

## Contract and examples

| Environment variable | Default | Meaning |
|---|---|---|
| `Workflow__SubFlow__CarriedHeaders__Emit` | `true` | `false` stops carrying headers entirely (kill switch) |
| `Workflow__SubFlow__CarriedHeaders__ExposeToOutputMapping` | `true` | Parent output mapping sees them as `context.Headers` |
| `Workflow__SubFlow__CarriedHeaders__ApplyToResume` | `true` | Parent resume applies them; `false` = stricter old behaviour |
| `Workflow__SubFlow__CarriedHeaders__AdditionalDenied__0` … `__N` | (none) | Extra exact header names never carried (adds to the code floor) |
| `Workflow__SubFlow__CarriedHeaders__MaxEntries` | `64` | Max carried headers |
| `Workflow__SubFlow__CarriedHeaders__MaxValueLength` | `4096` | Max characters per value |

Startup validation fails the host when `MaxEntries` or `MaxValueLength` is not positive, or when
`AdditionalDenied` is null.

## Acceptance

- A values file can set each key per environment; an unset key leaves the code default.
- With `ApplyToResume: false` rendered, the orchestration pod's environment contains
  `Workflow__SubFlow__CarriedHeaders__ApplyToResume=false`.

## Out of scope

- The deny floor (`authorization`, `cookie`, `*token*`, `*secret*`, `*session*`) and the framework
  exclusions are code-enforced. They are not configurable and must not be modelled as chart values.
