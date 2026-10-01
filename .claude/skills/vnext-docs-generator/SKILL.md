---
name: vnext-docs-generator
description: Generate or update vNext platform documentation for the vnext-docs Docusaurus site. Use when the user says "döküman oluştur", "create docs", "document this feature", or asks to update/sync documentation after a code change.
---

# vNext Docs Generator

Generate documentation for the [vnext-docs](https://github.com/burgan-tech/vnext-docs) Docusaurus site based on code changes in the vnext runtime repo.

## Workflow

### Step 1: Analyze the Code Change

**Always run `git diff` first** to understand exactly what changed:

```bash
git diff --stat          # Overview of changed files
git diff                 # Full unstaged diff
git diff --cached        # Staged changes
```

From the diff output, identify:
- What feature/component was added or modified
- Which domain area it belongs to (see [REFERENCE.md](REFERENCE.md) § Category Mapping)
- Whether this is a **new feature**, **feature update**, or **breaking change**

The user may also provide additional context and details beyond the diff. Wait for this input before proceeding.

**Clarify ambiguities before writing.** If any of the following are unclear, ask the user using AskUserQuestion or conversationally:
- The scope/boundary of the feature (what's included, what's not)
- The intended audience for the doc (developer, architect, business)
- Behavioral nuances not obvious from the code (edge cases, defaults, fallback logic)
- Whether a config change is optional vs required
- Naming preferences for the doc page or section titles
- Priority of EN translation

Do NOT guess on ambiguous points — ask first, then generate.

### Step 2: Fetch Current Docs State

Read the docs from the **local sibling clone first** — `../vnext-docs` (the platform-repo convention
in `AGENTS.md` § Platform repositories; a machine may record a different path in `CLAUDE.local.md`):

```bash
git -C ../vnext-docs pull --ff-only            # only if the user agrees to refresh it
find ../vnext-docs/docs ../vnext-docs/architecture -name '*.md*' | sort
```

Check both the Turkish source (`docs/`) and the English translation
(`i18n/en/docusaurus-plugin-content-docs/current/`).

**Fallback only when no clone exists** (and after asking once whether to clone it to
`../vnext-docs`): read the remote through `gh api`:

```bash
gh api "repos/burgan-tech/vnext-docs/git/trees/main?recursive=1" \
  --jq '.tree[] | select(.path | startswith("docs/")) | .path'
gh api "repos/burgan-tech/vnext-docs/contents/<path>" --jq '.content' | base64 -d
```

### Step 3: Classify and Act

#### A — New Feature (no existing doc counterpart)

1. State clearly: "Bu özellik vnext-docs'da henüz belgelenmemiş. Yeni döküman oluşturulmalı."
2. Produce a complete Markdown file following Docusaurus conventions (read [REFERENCE.md](REFERENCE.md) § Doc Template and § Docusaurus Conventions)
3. Specify the exact target path: `docs/<category>/<filename>.md`
4. Indicate where to register it in the sidebar file (`sidebars.ts` or `sidebars-architecture.ts`; snippet in [REFERENCE.md](REFERENCE.md) § Sidebar Registration)
5. If the feature is user-facing, note that EN translation is also needed at `i18n/en/docusaurus-plugin-content-docs/current/<category>/<filename>.md`

#### B — Feature Update (existing doc needs changes)

1. State clearly: "Bu özellik mevcut dökümanda bulunuyor → `docs/<path>`"
2. Fetch and display the current doc content
3. Produce a **diff-style change list** showing:
   - Which sections need updating (with line references)
   - New content to add
   - Content to remove or rewrite
   - Any sidebar or frontmatter changes needed
4. If EN translation exists, list the corresponding EN file that also needs updating

#### C — Breaking Change (parallel agent)

Launch a **parallel Agent** to produce a separate breaking-change document:

```
Agent(subagent_type="general-purpose", description="Breaking change doc for <feature>")
```

The breaking-change document must include:
- **What changed** — before vs after behavior
- **Migration path** — step-by-step upgrade instructions
- **Affected components** — list of workflows/tasks/APIs impacted
- **Timeline** — deprecation period if applicable

Output path: `docs/migration/<feature-name>-breaking-change.md` (or suggest a blog post under `blog/` if it aligns with a release).

### Step 4: Output the Documentation

Write the generated doc content to a local file under `ai-docs/vnext-docs/` in the vnext repo so the user can review before committing to vnext-docs.

```
ai-docs/vnext-docs/<category>/<filename>.md          # Turkish (primary)
ai-docs/vnext-docs/i18n-en/<category>/<filename>.md   # English (if needed)
```

## Checklist

Present this checklist to the user after generating docs:

```
Documentation Checklist:
- [ ] Doc content reviewed for accuracy
- [ ] Frontmatter (title, sidebar_position, description) set
- [ ] Sidebar entry added in correct position
- [ ] Code examples tested
- [ ] Internal links verified
- [ ] EN translation needed? (priority pages: getting-started, concepts, how-to)
- [ ] Breaking change doc generated? (if applicable)
- [ ] Blog post needed for release notes?
```
