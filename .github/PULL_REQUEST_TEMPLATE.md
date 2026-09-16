<!--
Title: Conventional Commits, English, imperative, max 72 chars — `<type>(<scope>): <subject>`
Types: feat, fix, refactor, perf, test, docs, chore, ci

This template is the shape `create-github-pr` drafts and `pr-review` checks. Remove a section that
genuinely does not apply rather than leaving its placeholder text in place.
-->

## Summary
<!-- 2-4 bullets: what changed and why -->
-

## Changes
<!-- Key files / areas touched -->
-

## Test Plan
<!-- How a reviewer verifies this -->
- [ ]

## Integration test evidence
<!--
Required when the change touches a core process — pipeline, transitions, subflows, locking,
instance data, error boundary, state function — or carries regression risk.
Contract: docs/testing/integration-testing.md §10. Remove this section when no integration run
was part of the change; do not leave it filled with placeholders.
-->
- Scenario(s): `vnext-example` `Tests/<Scenario>` (`--filter FullyQualifiedName~<Scenario>`)
- Runtime: commit `<sha>` at `VNEXT_BASE_URL=http://localhost:<port>` (`run-docker.sh up <domain>`)
- Result: `<passed>/<total>` green; remaining reds and their cause: `<environment defect | known gap | regression>`
- `TEST-SCENARIOS.md` row added/updated: yes / no

## Notes
<!-- Breaking changes (mirror any `BREAKING CHANGE:` footer with a ⚠️ note), migration steps,
     follow-ups in sibling repos (vnext-helm-charts, vnext-schema, vnext-example). Remove if none. -->
