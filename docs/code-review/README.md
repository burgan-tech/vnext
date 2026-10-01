# Code Review — Reviewer Set

This directory is the **single source** for what a review of this repository checks. The
`.claude/agents/pr-reviewer-*.md` shells and the `pr-review` skill read these files; nothing here is
duplicated into an agent definition, a skill body or a CI workflow — the shells only name their
checklist, the repo files they must open, and link back here for the output contract. Same model as
[Agent Council roles](../agent-council/roles/) — the role file holds the content, the runner holds
the procedure.

Two entry points consume it:

| Entry point | Shape |
| --- | --- |
| `pr-review` skill | Orchestrated. In the interactive path **the skill itself is the lead**: it classifies the diff, runs the relevant reviewers in parallel, merges findings, and optionally upserts one sticky PR comment. The `pr-review-lead` agent is the optional out-of-context runner of the same procedure (large PR, automated run, several PRs in a row) and never writes to GitHub. |
| `workflow-code-review` skill | Single-session local review of an unstaged/staged diff, no PR, no GitHub write. |

## Reviewers

| Reviewer | Checklist | Owns |
| --- | --- | --- |
| pipeline | [reviewers/pipeline.md](reviewers/pipeline.md) | Transition pipeline, profiles, subflow lifecycle, locking, error boundary, execution context |
| platform | [reviewers/platform.md](reviewers/platform.md) | Aether SDK, Result pattern, EF/include efficiency, logging, DI, clean architecture, multi-schema |
| contract | [reviewers/contract.md](reviewers/contract.md) | Public API and DTO compatibility, state function/ETag, events and outbox, `vnext-meta`, docs and rule single-source |
| evidence | [reviewers/evidence.md](reviewers/evidence.md) | Tests, the integration-test policy, PR evidence sections, local-feed and measurement-claim guards |

### Selection

The lead picks reviewers from the changed paths and is deliberately **conservative — when in doubt,
run the reviewer**. `evidence` runs on every review; its guards are cheap.

| Changed paths | Reviewers |
| --- | --- |
| `src/BBT.Workflow.Domain/Execution/**`, `src/BBT.Workflow.Application/Execution/**`, anything under a `Pipeline/` folder, correlation / subflow / lock / job types | pipeline, platform, evidence |
| Any other `*.cs` | platform, evidence (+ contract when the file is in `*.HttpApi*`, `*.Events.Contracts`, or an `*Options`/config type) |
| `*.Events.Contracts/**`, `workers/BBT.Workflow.Workers.Inbox/**` | contract, platform, evidence |
| `vnext-meta/**`, `common.props` | contract |
| `docs/**`, `.claude/rules/**`, `.claude/skills/**`, `AGENTS.md`, `CLAUDE.md` | contract |
| `etc/**`, `.github/**`, `*.csproj`, `nuget.config`, `Directory.Build.props` | platform, evidence |

A diff touching only `docs/**` or `*.md` must **not** start pipeline or platform.

## Severity

| Level | Meaning |
| --- | --- |
| **CRITICAL** | Correctness, data loss, security, or a documented invariant broken. Must be fixed before merge. |
| **WARNING** | Performance, maintainability, or a contract/process obligation missed. Should be fixed. |
| **INFO** | Style, readability, minor improvement. Optional. |

## Verdict

| Verdict | Condition |
| --- | --- |
| `Approve` | No CRITICAL and at most 2 WARNING |
| `Request changes` | No CRITICAL, 3+ WARNING |
| `Block` | One or more CRITICAL |

## Finding format and noise rules

This section is the one copy of the output contract. Reviewers apply the **reviewer rules** to their
own output; the lead applies the **merge rules** when combining them.

### Finding format

Each reviewer returns **only** finding lines, one per line — no prose, no headings:

```
SEVERITY | path/to/File.cs:123 | rule-id | claim | evidence | suggested fix
```

- `rule-id` — the checklist bullet's id, e.g. `pipeline/step-order`, `platform/logging-raw`.
- `evidence` — the file or doc that makes the claim true. Empty evidence caps the finding at WARNING.
- A finding about the PR body itself uses `PR:body:0` as its location.
- The last line is always `SUMMARY | <n> findings | <one sentence on what you checked and skipped>`.

### Reviewer rules

1. Only findings about lines this diff changed. A pre-existing problem is out of scope unless the
   change makes it reachable — then state the causal link.
2. Every CRITICAL cites a file path (code, a `/docs` page or a `.claude/rules/` line) that proves it.
   No citation ⇒ downgrade it to WARNING yourself.
3. Could not confirm it from the repository ⇒ phrase it as a question and cap it at WARNING.
4. Verify against the repository, never from memory. When a rule file and the code disagree, the
   code wins — say so in the finding.
5. Silence is a valid answer. Returning nothing on a clean diff is correct; padding the list with
   restatements of the checklist is the main failure mode.

Each reviewer checklist adds its own failure mode under *Reviewer-specific noise* at its end.

### Merge rules (the line between useful and ignored)

The lead applies these in order when merging, and reports how many findings each rule removed:

1. A finding without `file:line` is dropped.
2. Same line + same rule ⇒ one finding; the highest severity survives. Overlapping areas (for
   example `platform` and `evidence` on the Aether local feed) merge into one finding citing both
   rule ids.
3. Every CRITICAL must cite a source of truth — a code file (`LifecycleOrder.cs`, …), a `/docs` page,
   or a line in `.claude/rules/`. A CRITICAL without a citation is downgraded to WARNING.
4. No findings about lines the diff did not change. Exception: the change breaks that line — then the
   causal link is stated explicitly.
5. At most 5 INFO findings; the rest are summarized as `+N more`.
6. Uncertainty is stated, never hidden: a finding the reviewer could not confirm from the repo is
   phrased as a question and capped at WARNING.

## Report template

```markdown
## PR Review — <title or branch>

**Verdict**: Approve / Request changes / Block
**Reviewers run**: pipeline, platform, contract, evidence
**Files reviewed**: N (M skipped: <reason>)

### CRITICAL
- `file:line` — claim _(rule-id; evidence)_

### WARNING
- `file:line` — claim _(rule-id; evidence)_

### INFO
- `file:line` — claim

### Positive observations
- …

<sub>Filtered: N no-location, N duplicate, N unchanged-line, N downgraded for missing evidence.</sub>
```
