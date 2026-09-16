---
name: workflow-code-review
description: Systematic code review of a local diff for vNext workflow changes. Checks pipeline step discipline, repository include efficiency, long-polling correctness, error boundary integrity, result pattern compliance, and clean architecture. Use when the user says "code review", "review et", "incele", "review yap", or asks to review pipeline, transition, or workflow code. For an open pull request, use pr-review instead.
---

# Workflow Code Review

The single-session, local-diff path. No PR, no subagents, no GitHub write — you run the checklists
yourself and print the report. For an open pull request use the **`pr-review`** skill, which runs the
same checklists as four parallel reviewers and can post a sticky comment.

## Checklists — the single source

The content of this review lives in `docs/code-review/`, not in this file. Start at
[`docs/code-review/README.md`](../../../docs/code-review/README.md) — it carries the severity and
verdict vocabularies, the noise rules, the report template, and the § Reviewers table that says which
of [`reviewers/pipeline.md`](../../../docs/code-review/reviewers/pipeline.md),
[`platform.md`](../../../docs/code-review/reviewers/platform.md),
[`contract.md`](../../../docs/code-review/reviewers/contract.md) and
[`evidence.md`](../../../docs/code-review/reviewers/evidence.md) owns what.

## Procedure

1. Identify the changed files: `git diff` (unstaged), `git diff --cached` (staged), or the range the
   user names.
2. Pick the checklists that apply using the selection table in `docs/code-review/README.md`
   § Selection. When a path is ambiguous, run the checklist.
3. Work through each selected checklist against the **code**, not from memory. When
   `.claude/rules/` and the code disagree, the code wins.
4. Apply the § Noise rules before reporting — especially "no findings about lines the diff did not
   change" and "every CRITICAL cites a source of truth".
5. Print the § Report template, with the verdict taken mechanically from the § Verdict table.

This skill is read-only: it never edits source, commits, or writes to GitHub.
