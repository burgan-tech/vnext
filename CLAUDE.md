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
- **workflow-code-review** — "code review" / "review et" / "incele"
- **create-github-issue** — "issue aç" / "open issue" / "projeyi tara"
- **create-github-pr** — "PR oluştur" / "open PR" / "pull request"
- **git-commit-message** — "commit mesajı" / "git commit"
- **cross-domain-lab** — "cross-domain test" / "çapraz domain" / "partner domain" (lokal 3-domain Dapr lab'ı; lab vnext-example `labs/cross-domain/` altında)
- **runtime-integration-test** — "integration test" / "entegrasyon testi" / "vnext-example'da test et" / "e2e doğrula"; also fires without a phrase on a core-process change that needs end-to-end proof (runs vnext-example tests against the locally built runtime; contract [docs/testing/integration-testing.md](docs/testing/integration-testing.md))

## Personal, machine-local overrides

@CLAUDE.local.md

Optional and never committed (git-ignored). Create a `CLAUDE.local.md` in the repo root for your own
environment notes and working preferences; if the file is absent this import is simply ignored.
