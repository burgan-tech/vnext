---
name: pr-review
description: "Orchestrated review of an open PR: classifies the diff, runs the pipeline/platform/contract/evidence reviewers in parallel, merges one severity-ranked report, optionally upserts a sticky comment. Triggers: \"pr review\", \"PR incele\", \"PR kontrol\", \"review PR #N\". Local diff: workflow-code-review."
---

# PR Review

You are the **lead**. You do not review the code yourself — you decide who reviews, run them in
parallel, and take responsibility for the signal-to-noise ratio of the merged report.

The reviewer set, the selection table, the severity and verdict vocabularies, the finding format and
noise rules and the report template all live in **`docs/code-review/README.md`**. Read it first; it is the contract, and
nothing in it is restated here.

## Step 1 — Resolve the target

| Input | Resolution |
| --- | --- |
| `#N` or a PR URL | `gh pr view <N> --json number,title,body,headRefName,baseRefName,commits,files` + `gh pr diff <N>` |
| No argument, branch has a PR | `gh pr view --json …` (same fields) for the current branch |
| No argument, no PR | **pre-PR mode**: `git diff $(git merge-base HEAD origin/master)...HEAD`. No PR body to check, and the sticky-comment step is skipped entirely — do not offer it. |

Tell the user in one line which mode you resolved and how many files changed.

If the diff exceeds ~2000 changed lines, review the subset most likely to carry risk (source over
generated, `src/` over `etc/`), and say in the report which files were not reviewed and why. Never
silently truncate.

## Step 2 — Classify and select

Apply the selection table in `docs/code-review/README.md` § Selection to the changed paths. Be
conservative: when a path is ambiguous, run the reviewer. `evidence` always runs.

State the selection and the reason in one line before starting, e.g.
`pipeline + platform + evidence (11 files under Execution/, no contract-facing change)`.

## Step 3 — Run the reviewers in parallel

Launch every selected reviewer **in a single message** so they run concurrently:

- `pr-reviewer-pipeline`, `pr-reviewer-platform`, `pr-reviewer-contract`, `pr-reviewer-evidence`.

Give each agent the same payload: the mode, the diff (or the PR number so it can fetch the diff
itself), the list of changed files, and — for `pr-reviewer-evidence` — the PR title and body.

Do not review anything yourself while they run, and do not re-run a reviewer to "check" another's
finding; use `SendMessage` to the same agent if a finding needs one clarification.

## Step 4 — Merge

Apply the merge rules from `docs/code-review/README.md` § Finding format and noise rules in order,
counting what each one removed. The counts go in the report footer — a report that never filters anything is not being
trusted, and you must be able to show the work.

Resolve overlaps rather than listing both (merge rule 2) — `pipeline` and `contract` also both touch
subflow state notification.

Then set the verdict from the § Verdict table. The verdict follows the table mechanically — you do
not soften it because the change looks reasonable, and you do not harden it because the PR is large.

## Step 5 — Report

Print the § Report template to the terminal. Nothing else goes to the terminal before it except the
one-line mode and selection statements from steps 1 and 2.

## Step 6 — Sticky comment (PR mode only, after approval)

Never write to GitHub without an explicit yes from the user in this conversation. Skip this step
entirely — do not ask — when the user passed `--no-comment`, or in pre-PR mode.

Read [STICKY-COMMENT.md](STICKY-COMMENT.md) when you reach this step: it holds the approval gate, the
marker-keyed upsert script and the comment footer.

## Rules

- **Read-only on the code.** This skill never edits source, never commits, never pushes. If the user
  asks for the findings to be fixed, that is a separate task after the report.
- **English report, any-language conversation.** The report and the comment are English, like every
  other artifact in this repo.
- One review per invocation. Reviewing three PRs means three invocations.
- If a reviewer agent returns nothing, that is a clean result for its area — report it as such rather
  than re-running it.
