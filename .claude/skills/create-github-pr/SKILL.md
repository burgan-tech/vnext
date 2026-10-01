---
name: create-github-pr
description: "Drafts an English PR title and body from the commits on the current branch versus the base, following .github/PULL_REQUEST_TEMPLATE.md, then pushes and opens it only after two separate approvals. Triggers: \"PR oluştur\", \"open PR\", \"create PR\", \"pull request\", \"gh pr\"."
---

# Create GitHub PR

**No `gh` call and no push happen before the user approves the draft.** Everything up to Step 3 is
local git.

## Workflow

### Step 1 — Collect branch context (local only)

```bash
git rev-parse --abbrev-ref HEAD                                  # current branch
git remote -v                                                    # owner/repo from the origin URL
git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null    # base, e.g. origin/master
git log origin/<base>..HEAD --oneline                            # commits ahead of base
git diff origin/<base>...HEAD --stat                             # files changed
```

If `origin/HEAD` is not set, use `master` (this repo's main branch) and say so. Stop when the current
branch is the base branch (switch to a feature branch first) or when there are no commits ahead of
base (nothing to PR).

### Step 1b — Pre-flight guard: no live Aether local feed

```bash
grep -nE '^\s*<add key="aether-local"' nuget.config
grep -nE '<AetherPackageVersion>[^<]*-local<' Directory.Build.props
```

Either command printing a line means the branch builds against an unreleased Aether that CI cannot
restore — **stop**, tell the user, and offer the revert described in
[integration-testing §8](../../../docs/testing/integration-testing.md#8-aether-changes-during-development)
(the canonical procedure); re-check before continuing.

### Step 2 — Draft title and body

- **Title**: Conventional Commits, English, imperative, max 72 chars — `<type>(<scope>): <subject>`.
  The type list and subject rules are the `git-commit-message` skill's
  ([SKILL.md](../git-commit-message/SKILL.md) § Types); do not keep a second list here.
- **Body**: follows [`.github/PULL_REQUEST_TEMPLATE.md`](../../../.github/PULL_REQUEST_TEMPLATE.md)
  section for section. How to fill it:
  - Write every section from the actual commit messages and diff, not from the branch name.
  - **Remove** a section that does not apply (`Integration test evidence` when no integration run
    was part of the change, `Notes` when nothing is breaking or noteworthy) — never leave
    placeholder text.
  - `Integration test evidence` is filled from the real run (scenario, runtime commit and
    `VNEXT_BASE_URL`, pass count, cause of any red) per
    [integration-testing §10](../../../docs/testing/integration-testing.md#10-report-the-result).
  - A `BREAKING CHANGE:` footer in any commit ⇒ a `⚠️ Breaking change` note at the top of the body.

### Step 3 — Show the draft for approval

```
────────────────────────────────────────
  PULL REQUEST DRAFT
  Repo:   <owner/repo>
  Branch: <current-branch> → <base-branch>
────────────────────────────────────────

Title:
  <generated title>

Body:
<generated body>

────────────────────────────────────────
Approve this draft? (yes / edit / cancel)
────────────────────────────────────────
```

**edit** → apply and show again · **cancel** → abort. **yes** approves the *text only* — it is not
permission to push.

### Step 4 — Push (separate approval)

Check whether the branch already exists on the remote (`git ls-remote --heads origin <branch>`) and
ask explicitly:

```
Push <branch> to origin now? (yes / no)
```

Only on `yes`: `git push -u origin HEAD`. On `no`, stop and leave the approved draft in the
conversation.

### Step 5 — Create the PR (separate approval)

Ask explicitly:

```
Open the PR on <owner/repo> now? (yes / no)
```

Only on `yes`:

```bash
gh auth status        # not authenticated: stop and ask the user to run `gh auth login`
gh pr create --repo <owner/repo> \
  --title "<title>" \
  --body "$(cat <<'BODY'
<body>
BODY
)" \
  --base <base-branch>
```

Output the PR URL `gh pr create` returns.

## Rules

- **Always English** — title, body, everything.
- **Three gates, never merged into one**: draft approval, push approval, PR-creation approval.
- **Never force-push** — `git push -u origin HEAD` only.
- **Never create a PR from the default branch.**
