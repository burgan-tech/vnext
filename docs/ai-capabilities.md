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
| Check a domain package / its flows | [`domain-performance-audit`](#5-domain-package--flow-checks) + plugin `review-components` / `validate-and-fix` |
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
- **Run:** `cd etc/docker && ./run-docker.sh up <domain> [--offset N] [--with <d1,d2>]`, then in `../vnext-example`:
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

**Local environment** — `etc/docker/run-docker.sh status | up <domain> [--offset N] [--with <d1,d2>] | plan | down`,
records in `ai-docs/local-environments/<domain>.md`; full runbook in
[AGENTS.md](../AGENTS.md#runbook-bring-up-domain-x-for-agents).

## 5. Domain package / flow checks

**`domain-performance-audit`** — [skill](../.claude/skills/domain-performance-audit/SKILL.md) ·
checks [CHECKS.md](../.claude/skills/domain-performance-audit/CHECKS.md)

- **Does:** a static performance audit of a **domain package** (a vnext-template repo such as
  vnext-onboarding) — workflows, tasks, extensions, functions, views, scripts — against the current
  runtime, then a short prioritised report (in Turkish, since it goes to the domain team).
- **When:** "domain performans", "performans incele", "bu domaini incele", "domain audit".
- **Run:** *"bu domaini incele: ../vnext-onboarding"*; the scan step is
  `python3 .claude/skills/domain-performance-audit/scan.py <domain-root> [--json]`.
- **Boundary:** read-only, writes nothing to the target. Not for a diff of this repo — use `pr-review` /
  `workflow-code-review`.

Component correctness (schema validity, references, security) is the vnext-ai-toolkit plugin's job —
`validate-and-fix`, `review-components`, `security-audit` in [§8](#8-machine-level-plugins-and-tools-if-installed).

## 6. GitHub and git

| Skill | Does | When | Boundary |
|---|---|---|---|
| [`create-github-issue`](../.claude/skills/create-github-issue/SKILL.md) | Structured English issue from a scope in any language ([template](../.claude/skills/create-github-issue/TEMPLATE.md)); "projeyi tara" enriches it from the code | "issue aç", "issue oluştur", "open issue" | Shows the draft first; `gh` only after approval |
| [`create-github-pr`](../.claude/skills/create-github-pr/SKILL.md) | PR title and body from the branch's commits, following [`.github/PULL_REQUEST_TEMPLATE.md`](../.github/PULL_REQUEST_TEMPLATE.md) | "PR oluştur", "open PR", "pull request" | Three separate gates: draft → push → `gh pr create`; never force-pushes |
| [`git-commit-message`](../.claude/skills/git-commit-message/SKILL.md) | Conventional Commits message from the staged diff; the single source of commit/PR types | "commit mesajı", "git commit" | Never commits unless asked |

Nothing is written to GitHub, committed or pushed without an explicit approval.

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
