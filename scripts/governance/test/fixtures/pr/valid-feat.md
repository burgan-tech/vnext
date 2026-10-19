## Summary
- Adds the PR contract checker.

## vNext scope
Governance scripts only; no runtime behaviour change.

## E2E evidence
- Status: run
- Runtime: `3f2a9c1e5d`
- Base URL: `http://localhost:4201`
- Result: 10/10 passed, 0 failed, 0 skipped

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Core.IntegrationTests | 10 | 0 | 0 |

## Product impact
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
- Status: not-needed
- Reason: No runtime contract changed in this PR.
