# Agent Council Plan Mode

Governs **when** the council runs and what an answer must disclose. **How** it runs is the
`agent-council` skill (`.claude/skills/agent-council/`); verdicts, conditions, risk and the Chair are
the contract in [`docs/agent-council/README.md`](../../docs/agent-council/README.md).

## Trigger

**Phrases**: "council", "council işlet", "karar verelim", "hangisini seçelim", "eklemeli miyim",
"eklemeli miyiz", "yapmalı mıyız", "should we add", "should we build", "mimari karar", "hangi yaklaşım".

**Shape** — no phrase required. The honest answer is a *recommendation* rather than a fact, and the
subject is a new component/task type, service or public contract; moving a responsibility between
services; adopting or dropping a technology, broker or infrastructure dependency; a data-model, schema
or consistency change; an auth/secret/PII boundary; a performance claim that would justify a change; or
a deployment/recovery change. A request relayed from another team is a **strong** trigger: an outside
party already holds a position nobody has argued against.

**Does not apply to**: single-file or single-method fixes, renames, doc edits, test additions, purely
informational questions, or carrying out a decision the user has already made. If unsure, ask in one
sentence — do not silently skip. Lite is the default mode; Full only when the contract says so.

## Mandatory disclosure

An answer to a qualifying question **opens by stating council status**: that it ran (linking the
session), or that it was skipped and why. A council-shaped question answered silently from a single
perspective violates this rule — the usual failure is a well-researched answer in which nobody argued
the opposing side.

## Plan-mode boundary

While a session is active the agent reads anything and writes only the session folder under
`ai-docs/agent-council/sessions/` and one row in `docs/agent-council/sessions/README.md`. No production
source, tests, migrations, manifests, generated contracts or configuration; no infrastructure or
expensive integration runs without the user's go-ahead. A decision is not implementation approval:
implementation is a separate task with the user's explicit direction, linked to the decision. In the CI
pipeline an `APPROVED` verdict authorizes development, not merge
([pipeline §3](../../docs/governance/ai-engineering-pipeline.md)).
