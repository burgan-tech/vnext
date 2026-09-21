# Reviewer: evidence

Owns the question the other three cannot answer: **is this change proven, and is the proof where the
next person will look for it?** Tests, the integration-test policy, the PR body's evidence sections,
and the guards that keep unprovable or unbuildable states out of a PR.

Source of truth: [Integration Testing a Runtime Change](../../testing/integration-testing.md),
[`.claude/rules/mcp-observability-verification.md`](../../../.claude/rules/mcp-observability-verification.md),
and the `runtime-integration-test` skill (the runnable procedure).

This reviewer runs on **every** review — its guards are cheap and catch things no human notices at
review time.

## 1. Hard guards — run these first (`evidence/guard-*`)

These are mechanical, fast, and each one is CRITICAL because CI or a later reader breaks on them.

- [ ] `evidence/guard-aether-feed` — `nuget.config` has no uncommented `aether-local` source and no live `packageSourceMapping` block for it, and `Directory.Build.props` has no `-local` `AetherPackageVersion`. CI cannot restore a `-local` version (integration-testing contract §8).
- [ ] `evidence/guard-local-paths` — no absolute machine path (`/Volumes/...`, `/Users/...`, `C:\...`) in a committed file. Sibling repos are referenced as `../<repo>`.
- [ ] `evidence/guard-secrets` — no token, password, connection string with credentials, or API key in a committed file, including `.http` files and test fixtures.
- [ ] `evidence/guard-ai-docs` — nothing under `ai-docs/` or a `CLAUDE.local.md` is staged.
- [ ] `evidence/guard-debug-leftovers` — no `Console.WriteLine`, commented-out block of the code being replaced, `// TODO` without an issue reference, or skipped/`Skip = "..."` test added by this PR without a stated reason.

## 2. Unit tests (`evidence/unit-*`)

- [ ] `evidence/unit-present` — changed behaviour has a test that fails without the change. A PR touching `src/` with no test change at all is a WARNING, and the reviewer names which test project it belongs in (`Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`).
- [ ] `evidence/unit-pins-invariant` — where the change restores or protects a documented invariant, the test pins the invariant itself, not the incidental symptom. The repo's existing pinning tests are the model: `SubStateChangeCoalescingTests`, `InstanceIncidentPersistenceTests`, `TaskStepIncidentPersistenceTests`, `RoleGrantEvaluatorTests`, `DynamicRoleGrantTests`.
- [ ] `evidence/unit-no-overmock` — the test asserts behaviour, not the call sequence of its own mocks.
- [ ] `evidence/unit-naming` — the test name states the claim; xUnit + NSubstitute/Shouldly per the repo's convention.

## 3. Integration-test policy (`evidence/it-*`)

The policy is a decision, not a preference — apply the table from the contract, do not soften it.

- [ ] `evidence/it-required` — the change touches a **core process** (pipeline, transitions, subflows, locking, instance data, error boundary, state function) or carries regression risk ⇒ an integration test is **required**. Missing one is CRITICAL; the finding names the core process touched.
- [ ] `evidence/it-not-required` — a small isolated fix, a log/doc/refactor change ⇒ not required; do **not** raise a finding. A false "needs an integration test" is the most expensive noise this reviewer can produce.
- [ ] `evidence/it-borderline` — genuinely unclear ⇒ raise it as a WARNING phrased as a question for the user, never as a demand.
- [ ] `evidence/it-location` — tests live in the sibling `../vnext-example` (`tests/Core.IntegrationTests`), never in this repo's `test/`, which is unit tests only.
- [ ] `evidence/it-local-runtime` — the evidence is against a **locally built** runtime via `VNEXT_BASE_URL`, not a container image and not Testcontainers mode. A claim verified against an image is worthless — the image carries released code.
- [ ] `evidence/it-scenario-artifacts` — a new scenario lands in one commit with: the flow, the test class deriving from `WorkflowTestBase` with an XML `<summary>`, a scenario `README.md`, a Python load script under `api-tests/<scenario>/` when measured under load, **and** a row in `TEST-SCENARIOS.md`.
- [ ] `evidence/it-readme-why` — the scenario README's "why it exists" section names the bug, issue/PR number and date. "Never leave it empty" is the contract's wording; an empty one is a WARNING.

## 4. PR body evidence (`evidence/pr-*`)

- [ ] `evidence/pr-template` — the body follows `.github/PULL_REQUEST_TEMPLATE.md`. Read that file for the section list; it also permits **removing** a section that genuinely does not apply, so a trimmed body is correct and a body still carrying the template's placeholder text is not.
- [ ] `evidence/pr-it-section` — when an integration test was required, `## Integration test evidence` names the scenarios run, the runtime commit, the base URL/offset, passed/failed counts, and the cause of every remaining red (contract §10). A section left as the template placeholder counts as missing.
- [ ] `evidence/pr-title` — Conventional Commits, ≤72 chars, English. A branch-name-derived title (`Feature/...`, `F/...`) is a WARNING.
- [ ] `evidence/pr-breaking` — a `BREAKING CHANGE:` footer anywhere in the commits is mirrored by a `⚠️ Breaking change` note in the body.

## 5. Measurement claims (`evidence/claim-*`)

The rule: **a green test run is not, by itself, a verified result.**

- [ ] `evidence/claim-unbacked` — the PR claims a performance, latency, recovery, security or compatibility improvement with no number, trace, query or persisted-row evidence behind it. WARNING, and the finding names which MCP server would have answered it (`openobserve` for spans and durations, `postgres` for persisted rows, `redis` for cache/lock state, `elasticsearch` for APM traces).
- [ ] `evidence/claim-mislabelled` — the PR says "verified" where the evidence was only a passing test suite. The honest phrasing is "tests passed, not confirmed against traces"; ask for the label to be corrected.
- [ ] `evidence/claim-duration-misread` — a duration is cited as a regression without checking the three known traps: an unexplained gap that is an episode traced elsewhere under its own lane anchor; a duration that is the scenario's own configured timeout or mock delay; and `POST /job/{jobName}`, which is Dapr's job callback and is not client latency.
