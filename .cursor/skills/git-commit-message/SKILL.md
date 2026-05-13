---
name: git-commit-message
description: Generates a Conventional Commits-style English commit message by analyzing staged git changes. Use when the user asks for a commit message, wants to commit changes, mentions "commit", "git commit", or asks for help writing a commit message.
---

# Git Commit Message Generator

## Workflow

1. Run `git diff --cached` to inspect staged changes.
2. If the output is empty, inform the user that no changes are staged and suggest running `git add` first.
3. Analyze the diff and generate a commit message in English following the Conventional Commits format below.
4. Present the message to the user. Do NOT commit automatically unless explicitly asked.

## Commit Message Format

```
<type>(<scope>): <short subject>

<optional body>

<optional footer>
```

### Subject line rules
- Max 72 characters
- Lowercase, imperative mood ("add", not "added" or "adds")
- No period at the end
- `<scope>` is optional; use the affected module, layer, or file area

### Types

| Type | Use when |
|------|----------|
| `feat` | New feature or capability |
| `fix` | Bug fix |
| `refactor` | Code restructuring without behavior change |
| `perf` | Performance improvement |
| `test` | Adding or updating tests |
| `docs` | Documentation only |
| `chore` | Build scripts, tooling, dependencies, config |
| `ci` | CI/CD pipeline changes |
| `style` | Formatting, whitespace (no logic change) |
| `revert` | Reverting a previous commit |

### Body rules (include when helpful)
- Wrap at 72 characters
- Explain **what** and **why**, not how
- Separate from subject with a blank line

### Footer (optional)
- `BREAKING CHANGE: <description>` for breaking changes
- `Closes #<issue>` for linked issues

## Examples

**Simple feature:**
```
feat(auth): add JWT refresh token support
```

**Bug fix with body:**
```
fix(repository): prevent duplicate inserts on concurrent requests

EF Core change tracking was not reset between retries, causing
duplicate key violations under high concurrency. Reset tracker
after each failed attempt.
```

**Chore:**
```
chore(deps): upgrade EF Core to 9.0.4
```

**Breaking change:**
```
feat(api)!: remove deprecated v1 endpoints

BREAKING CHANGE: /api/v1/* routes are no longer available.
Migrate to /api/v2/*.
```

## Output Format

Present the commit message in a fenced code block so the user can copy it easily:

```
git commit -m "$(cat <<'EOF'
<type>(<scope>): <subject>

<body if any>
EOF
)"
```

If the diff contains multiple unrelated concerns, suggest splitting into separate commits and offer a message for each.
