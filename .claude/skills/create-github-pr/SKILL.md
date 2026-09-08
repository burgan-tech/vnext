---
name: create-github-pr
description: Creates an English GitHub Pull Request using gh CLI by analyzing commits on the current branch compared to the base branch. Generates a structured PR title and body, shows it for user approval, then creates the PR. Use when the user asks to create a PR, open a pull request, push and create PR, or mentions "gh pr", "pull request", or "open PR".
---

# Create GitHub PR

## Workflow

### Step 1 — Collect branch context

Run these in parallel:

```bash
git rev-parse --abbrev-ref HEAD          # current branch name
gh repo view --json defaultBranchRef -q '.defaultBranchRef.name'  # base branch
git log origin/<base>..HEAD --oneline    # commits ahead of base
git diff origin/<base>...HEAD --stat     # files changed summary
```

If the current branch equals the base branch, stop and tell the user to switch to a feature branch first.

If `gh` is not authenticated, stop and instruct the user to run `gh auth login`.

### Step 1b — Pre-flight guards (run before drafting)

```bash
grep -nE '^\s*<add key="aether-local"' nuget.config          # live (uncommented) local Aether feed?
grep -nE '<AetherPackageVersion>[^<]*-local<' Directory.Build.props   # -local version pinned?
```

If either command prints a line, **stop**: the branch builds against an unreleased Aether from
`../aether/.local-feed`, and CI cannot restore it. Tell the user, and offer to revert — set
`AetherPackageVersion` back to the released version and re-comment both the `aether-local` source and
its `packageSourceMapping` block in `nuget.config` — then re-check before continuing. Procedure and
rationale: `docs/testing/integration-testing.md` §8.

### Step 2 — Analyze commits and generate PR draft

Analyze the commit log and diff stat to draft:

- **Title**: Conventional Commits style, max 72 chars, English, imperative mood.
  Format: `<type>(<scope>): <subject>`
  Types: `feat`, `fix`, `refactor`, `perf`, `test`, `docs`, `chore`, `ci`

- **Body**: Structured markdown using the template below.

#### PR Body Template

```markdown
## Summary
<!-- 2-4 bullet points describing what changed and why -->
- 

## Changes
<!-- List key files/areas changed -->
- 

## Test Plan
<!-- How to verify this works -->
- [ ] 

## Integration test evidence
<!-- Only when an integration run was part of this change (docs/testing/integration-testing.md §10). Remove otherwise. -->
- Scenario(s): `vnext-example` `Tests/<Scenario>` (`--filter FullyQualifiedName~<Scenario>`)
- Runtime: commit `<sha>` at `VNEXT_BASE_URL=http://localhost:<port>` (`run-docker.sh up <domain>`)
- Result: <passed>/<total> green; remaining reds and their cause: <environment defect | known gap | regression>
- `TEST-SCENARIOS.md` row added/updated: yes/no

## Notes
<!-- Breaking changes, migration steps, or anything reviewers should know. Remove if not applicable. -->
```

Fill in all sections based on actual commit messages and diff content. Remove the `## Notes` section if there is nothing breaking or noteworthy, and the `## Integration test evidence` section when no integration run was part of the change.

### Step 3 — Show draft for approval

Present the full PR draft to the user **before creating anything**:

```
────────────────────────────────────────
  PULL REQUEST DRAFT
  Branch: <current-branch> → <base-branch>
────────────────────────────────────────

Title:
  <generated title>

Body:
<generated body>

────────────────────────────────────────
Create this PR? (yes / edit / cancel)
────────────────────────────────────────
```

Wait for the user to respond:
- **yes / confirm / create** → proceed to Step 4
- **edit / change** → apply the user's edits and show the draft again
- **cancel / no** → abort and inform the user

### Step 4 — Push branch and create PR

```bash
# Push if the remote branch doesn't exist yet
git push -u origin HEAD

# Create the PR
gh pr create \
  --title "<title>" \
  --body "$(cat <<'EOF'
<body>
EOF
)" \
  --base <base-branch>
```

After creation, output the PR URL returned by `gh pr create`.

## Rules

- **Always write in English** — title, body, labels, everything.
- **Never skip approval** — always show the draft and wait for confirmation.
- **Never force-push** — use `git push -u origin HEAD` only.
- **Never create a PR from the default branch** — validate and stop early if needed.
- If commits include `BREAKING CHANGE:` in footers, add a `⚠️ Breaking change` note at the top of the body.
- If the branch has no commits ahead of base, stop and tell the user there is nothing to PR.

## Examples

### Title examples

| Commits | Generated title |
|---------|----------------|
| feat: add JWT refresh, fix: token expiry | `feat(auth): add JWT refresh token with expiry fix` |
| chore: upgrade EF Core to 9.0.4 | `chore(deps): upgrade EF Core to 9.0.4` |
| fix null ref in transition pipeline | `fix(pipeline): prevent null reference in transition handler` |

### Minimal body example

```markdown
## Summary
- Add JWT refresh token endpoint to extend session lifetime
- Fix token expiry calculation that caused premature logouts

## Changes
- `src/BBT.Workflow.Application/Auth/TokenService.cs`
- `src/BBT.Workflow.Domain/Auth/RefreshToken.cs`

## Test Plan
- [ ] Log in and wait for access token to expire
- [ ] Verify refresh endpoint returns a new token without re-login
```
