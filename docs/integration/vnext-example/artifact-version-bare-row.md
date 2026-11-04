---
product: vnext-example
milestone: v0.0.104
status: pending
---

# Artifact-version resolution with a bare row — Handoff for vnext-example

## Context

- vNext PR: <https://github.com/burgan-tech/vnext/pull/PR_NUMBER>
- Milestone: v0.0.104
- Issue: <https://github.com/burgan-tech/vnext/issues/1164>

## What changed in the runtime

A component reference by artifact version, such as a state view `{ "key": "...", "flow": "sys-views", "version": "1.0.0" }`,
now resolves to the highest package-stamped row of that artifact (`1.0.0-pkg.X.Y.Z+domain`), even when a bare
`1.0.0` row is stored next to it. Before this change the bare row matched exactly and shadowed every package version.
A bare row is returned only when no `-pkg.` row exists for that artifact.

Source of truth: `InstanceDataVersionComparer.FindBestMatch`
(`src/BBT.Workflow.Domain/Instances/InstanceDataVersionComparer.cs`). It is shared by the component cache
(`IComponentCacheStore.GetByVersionAsync`) and by `EfCoreInstanceRepository.FindActiveDataAsync`.

## What this product must do

- Extend the existing view / instance-data scenario in `tests/Core.IntegrationTests`. Do not add a new one if an
  existing scenario fits. The scenario stores a bare `1.0.0` row and at least two `1.0.0-pkg.*` rows of one view
  in `sys-views`, references the view from a state with `"version": "1.0.0"`, and asserts that the View function
  returns the content of the highest pkg row (the `IsLatest` row).
- Update the scenario README and its `TEST-SCENARIOS.md` row in the same commit.

## Contract and examples

```json
"view": { "view": { "key": "<view-key>", "domain": "core", "flow": "sys-views", "version": "1.0.0" } }
```

The stored versions are `1.0.0`, `1.0.0-pkg.0.0.1+core` and `1.0.0-pkg.0.0.2+core`. The View function returns
the content of `1.0.0-pkg.0.0.2+core`.

## Acceptance

- The scenario passes against the locally built runtime from the vNext PR's head.
- The same scenario fails against a runtime built before this change.

## Out of scope

- Cleaning up legacy bare rows written by older publish paths.
