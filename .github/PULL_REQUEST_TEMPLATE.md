<!--
Title: Conventional Commits, English, imperative, max 72 chars — `<type>(<scope>): <subject>`

Contract tiers (checked by the `PR Contract` workflow, `scripts/governance/pr-contract.mjs`):
- Full — `feat`, `fix`, `perf`, `refactor` (also with `!`): every required section below is filled
  and the PR carries an open milestone `vX.Y.Z`.
- Light — `test`, `docs`, `chore`, `ci`: a Conventional title and `## Summary` only; delete the
  other sections or leave them.
- Exempt — a release merge titled `vX.Y.Z`.
Required sections: Summary, vNext scope, E2E evidence, Product impact, vnext-meta impact.
Optional: Changes, Test Plan, Notes. A `<...>` placeholder counts as blank.
Rule ids and what each one checks: docs/code-review/reviewers/evidence.md §4.

Generate the E2E evidence block from the TRX of the vnext-example run:
  node scripts/governance/e2e-evidence.mjs --trx <file>... --runtime-sha <sha> --base-url <url> [--reds "<text>"]
Check a draft body before opening the PR:
  node scripts/governance/pr-contract.mjs --body-file <draft.md> --title "<title>" --milestone vX.Y.Z --files-from-git origin/master
-->

## Summary
<!-- 2-4 bullets: what changed and why -->
-

## vNext scope
<!-- Which runtime areas this touches (pipeline, transitions, subflows, locking, instance data,
     error boundary, state function, API contract, hosts, workers …) and whether runtime
     behaviour changes. "No runtime behaviour change" is a valid answer when it is true. -->
-

## E2E evidence
<!--
Paste the output of `scripts/governance/e2e-evidence.mjs` (Status: run, Runtime, Base URL, Result,
suite table, Reds). The Runtime SHA must be one of this PR's commits; every failed test needs a Reds
line. When no run was part of the change, write instead:
  - Status: not-run
  - Reason: <why no end-to-end run was needed or possible, at least 10 characters>
Policy: docs/testing/integration-testing.md §1; block format: §10.
-->
- Status: <run | not-run>

## Product impact
<!--
One row per product, in this order. Needed = yes when the product must change because of this PR.
A yes row links a handoff: a docs/integration/<product>/<slug>.md file added or changed in this PR
(convention: docs/integration/README.md), or a https://github.com/burgan-tech/<product>/pull/<n> or
/issues/<n> URL.
-->
| Product | Needed | Handoff |
|---|---|---|
| vnext-schema | no | |
| vnext-forge | no | |
| vnext-helm-charts | no | |
| vnext-docs | no | |
| vnext-workflow-cli | no | |
| vnext-runtime | no | |
| vnext-template | no | |
| vnext-integration-test | no | |
| vnext-example | no | |
| vnext-ai-toolkit | no | |

## vnext-meta impact
<!--
updated — a vnext-meta/*.json file changes in this PR (run `node scripts/governance/meta-lint.mjs`).
not-needed — nothing in vnext-meta describes this change; give the reason.
-->
- Status: <updated | not-needed>
- Reason: <only for not-needed>

## Changes
<!-- Optional. Key files / areas touched -->
-

## Test Plan
<!-- Optional. How a reviewer verifies this -->
- [ ]

## Notes
<!-- Optional. Breaking changes (mirror any `BREAKING CHANGE:` footer with a ⚠️ note), migration
     steps, follow-ups. Remove if none. -->
