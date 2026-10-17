---
name: create-github-pr
description: "Drafts an English PR title and body from the commits on the current branch versus the base, following .github/PULL_REQUEST_TEMPLATE.md, then pushes and opens it only after two separate approvals. Triggers: \"PR oluştur\", \"open PR\", \"create PR\", \"pull request\", \"gh pr\"."
---

# Create GitHub PR

**No push and no `gh` write happen before the user approves the draft.** Everything up to Step 3 is
local git, plus one read-only `gh api` call listing open milestones (Step 2).

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
  - The title type picks the contract tier (template header): `feat` / `fix` / `perf` / `refactor`
    fill every required section; `test` / `docs` / `chore` / `ci` need only `## Summary`. Optional
    sections (`Changes`, `Test Plan`, `Notes`) are removed when empty — never leave placeholder text.
  - **Milestone** (full tier): ask the user, or list the open ones read-only with
    `gh api repos/<owner/repo>/milestones?state=open --jq '.[].title'` and propose the matching
    `vX.Y.Z`.
  - **`## E2E evidence`**: when the integration run left a TRX, generate the block with
    `node scripts/governance/e2e-evidence.mjs --trx <file>... --runtime-sha <sha> --base-url <url> [--reds "<text>"]`
    ([integration-testing §10](../../../docs/testing/integration-testing.md#10-report-the-result));
    never write the numbers by hand. Without a run, ask the user for the reason and write
    `- Status: not-run` + `- Reason: <their reason>`.
  - **`## Product impact`**: the template ships every row as `<yes/no>` with no default; for each of
    the ten rows, look at the diff and propose `yes` or `no` with
    a one-line why (new component field or task type → `vnext-schema`, `vnext-forge`, `vnext-docs`; new
    config key → `vnext-helm-charts`; …). For each `yes`, propose a handoff draft at
    `docs/integration/<product>/<slug>.md` from
    [`_template.md`](../../../docs/integration/_template.md) (frontmatter `milestone` = the PR's
    milestone, `status: pending`), or take a PR/issue URL from the user. A new handoff file must be
    committed on the branch before the PR opens — ask before committing it.
  - **`## vnext-meta impact`**: `updated` when the diff changes `vnext-meta/*.json`, else
    `not-needed` with the reason.
  - A `BREAKING CHANGE:` footer in any commit ⇒ a `⚠️ Breaking change` note at the top of the body.
  - **Check the draft before showing it**: write the body to a scratch file and run
    `node scripts/governance/pr-contract.mjs --body-file <draft.md> --title "<title>" --milestone <vX.Y.Z> --files-from-git origin/<base>`
    (omit `--milestone` for a light-tier PR).
    Show its findings (rule ids: [reviewer: evidence §4](../../../docs/code-review/reviewers/evidence.md))
    together with the draft in Step 3, and offer to fix them; the draft may still be approved with
    findings if the user says so.

### Step 3 — Show the draft for approval

```
────────────────────────────────────────
  PULL REQUEST DRAFT
  Repo:   <owner/repo>
  Branch: <current-branch> → <base-branch>
────────────────────────────────────────

Title:
  <generated title>
Milestone:
  <vX.Y.Z | none (light tier)>

Body:
<generated body>

Contract check (pr-contract.mjs --body-file):
  <findings, or "no findings">

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
  --base <base-branch> \
  --milestone "<vX.Y.Z>"        # omit for a light-tier PR without a milestone
```

Output the PR URL `gh pr create` returns.

## Rules

- **Always English** — title, body, everything.
- **Three gates, never merged into one**: draft approval, push approval, PR-creation approval.
- **Never force-push** — `git push -u origin HEAD` only.
- **Never create a PR from the default branch.**
