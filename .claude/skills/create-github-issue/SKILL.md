---
name: create-github-issue
description: Creates a structured English GitHub issue using gh CLI based on a user-provided scope description. Translates the scope to English, optionally scans the codebase to enrich the technical description, shows the draft for user approval, then opens the issue. Use when the user asks to open an issue, create a GitHub issue, report a bug/task/feature, or says "issue aç", "issue oluştur", or "scan project" / "projeyi tara".
---

# Create GitHub Issue

## Workflow

### Step 0 — Pre-flight checks

Run in parallel:

```bash
gh auth status                           # must be authenticated
gh repo view --json nameWithOwner        # confirms a remote repo is linked
```

If `gh` is not authenticated, stop and instruct the user to run `gh auth login`.
If no remote repo is linked, stop and inform the user.

---

### Step 1 — Understand the scope

Accept the scope in any language (Turkish, English, etc.). Internally translate and work in English throughout.

Identify the issue type from the scope:

| User intent | Issue type label |
|-------------|-----------------|
| Bug / error / crash | `bug` |
| New feature / request | `enhancement` |
| Refactor / cleanup | `refactor` |
| Documentation | `documentation` |
| Task / chore | `chore` |
| Performance | `performance` |

If the user says **"projeyi tara"** or **"scan project"**, execute **Step 1a** before continuing.

---

### Step 1a — Codebase scan (optional, triggered by "projeyi tara")

Scan the repo to find areas relevant to the scope:

1. Prefer `code-review-graph` MCP tools (semantic_search_nodes, query_graph) over Grep/Glob/Read for structural lookups.
2. Identify up to **5 specific touch-points** (files, methods, patterns) that would need to change.
3. Note any obvious risks: missing error handling, unclear ownership, tight coupling, deprecated patterns.
4. Include these findings in the `## Technical Context` section of the issue body (see template below).

Keep findings concise — surface-level technical observations are sufficient, not a full audit.

---

### Step 2 — Generate issue draft

Draft the following:

- **Title**: Imperative English, max 72 chars.
  Format: `[type] <concise description>`
  Example: `[bug] Fix null reference in payment callback handler`

- **Body**: Use the template below. Fill in all sections. Remove sections that truly don't apply.

#### Issue Body Template

```markdown
## Description
<!-- What is the problem or goal? Why does it matter? -->

## Steps to Reproduce (bugs only)
<!-- Numbered steps. Remove this section for non-bugs. -->
1. 
2. 

## Expected Behavior
<!-- What should happen? -->

## Actual Behavior (bugs only)
<!-- What actually happens? Remove for non-bugs. -->

## Technical Context
<!-- Relevant files, classes, methods, or patterns identified during scope analysis.
     If "projeyi tara" was used, populate from scan results. Otherwise, add what is known. -->
- 

## Acceptance Criteria
<!-- Definition of done — bullet list of verifiable conditions -->
- [ ] 

## Notes
<!-- Risks, dependencies, related issues, or anything else worth knowing. Remove if not applicable. -->
```

---

### Step 3 — Show draft for approval

Present the full draft **before creating anything**:

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

Wait for the user to respond:
- **yes / confirm / create** → proceed to Step 4
- **edit / change** → apply the user's edits and show the draft again
- **cancel / no** → abort and inform the user

---

### Step 4 — Create the issue

```bash
gh issue create \
  --title "<title>" \
  --body "$(cat <<'EOF'
<body>
EOF
)" \
  --label "<label>"
```

After creation, output the issue URL returned by `gh issue create`.

---

## Rules

- **Always write in English** — title, body, labels, everything, regardless of input language.
- **Never skip approval** — always show the draft and wait for confirmation.
- **Translate silently** — do not mention the translation step to the user unless asked.
- **Prefer specific over generic** — include file paths, method names, or line references when known.
- **Keep Technical Context surface-level** — aim for actionable pointers, not exhaustive analysis.
- **One issue at a time** — if the scope covers multiple unrelated concerns, ask the user to split them.

---

## Examples

### Title examples

| User scope | Generated title |
|-----------|----------------|
| "Login sayfası hata veriyor, boş email kabul ediyor" | `[bug] Fix login form accepting empty email address` |
| "Kullanıcı profil sayfasına avatar yükleme ekle" | `[enhancement] Add avatar upload to user profile page` |
| "RuleEvaluator servisini temizle, tekrar eden kodlar var" | `[refactor] Remove duplicated logic in RuleEvaluator service` |

### Minimal body example (no scan)

```markdown
## Description
The login form currently accepts empty email addresses and submits, causing a 500 error on the server side.

## Steps to Reproduce
1. Navigate to /login
2. Leave the email field blank, enter any password
3. Click Submit

## Expected Behavior
Client-side validation rejects the form and shows an error message.

## Actual Behavior
The form submits and the server returns a 500 Internal Server Error.

## Technical Context
- `src/components/LoginForm.jsx` — form submit handler lacks email presence check

## Acceptance Criteria
- [ ] Empty email field shows inline validation error
- [ ] Form does not submit until email is valid
- [ ] Server-side validation also rejects empty email (defense in depth)
```
