---
name: create-github-issue
description: "Drafts a structured English GitHub issue from a scope in any language, optionally enriched by a codebase scan, and opens it with gh only after the user approves the draft. Triggers: \"issue aç\", \"issue oluştur\", \"open issue\", \"create GitHub issue\", \"projeyi tara\" / \"scan project\"."
---

# Create GitHub Issue

**No `gh` call happens before the user approves the draft** — not `gh auth status`, not
`gh repo view`. Everything up to the approval gate is local.

## Workflow

### Step 0 — Resolve the repository locally

```bash
git remote -v      # take owner/repo from the origin (or upstream) URL
```

If there is no GitHub remote, stop and tell the user.

### Step 1 — Understand the scope

Accept the scope in any language; work in English throughout. Pick the label:

| User intent | Label |
|-------------|-------|
| Bug / error / crash | `bug` |
| New feature / request | `enhancement` |
| Refactor / cleanup | `refactor` |
| Documentation | `documentation` |
| Task / chore | `chore` |
| Performance | `performance` |

If the user says **"projeyi tara"** or **"scan project"**, run Step 1a first.

### Step 1a — Codebase scan (optional)

1. When `graphify-out/graph.json` exists, start with `graphify query "<scope>"` /
   `graphify explain "<symbol>"` (see `.claude/rules/graphify-navigation.md`); otherwise use targeted
   `grep`/`Glob` on the names in the scope. Do not read the codebase broadly.
2. Identify up to **5 specific touch-points** (files, methods, patterns) that would need to change.
3. Note obvious risks: missing error handling, unclear ownership, tight coupling, deprecated patterns.
4. Put the findings in the body's `## Technical Context` section — surface-level, not a full audit.

### Step 2 — Draft

Read [TEMPLATE.md](TEMPLATE.md) for the title format, the body template and worked examples.

### Step 3 — Show the draft for approval

```
────────────────────────────────────────
  GITHUB ISSUE DRAFT
  Repo: <owner/repo>
────────────────────────────────────────

Title:
  <generated title>

Labels:  <label(s)>

Body:
<generated body>

────────────────────────────────────────
Create this issue? (yes / edit / cancel)
────────────────────────────────────────
```

- **yes** → Step 4 · **edit** → apply the edits and show the draft again · **cancel** → abort.
- If the user prefers, save the draft as a local markdown file instead and stop there.

### Step 4 — Create the issue (only after `yes`)

```bash
gh auth status        # not authenticated: stop and ask the user to run `gh auth login`
gh issue create --repo <owner/repo> \
  --title "<title>" \
  --body "$(cat <<'BODY'
<body>
BODY
)" \
  --label "<label>"
```

Output the issue URL `gh issue create` returns.

## Rules

- **Always English** — title, body, labels, regardless of input language. Translate silently.
- **Never skip approval**, and never call `gh` before it.
- **Specific over generic** — file paths, method names, line references when known.
- **One issue at a time** — if the scope covers unrelated concerns, ask the user to split them.
