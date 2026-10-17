# Integration Handoffs — Runtime Changes Other Products Must Follow

A runtime PR that needs work in another platform product (see the `## Product impact` table of
[`.github/PULL_REQUEST_TEMPLATE.md`](../../.github/PULL_REQUEST_TEMPLATE.md)) hands that work over
through a handoff document in this folder, or through a PR/issue opened in the product's own
repository. This page is the convention for the document.

## Layout

```
docs/integration/
├── README.md              this page
├── _template.md           copy this to start a handoff
└── <product>/<slug>.md    one handoff per runtime change and product
```

- `<product>` is one of the product names of the PR template's `## Product impact` table:
  `vnext-schema`, `vnext-forge`, `vnext-helm-charts`, `vnext-docs`, `vnext-workflow-cli`,
  `vnext-runtime`, `vnext-template`, `vnext-integration-test`, `vnext-example`, `vnext-ai-toolkit`.
- `<slug>` is short kebab-case naming the feature, e.g. `vnext-forge/fanout-task.md`.
- One runtime change that needs three products gets three files, one per product folder.

## Frontmatter

Every handoff starts with:

```yaml
---
product: vnext-forge        # the folder name; must equal the product row it answers
milestone: v0.0.101         # must equal the milestone of the vNext PR that adds the file
status: pending             # pending | in-progress | done
---
```

The `PR Contract` workflow (`scripts/governance/pr-contract.mjs`, rule `PR-HANDOFF-DOC`) checks these
three fields on every handoff that a `yes` row links; `PR-HANDOFF` checks that the linked file is part
of the PR's changed files.

## Who writes it, who links it

- **The author of the vNext PR** writes the handoff, in the same PR, starting from
  [`_template.md`](_template.md). The `create-github-pr` skill proposes a draft for every `yes` row.
- **The vNext PR** links the file from its `## Product impact` row (`Handoff` column), or instead links
  a PR/issue URL in `https://github.com/burgan-tech/<product>`.
- **The sibling PR** in the product repository links back to both this document and the vNext PR, and
  the product team moves `status` to `in-progress` / `done` with a small follow-up PR here.

## Legacy, pre-convention documents

These were written before the convention and stay where they are; they have no frontmatter and are not
checked:

- [Forge Studio — FanOutTask (type 21) Implementation Spec](forge-fanout-task-implementation.md)
- [Field Protection — Revision Guide for vnext-docs and Forge Studio](field-protection-consumer-revision.md)
