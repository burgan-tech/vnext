# AI Capabilities — what exists, when to use it, how to run it

One catalog of every AI capability that ships with this repository — skills, subagents, MCP servers,
hooks and scripts — plus the machine-level plugins that are commonly installed beside it. Each entry
says what it does, when to reach for it, how to start it and where its boundaries are; the detail lives
in the linked file, never here. This page is read on demand and is not loaded into every session.

Skills are started by saying one of their trigger phrases (or `/<skill-name>`); Claude Code matches the
phrase against the skill's frontmatter `description`. Cursor reads the same `.claude/skills/` folder.

## 1. Quick chooser

| I want to… | Use |
|---|---|
| Decide on an architecture or technology choice | [`agent-council`](#2-decision--agent-council) |
| Review an open PR | [`pr-review`](#3-review) (`#N`, `--no-comment`) |
| Review a local diff | [`workflow-code-review`](#3-review) |
| Check a domain package / its flows | [`domain-performance-audit`](#5-domain-package--flow-checks) (→ plugin `domain-check`) + plugin `review-components` / `validate-and-fix` |
| Prove a runtime change end to end | [`runtime-integration-test`](#4-testing-and-verification) |
| Test cross-domain behaviour | [`cross-domain-lab`](#4-testing-and-verification) |
| Open an issue / a PR / write a commit message | [`create-github-issue` / `create-github-pr` / `git-commit-message`](#6-github-and-git) |
| Check or report on vnext-meta | [`vnext-meta-validator` / `vnext-meta-matrix`](#7-vnext-meta-and-documentation) |
| Write product documentation | [`vnext-docs-generator`](#7-vnext-meta-and-documentation) |
| Verify a change through traces and logs | [MCP servers + `scripts/trace-profile.py`](#4-testing-and-verification) |
| Check that the AI guidance itself is healthy | [`scripts/check-ai-guidance.sh`](#9-always-on-guards-no-invocation-needed) |

## 2. Decision — Agent Council

**`agent-council`** — [skill](../.claude/skills/agent-council/SKILL.md) ·
[rule](../.claude/rules/agent-council-plan-mode.md) · [process](agent-council/PROCESS.md)

- **Does:** an evidence-based decision: task record → smallest sufficient council (roles by risk) →
  independent proposals → at most two objection rounds → decision with dissent, rollback and
  verification plan. Outcome: `APPROVED` / `EXPERIMENT_REQUIRED` / `CLARIFICATION_REQUIRED` /
  `BLOCKED` / `REJECTED`.
- **When:** "council", "karar verelim", "eklemeli miyim", "mimari karar" — and, with no phrase, any
  question whose honest answer is a recommendation (new component, service boundary, technology,
  data model, auth boundary, performance claim, deployment change). Every such answer must open by
  stating whether the council ran.
- **Run:** *"council: should we add a post-commit relay for event X?"*
- **Produces / boundary:** a session folder under `ai-docs/agent-council/sessions/<id>/` (git-ignored)
  and one row in [the committed log](agent-council/sessions/README.md). Planning only — it never writes
  code and its output is not implementation approval.

## 3. Review

**`pr-review`** — [skill](../.claude/skills/pr-review/SKILL.md) · checklists [docs/code-review/](code-review/README.md)

- **Does:** classifies the diff, runs the four read-only reviewers in parallel
  (`pr-reviewer-pipeline`, `pr-reviewer-platform`, `pr-reviewer-contract`, `pr-reviewer-evidence`),
  merges and de-duplicates their findings into one severity-ranked report.
- **When:** "pr review", "PR incele", "PR kontrol", "review PR #N".
- **Run:** *"PR incele #1041"*, a PR URL, or no argument (the current branch's PR; with no PR it runs in
  **pre-PR mode** against `origin/master`). Add `--no-comment` to skip the comment step.
- **Produces / boundary:** a report in chat. The sticky PR comment is optional and is posted only after
  you approve it — never in pre-PR mode. Reads the PR through `gh`.

**`pr-review-lead`** (subagent) — [definition](../.claude/agents/pr-review-lead.md)

- Same procedure, run outside the main conversation's context — for a large PR, an automated run or
  several PRs in a row. Returns the merged report; never writes to GitHub.

**`workflow-code-review`** — [skill](../.claude/skills/workflow-code-review/SKILL.md)

- **Does:** the same `docs/code-review/` checklists against a **local** diff, single session, no PR.
- **When / run:** "code review", "review et", "diff review" — unstaged, staged (`git diff --cached`) or
  a range you name.
- **Boundary:** read-only; for an open PR use `pr-review`.

Finding format and noise rules shared by all reviewers:
[code-review/README.md](code-review/README.md). General-purpose built-ins that know nothing about vNext:
`/code-review` (`--comment`, `--fix`) and `/simplify`.

## 4. Testing and verification

**`runtime-integration-test`** — [skill](../.claude/skills/runtime-integration-test/SKILL.md) ·
contract [testing/integration-testing.md](testing/integration-testing.md)

- **Does:** proves a core-process change (pipeline, transitions, subflows, locking, instance data,
  error boundary, state function) with vnext-example's `tests/Core.IntegrationTests` against the
  **locally built** runtime — never a container image.
- **When:** "integration test", "entegrasyon testi", "vnext-example'da test et", "e2e doğrula"; also on
  any core-process change that needs end-to-end proof. Whether a test is required at all:
  [policy table](testing/integration-testing.md#1-policy--when-an-integration-test-is-required) —
  small isolated fixes do not need one; when unsure it proposes and waits.
- **Run:** `cd etc/docker && ./run-docker.sh up <domain> [--offset N] [--with <d1,d2=db>]`, then in `../vnext-example`:
  `dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~<Scenario>"`
  with `VNEXT_BASE_URL` pointing at the local orchestration host.
- **Produces / boundary:** counts, runtime commit and the cause of every red. A new scenario needs a
  README and a `TEST-SCENARIOS.md` row in the same commit.

**`cross-domain-lab`** — [skill](../.claude/skills/cross-domain-lab/SKILL.md)

- **Does:** runs the three-domain lab (core + partner + discovery) from vnext-example
  `labs/cross-domain/` for Dapr name resolution and invocation, the discovery registry,
  SubFlow/SubProcess/trigger tasks and function descent.
- **When:** "cross-domain test", "çapraz domain", "iki domain", "partner domain", "useDapr test".
- **Run:** `labs/cross-domain/lab.sh status | images | up | down | verify | logs`; after a runtime change
  `lab.sh images` → `down` → `up`. Rollback drill: `VNEXT_LAB_DISCOVERY_PROVIDER=http lab.sh up`.
- **Boundary:** checks state before restarting; never takes down a stack owned by another compose file.

**MCP servers** — [`.mcp.json`](../.mcp.json) · rule
[mcp-observability-verification](../.claude/rules/mcp-observability-verification.md) · reference
[testing/observability-verification.md](testing/observability-verification.md)

| Server | Use it for | Notes |
|---|---|---|
| `openobserve` | Runtime logs and the span tree (avg/p95/max, root cause by `instanceId`, `flow`, `transitionKey`, trace id) | org `default`, stream `vnext`, top-level `type`, microsecond times |
| `postgres` | What was actually persisted | restricted access mode; needs core's DbMigrator to have run |
| `redis` | Cache and lock state | use the read tools only unless asked |
| `elasticsearch` | APM traces | full docker profile |

They answer only while the docker infra is up (`etc/docker/run-docker.sh status`); the SessionStart
hook [`check-mcp-infra.sh`](../.claude/hooks/check-mcp-infra.sh) reports which backends are down at
session start. Rule: a green test run is not evidence — quote the measured numbers.

**`scripts/trace-profile.py`** — [script](../scripts/trace-profile.py)

- Span profiling against Elastic APM: `python3 scripts/trace-profile.py profile [--since 2h] [--service vnext-orchestration] [--only Cache.,Db.] [--sort total|count|p95]`
  for where time goes and whether caches hit; `python3 scripts/trace-profile.py trace <trace-id>` for one
  trace with inclusive and self time.

**Local environment** — `etc/docker/run-docker.sh status | up <domain> [--offset N] [--with <d1,d2=db>] | plan | down`,
records in `ai-docs/local-environments/<domain>.md`; full runbook in
[AGENTS.md](../AGENTS.md#runbook-bring-up-domain-x-for-agents).

## 5. Domain package / flow checks

**`domain-performance-audit`** — [skill](../.claude/skills/domain-performance-audit/SKILL.md), a pointer to
the vnext-ai-toolkit plugin's **`domain-check`** (canonical checks, scanner and report format:
[burgan-tech/vnext-ai-toolkit](https://github.com/burgan-tech/vnext-ai-toolkit) `skills/domain-check/`)

- **Does:** a static performance audit of a **domain package** (a vnext-template repo such as
  vnext-onboarding) — workflows, tasks, extensions, functions, views, scripts — against the current
  runtime, then a short prioritised report (in Turkish, since it goes to the domain team).
- **When:** "domain performans", "performans incele", "bu domaini incele", "domain audit".
- **Run:** `/vnext-ai-toolkit:domain-check <domain-root>` with the plugin installed; otherwise the scan
  step is `python3 <vnext-ai-toolkit>/skills/domain-check/scan.py <domain-root> [--json]`. From this
  repo, additionally confirm runtime claims against the code (the pointer skill says how).
- **Boundary:** read-only, writes nothing to the target. Not for a diff of this repo — use `pr-review` /
  `workflow-code-review`. Fix a wrong check in the plugin, not here.

Component correctness (schema validity, references, security) is the vnext-ai-toolkit plugin's job —
`validate-and-fix`, `review-components`, `security-audit` in [§8](#8-machine-level-plugins-and-tools-if-installed).

## 6. GitHub and git

| Skill | Does | When | Boundary |
|---|---|---|---|
| [`create-github-issue`](../.claude/skills/create-github-issue/SKILL.md) | Structured English issue from a scope in any language ([template](../.claude/skills/create-github-issue/TEMPLATE.md)); "projeyi tara" enriches it from the code | "issue aç", "issue oluştur", "open issue" | Shows the draft first; `gh` only after approval |
| [`create-github-pr`](../.claude/skills/create-github-pr/SKILL.md) | PR title and body from the branch's commits, following [`.github/PULL_REQUEST_TEMPLATE.md`](../.github/PULL_REQUEST_TEMPLATE.md) | "PR oluştur", "open PR", "pull request" | Three separate gates: draft → push → `gh pr create`; never force-pushes |
| [`git-commit-message`](../.claude/skills/git-commit-message/SKILL.md) | Conventional Commits message from the staged diff; the single source of commit/PR types | "commit mesajı", "git commit" | Never commits unless asked |

Nothing is written to GitHub, committed or pushed without an explicit approval.

Governance scripts (Node built-ins only; tests: `node --test "scripts/governance/test/*.test.mjs"`).
Rule ids: `PR-*` in [reviewer: evidence §4](code-review/reviewers/evidence.md), `META-*` in the
[`vnext-meta-validator` checks](../.claude/skills/vnext-meta-validator/CHECKS.md).

| Script | Does | Run |
|---|---|---|
| [`scripts/governance/pr-contract.mjs`](../scripts/governance/pr-contract.mjs) | Checks a PR body, title, milestone and handoff docs against [the template](../.github/PULL_REQUEST_TEMPLATE.md), then lints `vnext-meta/`; on a PR with `ai:` labels also the AI decision record and the declared impact against [`.github/ai/impact-paths.json`](../.github/ai/impact-paths.json) (`PR-AI-*`) | CI: `PR Contract` workflow on every non-draft PR (`PR_CONTRACT_MODE` = `warn` / `enforce`). Locally: `--pr <n>`, or a draft with `--body-file <path> --title "<t>" [--milestone vX.Y.Z] [--files-from-git <base>] [--labels a,b]` |
| [`scripts/governance/e2e-evidence.mjs`](../scripts/governance/e2e-evidence.mjs) | Renders the PR's `## E2E evidence` block from vnext-example TRX files | `--trx <file>... --runtime-sha <sha> --base-url <url> [--reds "<text>"]` |
| [`scripts/governance/meta-lint.mjs`](../scripts/governance/meta-lint.mjs) | Deterministic lint of `vnext-meta/` (parse, shape, manifest order, `since` timeline, registry keys in code) | `[--milestone vX.Y.Z] [--json]`; also part of every PR Contract run, regardless of title |
| [`scripts/governance/meta-stamp.mjs`](../scripts/governance/meta-stamp.mjs) | Release stamp: turns `vnext-meta/unreleased/` fragments into versioned data files, adds the manifest row, records and deletes the fragments, deletes back-merge leftovers ([format](../vnext-meta/unreleased/README.md)) | `--version X.Y.Z [--date YYYY-MM-DD] [--schema-version X.Y.Z] [--meta-dir <path>] [--notes-out <file>] [--dry-run]`; CI: the release `prepare` job, lint → stamp → lint |
| [`scripts/governance/meta-sync.mjs`](../scripts/governance/meta-sync.mjs) | Syncs a patch line's released `vnext-meta/` into master (every manifest row master lacks, union of ids and feature keys, lower version wins) | `--source <dir> --target <dir> [--dry-run]`; CI: `sync-meta-to-master` job of the release workflow, for lines that are not the latest ([process](governance/release-lines.md)) |
| [`scripts/ci/unit-tests-report.mjs`](../scripts/ci/unit-tests-report.mjs) | Summarises unit-test TRX files (failures grouped by error signature) and fails on any failure — the suite is kept green ([policy](testing/unit-tests.md)) | CI: `Unit Tests` workflow ([`pr-tests.yml`](../.github/workflows/pr-tests.yml); `UNIT_TESTS_MODE=report` is an outage-only switch). Locally: `--trx <file>... [--mode enforce\|report] [--summary <md>] [--failures-out <json>]`; tests: `node --test "scripts/ci/test/*.test.mjs"` |
| [`.github/workflows/backport.yml`](../.github/workflows/backport.yml) | Opens a backport PR to `release-vX.Y` for a merged PR labelled `backport/release-vX.Y` | CI: `pull_request_target` on `closed` / `labeled`; process: [Release lines](governance/release-lines.md) |

**Issue triage** — the one automation that writes to GitHub without a per-action approval, and only
to issues: [`.github/workflows/issue-triage.yml`](../.github/workflows/issue-triage.yml) runs on
issue `opened` / `edited` / `reopened`, and on a manual run for one issue or a sweep of at most 20
untriaged ones. A
read-only Cursor CLI run (`agent -p --mode ask`, no `--force`) follows
[`.github/triage/TRIAGE.md`](../.github/triage/TRIAGE.md) and prints a JSON result; [`scripts/triage/apply.mjs`](../scripts/triage/apply.mjs) validates it against
[`.github/triage/labels.json`](../.github/triage/labels.json) and upserts one sticky comment, and in
`apply` mode adds labels (never removes one, never overrides a human kind / priority / type, never
applies `priority:P0`) and sets the issue type; it then fills what is still empty from the `defaults`
block of `labels.json` — milestone `backlog`, the org issue fields Priority (from the priority label,
never Urgent) and Effort (the agent's estimate), and the `vNext` org project with `Project` =
`platform` and its own Priority mirroring the issue's. Issue fields and the project use the GitHub App token (`APP_ID` / `APP_PRIVATE_KEY`; the
app needs Issues and Organization projects read/write) and are skipped with a warning without it. Repository variable `TRIAGE_MODE` = `dry-run`
(default, comment only) / `apply`; secret `CURSOR_API_KEY` (the
shared CI Cursor account), without which the job is a no-op; `TRIAGE_MODEL` overrides the pinned model
(`claude-sonnet-5-5-medium`; names from `agent --list-models`). Labels: `node scripts/triage/sync-labels.mjs [--apply]` writes every group of `labels.json`, the pipeline's
`ai:*` groups included, and never deletes. Tests (also run by the `Unit Tests` workflow on every PR):
`node --test "scripts/triage/test/*.test.mjs"`. Issue text is untrusted (public repo): the agent has
no shell, write, network or GitHub access, and a changed working tree blocks the apply step.

**AI engineering pipeline** *(being rolled out)* — extends the same model to planning,
council, implementation and PR review, driven by `ai:*` labels:
[AI engineering pipeline](governance/ai-engineering-pipeline.md). Built so far: the **Director**
(`.github/workflows/ai-director.yml`, prompt `.github/ai/director.md`, scripts `scripts/ai/director/`)
plans an issue when a maintainer adds `ai:ready` / `ai:replan`; `AI_MODE` = `dry-run` (default, plan
comment only) / `apply` (labels via the GitHub App); model `AI_DIRECTOR_MODEL`. The **Quality Gate**
(`.github/workflows/ai-quality-gate.yml`, prompt `.github/ai/quality-gate.md`, evidence list
`.github/ai/gate.json`, scripts `scripts/ai/gate/`) reviews every open PR into `master` /
`release-v*` once its CI reported, one comment per PR; model `AI_GATE_MODEL`, repair limit
`AI_MAX_REPAIRS`. Locally: `SONAR_TOKEN=… node scripts/ai/gate/collect.mjs --pr <n> --force --out <dir>`
then `apply.mjs --in <dir> --print`. Locally:
`node scripts/ai/director/collect.mjs --issue <n>` then `apply.mjs --print`. Tests:
`node --test "scripts/ai/test/*.test.mjs"` (also in the `Unit Tests` workflow).

## 7. vnext-meta and documentation

| Skill | Does | When | Produces |
|---|---|---|---|
| [`vnext-meta-validator`](../.claude/skills/vnext-meta-validator/SKILL.md) | Schema, version and codebase alignment of the `vnext-meta/` JSON files ([checks](../.claude/skills/vnext-meta-validator/CHECKS.md)) | "meta kontrol", "validate meta"; after any `vnext-meta/` edit | A findings report |
| [`vnext-meta-matrix`](../.claude/skills/vnext-meta-matrix/SKILL.md) | Feature / deprecation / known-issue / component / limit matrix by version | "meta matrix", "meta rapor" | `ai-docs/vnext-meta-matrix.md` (local) |
| [`vnext-docs-generator`](../.claude/skills/vnext-docs-generator/SKILL.md) | Creates or updates pages of the vnext-docs Docusaurus portal ([reference](../.claude/skills/vnext-docs-generator/REFERENCE.md)) | "döküman oluştur", "create docs" | Edits in the local `../vnext-docs` clone; `gh api` only when there is no clone |

## 8. Machine-level plugins and tools (if installed)

Not part of this repository — they are installed per user and may be missing on another machine.

- **vnext-ai-toolkit** — domain-package development (not this runtime):
  - design: `vnext-design-process` (multi-turn workflow design), `workflow-scaffold` (workflow JSON + `.csx` + `.http`);
  - components: `component-task`, `component-function`, `component-extension`, `component-mapping`,
    `schema-design`, `view-design`;
  - checks: `validate-and-fix` (`npm run validate` + fixes), `review-components` (reviewer +
    security-reviewer sweep), `security-audit` (OWASP-style report);
  - tests and setup: `integration-test` (for domain teams' own workflows), `vnext-init` (workspace setup).
- **burgan-tools** — bank-wide analysis: `analyze`, `impact-analyze` (cross-repo impact of a change),
  `brd`, `drd`, `vnext-plan` / `vnext-execute` (the `VNEXT-BUILD-PLAN.md` gate before building a
  domain flow). They need the `burgan` MCP server, which only resolves on the internal network / VPN.
- **graphify** — `/graphify` builds `graphify-out/graph.json`; then `graphify path "X" "Y"`,
  `graphify explain "X"`, `graphify query "<question>"`. Use it before broad grepping:
  [navigation rule](../.claude/rules/graphify-navigation.md).
- **Claude Code built-ins** — `/code-review`, `/simplify`, `/loop` (repeat a task on an interval),
  `/schedule` (cloud routines).

## 9. Always-on guards (no invocation needed)

| Guard | What it enforces |
|---|---|
| [Council plan mode](../.claude/rules/agent-council-plan-mode.md) | Council-shaped questions disclose council status; decisions before implementation |
| [MCP verification](../.claude/rules/mcp-observability-verification.md) | A green test is not evidence; say when a server was unavailable |
| [Graphify first](../.claude/rules/graphify-navigation.md) | Query the graph before reading broadly |
| [Workflow card](../.claude/rules/vnext-workflow-developer.md) and [.NET standards](../.claude/rules/dotnet-coding-standards.md) | Path-scoped: load once code under `src/`, the hosts, `workers/`, `modules/`, `test/` or `vnext-meta/` is open |
| [`check-mcp-infra.sh`](../.claude/hooks/check-mcp-infra.sh) | SessionStart: lists unreachable MCP backends; starts nothing |
| [`scripts/check-ai-guidance.sh`](../scripts/check-ai-guidance.sh) | Run after editing AI guidance: frontmatter, links, Cursor pointers, duplicate headings, size budget, catalog coverage |

## 10. Adding a new capability

- A skill goes in `.claude/skills/<name>/SKILL.md` with a quoted `description` of ≤ ~300 characters
  carrying its trigger phrases; long detail goes in a sibling file read on demand.
- A new rule needs a pointer in `.cursor/rules/` with the same scope
  ([editing the AI guidance](agent-onboarding.md#editing-the-ai-guidance)).
- Add its row to this page, then run `scripts/check-ai-guidance.sh` — a skill or agent missing from this
  catalog is an error.
