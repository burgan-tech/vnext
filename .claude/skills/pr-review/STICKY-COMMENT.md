# Sticky comment — pr-review Step 6

Loaded only when the review ran in PR mode and the user did not pass `--no-comment`.

## Approval gate

Show the gate in the same shape the `create-github-pr` and `create-github-issue` skills use:

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

The committed `.claude/settings.json` deliberately does **not** allow the GitHub write, so with the
committed settings the harness asks again at the `gh api` call. A developer's local settings
(`.claude/settings.local.json`, user settings) may allow it, so that second prompt cannot be relied
on: **this gate is the skill's own obligation** and is never skipped because the harness would not
ask. Do not add the write to the committed allowlist.

## Upsert

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
