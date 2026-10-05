# vnext-docs reference — vnext-docs-generator

Loaded on demand by the `vnext-docs-generator` skill: the category mapping when classifying a change
(Step 1), the doc template and conventions when writing a page (Step 3A), the sidebar snippet when
registering a new page.

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
