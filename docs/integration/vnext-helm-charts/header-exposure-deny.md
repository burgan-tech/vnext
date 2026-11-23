---
product: vnext-helm-charts
milestone: v0.0.107
status: pending
---

# Header exposure deny list — Handoff for vnext-helm-charts

## Context

- vNext PR: (link added when the PR opens) — branch `claude/issue-112-analysis-55b8ce`
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1260

## What changed in the runtime

A new configuration section `Workflow:HeaderExposure` with one key, `Deny` (a list of header names).
A listed request header is not written to the transition record, is removed from the transition history
response and is absent from `context.Headers` in every script and mapping. The runtime's own reads
(roles, identity, forwarded credential) are unaffected.

The default is an empty list, and empty changes nothing. The runtime ships no default list: which
header is sensitive is the domain's decision.

Source of truth: `docs/runtime/header-exposure.md`.

## What this product must do

- Add an optional `Workflow__HeaderExposure__Deny` list and render it into every runtime deployment
  (orchestration, execution, inbox, outbox) with the same value — every host registers the
  application module, and any of them that runs a script must apply the same list. Default: empty —
  render nothing when it is empty.
- Document in the chart README that a domain lists its sensitive headers here, with the example
  below, and that the list is read at host start (a change needs a restart).

## Contract and examples

```yaml
env:
  - name: Workflow__HeaderExposure__Deny__0
    value: "authorization"
  - name: Workflow__HeaderExposure__Deny__1
    value: "cookie"
```

Names match exactly and case-insensitively; blank entries are ignored. No wildcards.

## Acceptance

- With the value unset, the rendered manifests carry no `Workflow__HeaderExposure__*` variable.
- With two names set, every deployment carries `__0` and `__1`; a transition started with one of them
  shows it neither in `GET …/instances/{id}/transitions` nor in a mapping.

## Out of scope

- Shipping a default list (Chair decision 2026-11-23: no default).
