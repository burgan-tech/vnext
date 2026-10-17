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

- [ ] `evidence/guard-aether-feed` — no live Aether local feed: `nuget.config` has no active `aether-local` source/mapping and `Directory.Build.props` no `-local` `AetherPackageVersion`. Procedure and revert steps: [integration-testing §8](../../testing/integration-testing.md#8-aether-changes-during-development).
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

The mechanical checks of the PR body run in CI: the `PR Contract` workflow
(`.github/workflows/pr-contract.yml`) runs `scripts/governance/pr-contract.mjs` on every non-draft PR,
in `warn` or `enforce` mode (repository variable `PR_CONTRACT_MODE`, default `warn`). **Do not repeat
these checks** — read the workflow's annotations and job summary (or run
`node scripts/governance/pr-contract.mjs --pr <n>` locally — replay limits in [integration testing §10](../../testing/integration-testing.md#10-report-the-result)), quote any finding by its id, and spend the
review on the semantic questions below. This table is the single list of the rule ids; the PR template
and the skills point here.

| Id | What it checks |
|---|---|
| `PR-TITLE` | The title is Conventional Commits (`feat`, `fix`, `perf`, `refactor` = full contract; `test`, `docs`, `chore`, `ci`, `style`, `revert` and GitHub's auto title `Revert "…"` = light contract, `## Summary` only), at most 72 characters; a release title `vX.Y.Z` is exempt from every `PR-*` rule (the `META-*` lint still runs). |
| `PR-MILESTONE` | A full-contract PR carries an open milestone named `vX.Y.Z`. |
| `PR-SECTION` | Each required section (`Summary`, `vNext scope`, `E2E evidence`, `Product impact`, `vnext-meta impact`) exists and is not blank; comments and `<...>` placeholders count as blank. |
| `PR-E2E-STATUS` | `## E2E evidence` declares `Status: run`, or `Status: not-run` with a `Reason` of at least 10 characters. |
| `PR-E2E-RUN` | A `run` block has a runtime SHA that is one of the PR's commits, a `Result` line, at least one suite row, and a `Reds` line when anything failed. |
| `PR-PRODUCT-ROWS` | `## Product impact` has exactly one `yes`/`no` row for each of the ten products and no unknown product. |
| `PR-HANDOFF` | Each `yes` row links a `docs/integration/<product>/*.md` file changed in this PR, or a PR/issue URL in `burgan-tech/<product>`. |
| `PR-HANDOFF-DOC` | A linked handoff doc has frontmatter with the right `product`, the PR's `milestone`, and `status` `pending`/`in-progress`/`done` ([convention](../../integration/README.md)). |
| `PR-META` | `## vnext-meta impact` says `updated` only when a `vnext-meta/*.json` file changed, and `not-needed` (with a reason) only when none did. |
| `META-*` | The same run lints `vnext-meta/` on every PR regardless of title (`scripts/governance/meta-lint.mjs`; rule ids in the [`vnext-meta-validator` checks](../../../.claude/skills/vnext-meta-validator/CHECKS.md)). |

What CI cannot judge, and this reviewer does:

- [ ] `evidence/pr-e2e-coverage` — the `## E2E evidence` run actually exercises the change: the suites in the table reach the code paths the diff touches, and a `not-run` reason is true for this diff (a core-process change with `not-run` contradicts §3). A well-formed block that proves something else is a finding.
- [ ] `evidence/pr-product-impact` — every product marked `no` is really unaffected: a new or changed component field, task type or error code with `vnext-schema` / `vnext-forge` / `vnext-docs` at `no`, a new configuration key with `vnext-helm-charts` at `no`, and so on. A `yes` row's handoff says what the product must do, not just that something changed.
- [ ] `evidence/pr-breaking` — a `BREAKING CHANGE:` footer anywhere in the commits is mirrored by a `⚠️ Breaking change` note in the body.

## 5. Measurement claims (`evidence/claim-*`)

The rule: **a green test run is not, by itself, a verified result.**

- [ ] `evidence/claim-unbacked` — the PR claims a performance, latency, recovery, security or compatibility improvement with no number, trace, query or persisted-row evidence behind it. WARNING, and the finding names which MCP server would have answered it (`openobserve` for spans and durations, `postgres` for persisted rows, `redis` for cache/lock state, `elasticsearch` for APM traces).
- [ ] `evidence/claim-mislabelled` — the PR says "verified" where the evidence was only a passing test suite. The honest phrasing is "tests passed, not confirmed against traces"; ask for the label to be corrected.
- [ ] `evidence/claim-duration-misread` — a duration is cited as a regression without checking the three known traps: an unexplained gap that is an episode traced elsewhere under its own lane anchor; a duration that is the scenario's own configured timeout or mock delay; and `POST /job/{jobName}`, which is Dapr's job callback and is not client latency.

## Reviewer-specific noise

The shared output contract is [Finding format and noise rules](../README.md#finding-format-and-noise-rules).
On top of it: **a false "this needs an integration test" is the most expensive noise this reviewer
can produce** — when genuinely unsure, raise a WARNING phrased as a question, never a demand. A
missing unit test is a WARNING that names the test project it belongs in. If `../vnext-example` is
not checked out, say so instead of guessing what is in it.
