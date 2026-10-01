# CLAUDE.md

Claude Code entry point. All project guidance is in `AGENTS.md`, shared with every other coding
agent; this file only adds what is Claude Code-specific. Keep it thin — new content belongs in
`AGENTS.md`, `.claude/rules/` or `/docs`.

@AGENTS.md

## Project skills (`.claude/skills/`)

Invoke through the `Skill` tool. Each skill's trigger phrases live in its own frontmatter
`description`, which Claude Code already loads every session — do not repeat them here.
`agent-council` also fires without a phrase on any council-shaped question
([Agent Council plan mode](.claude/rules/agent-council-plan-mode.md)), and `runtime-integration-test`
on a core-process change that needs end-to-end proof.

`agent-council` · `pr-review` · `workflow-code-review` · `domain-performance-audit` ·
`runtime-integration-test` · `cross-domain-lab` · `create-github-issue` · `create-github-pr` ·
`git-commit-message` · `vnext-docs-generator` · `vnext-meta-validator` (also after any `vnext-meta/`
edit) · `vnext-meta-matrix`. When to use which, with examples: `docs/ai-capabilities.md`.

## Project subagents (`.claude/agents/`)

Claude Code-only thin shells over [`docs/code-review/`](docs/code-review/README.md):
`pr-review-lead` runs a whole review out of the main context and never writes to GitHub (the
interactive path is the `pr-review` skill); `pr-reviewer-pipeline / -platform / -contract / -evidence`
are read-only specialists started in parallel by `pr-review`.

## Project MCP servers (`.mcp.json`)

`openobserve` (logs + traces: org `default`, stream `vnext`), `postgres` (restricted access mode),
`redis`, `elasticsearch` (APM, full docker profile). They only answer while the docker infra is up
(`etc/docker/run-docker.sh status`); the `openobserve` header is the compose-file root login. During
local development and test debugging, query them instead of guessing from host stdout. `redis`
exposes write tools (`set`, `delete`, `publish`, …) — use only the read tools unless the user asks.
Query mechanics, traps and the `postgres` connect-timeout cause:
[Verifying a change through the MCP servers](.claude/rules/mcp-observability-verification.md).

## Personal, machine-local overrides

@CLAUDE.local.md

Optional and never committed (git-ignored): repo paths and machine facts only, never policy. If the
file is absent this import is simply ignored.
