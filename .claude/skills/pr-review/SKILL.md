---
name: pr-review
description: Orchestrated PR review for this runtime. Classifies the diff, runs the pipeline / platform / contract / evidence reviewer agents in parallel, merges and de-duplicates their findings into one severity-ranked report, and optionally upserts a single sticky comment on the PR. Use when the user says "pr review", "PR incele", "PR kontrol", "review PR #N", "PR'ı denetle", or asks for a review of an open pull request. For a local diff with no PR, workflow-code-review is the lighter path.
---

# PR Review

You are the **lead**. You do not review the code yourself — you decide who reviews, run them in
parallel, and take responsibility for the signal-to-noise ratio of the merged report.

The reviewer set, the selection table, the severity and verdict vocabularies, the noise rules and the
report template all live in **`docs/code-review/README.md`**. Read it first; it is the contract, and
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

Apply the noise rules from `docs/code-review/README.md` § Noise rules in order, counting what each
one removed. The counts go in the report footer — a report that never filters anything is not being
trusted, and you must be able to show the work.

Resolve overlaps rather than listing both: `platform` and `evidence` both guard the Aether local
feed, and `pipeline` and `contract` both touch subflow state notification. One finding, highest
severity, both rule ids cited.

Then set the verdict from the § Verdict table. The verdict follows the table mechanically — you do
not soften it because the change looks reasonable, and you do not harden it because the PR is large.

## Step 5 — Report

Print the § Report template to the terminal. Nothing else goes to the terminal before it except the
one-line mode and selection statements from steps 1 and 2.

## Step 6 — Sticky comment (PR mode only, after approval)

Never write to GitHub without an explicit yes. Show the approval gate in the same shape the
`create-github-pr` and `create-github-issue` skills use:

```
┌────────────────────────────────────────────┐
│  Post this review as a sticky comment?     │
│  PR #<N> · verdict <V> · <C>/<W>/<I>       │
│                                            │
│  yes    → upsert the comment               │
│  edit   → tell me what to change           │
│  no     → terminal report only             │
└────────────────────────────────────────────┘
```

Skip the gate entirely and do not ask when the user passed `--no-comment`, or in pre-PR mode.

The GitHub write is deliberately **not** in `.claude/settings.json`'s allowlist, so the harness will
ask again at the `gh api` call. That second prompt is the backstop, not a misconfiguration — do not
add the write to the allowlist to remove it.

On `yes`, upsert **one** comment keyed by the marker — the same pattern
`.github/workflows/pr-prerelease-images.yml` already uses for its prerelease comment, so a re-review
updates in place instead of stacking:

```bash
MARKER='<!-- vnext-pr-review -->'
REPO=$(gh repo view --json nameWithOwner -q .nameWithOwner)
BODY="$MARKER
<report>"

CID=$(gh api "repos/$REPO/issues/$PR/comments" --paginate \
      --jq ".[] | select(.body | startswith(\"$MARKER\")) | .id" | head -n 1)

if [ -n "$CID" ]; then
  gh api -X PATCH "repos/$REPO/issues/comments/$CID" -f body="$BODY"
else
  gh api -X POST "repos/$REPO/issues/$PR/comments" -f body="$BODY"
fi
```

The comment body carries the report plus a footer line naming the reviewers that ran and the runtime
commit reviewed. Never use `gh pr review --approve` or `--request-changes`: this is advisory, and an
approval must come from a human.

## Rules

- **Read-only on the code.** This skill never edits source, never commits, never pushes. If the user
  asks for the findings to be fixed, that is a separate task after the report.
- **English report, any-language conversation.** The report and the comment are English, like every
  other artifact in this repo.
- One review per invocation. Reviewing three PRs means three invocations.
- If a reviewer agent returns nothing, that is a clean result for its area — report it as such rather
  than re-running it.
