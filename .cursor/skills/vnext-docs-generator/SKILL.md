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
- Which domain area it belongs to (see [Category Mapping](#category-mapping))
- Whether this is a **new feature**, **feature update**, or **breaking change**

The user may also provide additional context and details beyond the diff. Wait for this input before proceeding.

**Clarify ambiguities before writing.** If any of the following are unclear, ask the user using AskQuestion or conversationally:
- The scope/boundary of the feature (what's included, what's not)
- The intended audience for the doc (developer, architect, business)
- Behavioral nuances not obvious from the code (edge cases, defaults, fallback logic)
- Whether a config change is optional vs required
- Naming preferences for the doc page or section titles
- Priority of EN translation

Do NOT guess on ambiguous points — ask first, then generate.

### Step 2: Fetch Current Docs State

Use `gh` CLI to check if relevant documentation already exists:

```bash
# List existing doc files in the relevant category
gh api "repos/burgan-tech/vnext-docs/git/trees/main?recursive=1" \
  --jq '.tree[] | select(.path | startswith("docs/")) | .path'

# Fetch a specific doc file content
gh api "repos/burgan-tech/vnext-docs/contents/<path>" --jq '.content' | base64 -d
```

Check both Turkish source (`docs/`) and English translation (`i18n/en/docusaurus-plugin-content-docs/current/`).

### Step 3: Classify and Act

#### A — New Feature (no existing doc counterpart)

1. State clearly: "Bu özellik vnext-docs'da henüz belgelenmemiş. Yeni döküman oluşturulmalı."
2. Produce a complete Markdown file following Docusaurus conventions (see [Doc Template](#doc-template))
3. Specify the exact target path: `docs/<category>/<filename>.md`
4. Indicate where to register it in the sidebar file (`sidebars.ts` or `sidebars-architecture.ts`)
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

Launch a **parallel Task agent** to produce a separate breaking-change document:

```
Task(subagent_type="generalPurpose", description="Breaking change doc for <feature>")
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

## Category Mapping

Map code changes to docs categories:

| Code Area | Docs Category | Sidebar |
|-----------|---------------|---------|
| Pipeline steps, transitions | `docs/components/workflow` or `architecture/domain-model/` | `sidebars.ts` or `sidebars-architecture.ts` |
| Task types (HTTP, Script, Dapr, etc.) | `docs/components/tasks/<type>.md` | `sidebars.ts` → Workflow → Tasks |
| Functions (built-in, custom) | `docs/components/functions/` | `sidebars.ts` → Workflow → Functions |
| Instance data, schema | `docs/concepts/instance-data` or `docs/components/schema` | `sidebars.ts` → Core Concepts |
| Error handling, boundaries | `docs/how-to/error-handling` | `sidebars.ts` → Practical Guides |
| Views, extensions | `docs/components/view` or `docs/components/extension` | `sidebars.ts` → Workflow |
| SubFlow, correlation | `docs/getting-started/tutorial-subflow` | `sidebars.ts` → Getting Started |
| API endpoints, REST | `docs/api-reference/rest-api` | `sidebars.ts` → API Reference |
| DB schema, persistence | `architecture/data/` | `sidebars-architecture.ts` → Data |
| Domain events, patterns | `architecture/patterns/` | `sidebars-architecture.ts` → Patterns |
| Config (URL templates, discovery, timeout) | `docs/configuration/` | `sidebars.ts` → Yapılandırma |
| Mappings, interfaces | `docs/components/mappings` or `docs/components/interfaces` | `sidebars.ts` → Workflow |

## Doc Template

```markdown
---
sidebar_position: <number>
title: <Title>
description: <One-line description for SEO and sidebar>
---

# <Title>

<Brief introduction — what this feature/component does and why it exists.>

## Genel Bakış

<High-level explanation with a diagram or flow if applicable.>

## Yapılandırma

<Configuration options, JSON/YAML examples with field descriptions.>

## Kullanım

<Step-by-step usage with code snippets.>

## Örnekler

<Concrete examples showing real-world usage patterns.>

## İlgili Konular

- [Related Doc 1](./related-1)
- [Related Doc 2](./related-2)
```

## Docusaurus Conventions

- **No HTML comments** — use `{/* comment */}` for MDX comments
- **Frontmatter required** — every page needs `title` at minimum
- **Language** — TR primary, EN secondary; new content always starts in TR
- **Blog truncation** — use `{/* truncate */}` marker
- **Admonitions** — use `:::note`, `:::tip`, `:::warning`, `:::danger`, `:::info`
- **Code blocks** — use fenced blocks with language tag; add `title="filename"` for file context
- **Links** — relative paths for internal links: `[text](./sibling)` or `[text](../parent/child)`

## Sidebar Registration

When adding a new page, show the user the sidebar diff:

```typescript
// sidebars.ts — add to the relevant category
{
  type: 'category',
  label: 'Category Name',
  items: [
    'category/existing-page',
+   'category/new-page',      // ← new doc
  ],
}
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
