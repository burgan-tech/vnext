# Council Chair

## Identity

- Name/actor ID: **Mehmet TOSUN** — `mtosun@burgantech.com` (GitHub `middt`)
- Backup/delegate: none configured — a High/Critical decision closes as `Awaiting Chair` while the Chair is unavailable
- Authority source: vNext runtime repository owner / platform team lead (standing, not delegated)
- Delegation expiry (UTC): none — standing appointment; revisit when a backup is named

The Chair protects architectural boundaries, risk controls, evidence quality and long-term
maintainability. The Chair need not design the solution.

## Mandatory approval

Service boundary or data ownership changes, new infrastructure, authentication/authorization/token/secret
changes, breaking cross-service contracts, production migrations, high-blast-radius changes, critical
technical debt, and unresolved hard constraints — plus every High or Critical decision
([README § Risk](README.md#risk)).

The Chair rules with the council verdicts ([README § Verdicts](README.md#verdicts)); `REJECTED` is the
veto. The ruling and any added or waived conditions are recorded as `Chair YYYY-MM-DD: <verdict>` in
the session's `DECISION.md` and its log row.

## Standing precedents

Read these before drafting a decision. A proposal that contradicts one says so explicitly and argues why
the precedent should change; it is not re-litigated silently.

### Review and release gates

- **Nothing is committed, pushed, or opened on GitHub without an explicit instruction.** Implementation
  plus build plus tests is delivered as a diff summary; the commit is the Chair's call. Issue and PR
  drafts are produced as local markdown first. GitHub is **written** (issue, PR, comment, label, push)
  only on an explicit instruction; **reading** it with `gh` is fine inside a skill the user invoked
  for that purpose (`pr-review`, `create-github-pr`, `vnext-docs-generator` fallback) and never
  done on the agent's own initiative.
- **Unit tests are not enough for a pipeline-level change.** Integration tests in `vnext-example` against
  the locally built runtime, per `docs/testing/integration-testing.md`; starting infrastructure is
  approved case by case.
- **Aether is proposed to, not edited.** An SDK-level fix is written up as a proposal with the trade-off;
  the Chair decides whether to take it to Aether.
- **Evidence before claims.** An unmeasured performance, recovery or compatibility claim is
  `EXPERIMENT_REQUIRED`, not approval.

### Architectural decisions already taken (do not reopen without new evidence)

Each line names the decision; the rule itself lives in the linked source.

- `transition.roles` is not enforced at execution; `queryRoles` is answered by `authorize`, enforced at
  the gateway (amended 2026-09-22) — [workflow card § Role Grants](../../.claude/rules/vnext-workflow-developer.md#role-grants-validation--evaluation).
- Only `updateData` skips the state lifecycle on `$self` — card § Self-target composition.
- The Busy flag is the mutex; one status lock per hop — card § Locking.
- Epilogue order Auto → Schedule — card § Transition Pipeline Order.
- Runtime child calls and their `sync` mode — card § Sync vs Async.
- A parent with an open SubFlow correlation stays Busy for the child's lifetime — card § SubFlow Lifecycle.
- Dual delivery (outbox + post-commit relay) is granted per event type by registration; EventHook stays
  deleted (amended 2026-09-09) — `docs/runtime/event-publish-modes.md`.
- Scheduled-transition job changes stay outside the state-function ETag (#864) — known gap, not a bug.
- Activation episode tracing: one backdated span per episode — card § Activation episode.
- AI guidance has one source (`.claude/`, `AGENTS.md` as bootstrap); decision history is the committed
  log row, session folders stay in git-ignored `ai-docs/`.

### What the Chair expects from a decision record

- Verified facts separated from assumptions; code cited over docs when they disagree.
- A real dissent table. A council where nobody argued the losing side is sent back.
- A rollback path and an integration-test plan, or an explicit statement of why the change is below
  that threshold.
- For a new component type: the coordination cost with `@burgan-tech/vnext-schema`, `vnext-meta` and the
  Helm chart impact of any new required configuration.
