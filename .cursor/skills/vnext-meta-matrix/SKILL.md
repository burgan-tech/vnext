---
name: vnext-meta-matrix
description: "Generates a comprehensive matrix report from vnext-meta JSON files. Outputs a markdown table showing features, deprecations, known issues, components, and limits by version. Use when the user says 'meta matrix', 'meta rapor', 'feature matrix', 'meta ozet', or 'meta tablo'."
---

# vnext-meta Matrix Report

## Trigger

Activate when the user asks for a consolidated view of runtime metadata: **meta matrix**, **meta rapor**, **feature matrix**, **meta ozet**, **meta tablo**, or equivalent requests for a summary table from `vnext-meta`.

## Skill Instructions

1. Read every JSON file under `vnext-meta/` (`version-manifest.json`, `features.json`, `deprecations.json`, `migrations.json`, `known-issues.json`, `component-registry.json`, `performance-profiles.json`, `security-policy.json`).
2. Build a markdown report with these sections:
   - **Version Summary** — from `version-manifest.json` (and briefly note `migrations.json` when empty or populated).
   - **Feature Matrix** — tables for engine / API / integration features from `features.json` with **since** version (and endpoint or description).
   - **Component Catalog** — tables for tasks, functions, extensions from `component-registry.json` with stability tags.
   - **Deprecation Tracker** — table from `deprecations.json` with timeline fields.
   - **Known Issues** — table from `known-issues.json` with severity and workarounds.
   - **Performance Limits** — tables from `performance-profiles.json`, grouped by limit category (workflow, instance, query, runtime, serialization, retention, cache).
   - **Security Policies** — table from `security-policy.json`.
3. Write **`./ai-docs/vnext-meta-matrix.md`** at the repo root. Never overwrite unrelated docs (e.g. `feature_catalog.md`).
4. Start the document with `# vnext-meta Matrix Report` and a blockquote header line including **runtime version** (prefer `features.runtimeVersion` or `version-manifest` keys; reconcile with `common.props` if needed) and **generation timestamp**.

### Section detail

- **Header**: Blockquote format `Runtime Version: X.X.X | Generated: YYYY-MM-DD` (or full ISO-8601 when time matters).
- Use **text tags only** (no emoji): `[STABLE]`, `[EXPERIMENTAL]`, `[DEPRECATED]`, `[WARNING]`, `[ERROR]`, `[INFO]` as appropriate.
- Prefer **clean GitHub-flavored markdown tables**; escape or shorten prose that breaks pipes in table cells.

## Quality Bar

- Apply the tags from **Section detail** consistently across features, components, deprecations, and known issues.
- Escape or shorten cell text that breaks table parsing (pipes in prose).
- Keep the document **comprehensive but scannable**; split wide tables by subsection if needed.

## Output Path

Always write the generated report to:

`ai-docs/vnext-meta-matrix.md`
