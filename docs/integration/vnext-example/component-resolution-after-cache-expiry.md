---
product: vnext-example
milestone: v0.0.104
status: pending
---

# Component Version Resolution After Cache Expiry — Handoff for vnext-example

## Context

- vNext PR: opened from branch `claude/pensive-feynman-eek1oj`
- Milestone: v0.0.104
- Issue: https://github.com/burgan-tech/vnext/issues/1192

## What changed in the runtime

A version resolution that misses the cache (after the publish-time warm entries expire) now logs the
candidate versions it was computed from (EventId 70010, Information) and tags them on the cache span
(`cache.candidate_count`, `cache.candidate_versions`, `cache.dropped_count`, `cache.resolved_version`).
A resolution computed from a partial candidate list (a stored version that could not be loaded) logs
EventId 70022 and is cached for `ComponentCache:NegativeTtlSeconds` only. Source of truth:
`src/BBT.Workflow.Application/Caching/CacheSet.cs`, `docs/domain/domain-cache-context.md`.

## What this product must do

- Add one scenario to `tests/Core.IntegrationTests` (README + `TEST-SCENARIOS.md` row in the same
  commit): publish three package builds of one flow (`A < B < C`), start instances by latest, by the
  artifact version and from a SubProcess task with `version: "<artifact>"`, then let the resolution
  entries expire and start again.
- Run the runtime with shortened TTLs so expiry fits a test run:
  `ComponentCache__GenerationTtlSeconds=30`, `ComponentCache__ResolutionTtlSeconds=30`.

## Contract and examples

- Expected: every new instance's `flowVersion` is `C` before and after expiry, with no pointer set.
- With a `sys-version-registry` pointer at `B`: latest → `B`, the artifact request → `C`.

## Acceptance

- The scenario passes against the locally built runtime.
- The miss-path log line (EventId 70010) for the flow lists `A`, `B` and `C` as candidates.

## Out of scope

- Diagnosing the reporter's environment (needs their `sys_flows` and `sys_version_registry` data).
