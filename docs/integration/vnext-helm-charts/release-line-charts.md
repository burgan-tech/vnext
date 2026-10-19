---
product: vnext-helm-charts
milestone: v0.0.101
status: pending
---

# Parallel release lines — Handoff for vnext-helm-charts

Convention: [README](../README.md). Process: [Release lines](../../governance/release-lines.md).

## Context

- vNext PR: <to be filled when the PR opens>
- Milestone: v0.0.101
- Issue: none

## What changed in the runtime

The release workflow can publish a patch for an older minor line (`release-vX.Y`) next to a newer
one. It only dispatches the chart repository for the latest line, because the chart writes
`appVersion` from the payload unconditionally. Source of truth:
[`build-and-publish-images.yml`](../../../.github/workflows/build-and-publish-images.yml) job
`trigger-helm`.

## What this product must do

- Accept two new, additive payload keys: `is_latest` (`"true"`/`"false"`) and `release_line`
  (`release-vX.Y`). Unknown keys are ignored today, so nothing breaks before this lands.
- Never lower `appVersion` on the main chart: it follows the latest line only.
- To support patch lines, decide whether the chart gets a per-line chart version or deployments pin
  the image tag `release-vX.Y` / `X.Y.Z` through values. Until then patch-line deployments set the
  image tag through values.
- Document the choice in the chart README.

## Contract and examples

Payload keys on the existing dispatch: `is_latest`, `release_line`. The workflow does not dispatch
at all when `is_latest` would be `false`, so a chart that wants older-line releases needs the
dispatch condition changed on the vNext side too — raise it before building on it.

## Acceptance

- A latest-line release still updates `appVersion` exactly as before.
- Receiving `is_latest: false` never lowers `appVersion`.

## Out of scope

Automatic chart releases per line; any change to the runtime image tags.
