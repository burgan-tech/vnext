# CLAUDE.md

Claude Code entry point. All project guidance is in `AGENTS.md`, shared with every other coding
agent; this file only adds what is Claude Code-specific. Keep it thin — new content belongs in
`AGENTS.md`, `.claude/rules/` or `/docs`.

@AGENTS.md

## Project Skills

On-demand skills live under `.claude/skills/` (Cursor reads the same folder; see *AI guidance layout*
in `AGENTS.md`). Invoke via the `Skill` tool when the trigger phrase matches:

- **agent-council** — "council" / "karar verelim" / "eklemeli miyim" / "mimari karar"; also fires
  without a trigger phrase on any design, technology-selection or cross-service question whose
  answer would be a recommendation (see [Agent Council plan mode](.claude/rules/agent-council-plan-mode.md))
- **vnext-docs-generator** — "döküman oluştur" / "create docs"
- **vnext-meta-validator** — "validate meta" / "meta kontrol" (also after any `vnext-meta/` edit)
- **vnext-meta-matrix** — "meta matrix" / "meta rapor"
- **pr-review** — "pr review" / "PR incele" / "PR kontrol" / "review PR #N"; orchestrated review of an
  open pull request (classifies the diff, runs the four `pr-reviewer-*` agents in parallel, merges
  the findings, optionally upserts one sticky PR comment). Checklists:
  [docs/code-review/](docs/code-review/README.md)
- **workflow-code-review** — "code review" / "review et" / "incele"; the same checklists run against a
  local diff, single session, no PR
- **create-github-issue** — "issue aç" / "open issue" / "projeyi tara"
- **create-github-pr** — "PR oluştur" / "open PR" / "pull request"
- **git-commit-message** — "commit mesajı" / "git commit"
- **cross-domain-lab** — "cross-domain test" / "çapraz domain" / "partner domain" (lokal 3-domain Dapr lab'ı; lab vnext-example `labs/cross-domain/` altında)
- **runtime-integration-test** — "integration test" / "entegrasyon testi" / "vnext-example'da test et" / "e2e doğrula"; also fires without a phrase on a core-process change that needs end-to-end proof (runs vnext-example tests against the locally built runtime; contract [docs/testing/integration-testing.md](docs/testing/integration-testing.md))

## Project subagents (`.claude/agents/`)

Claude Code-only; other agents ignore the folder. The definitions are thin shells — the content they
review is the single source under [`docs/code-review/`](docs/code-review/README.md).

- **pr-review-lead** — runs a whole review out of the main conversation's context; never writes to
  GitHub. The interactive path is the `pr-review` skill.
- **pr-reviewer-pipeline / -platform / -contract / -evidence** — read-only specialists, one checklist
  each, started in parallel by `pr-review`.

## Project MCP servers (`.mcp.json`)

- **openobserve** — the local OpenObserve instance started by `etc/docker/run-docker.sh`
  (`http://localhost:5080`, org `default`). **During local development and while running or
  debugging tests, query runtime logs and traces through this server** (search the `default`
  org's log streams by `instanceId`, `flow`, `transitionKey`, trace id) instead of guessing from
  host stdout. It only answers while the docker infra is up (`./run-docker.sh status`). The header
  is the compose-file root login, not a secret beyond what `etc/docker/docker-compose*.yml`
  already contains.

How to use these servers as verification evidence — query mechanics, the traps that have produced
wrong conclusions, and the `postgres` connect-timeout cause — is the always-on rule
[Verifying a change through the MCP servers](.claude/rules/mcp-observability-verification.md).

## Personal, machine-local overrides

@CLAUDE.local.md

Optional and never committed (git-ignored). Create a `CLAUDE.local.md` in the repo root for your own
environment notes and working preferences; if the file is absent this import is simply ignored.
