---
product: <product>
milestone: <vX.Y.Z>
status: pending
---

# <Feature> — Handoff for <product>

Copy this file to `docs/integration/<product>/<slug>.md`, fill every section, and link it from the
`## Product impact` row of the vNext PR. Convention: [README](README.md).

## Context

- vNext PR: <https://github.com/burgan-tech/vnext/pull/N>
- Milestone: <vX.Y.Z>
- Issue: <link, if any>

## What changed in the runtime

<!-- The runtime behaviour, contract or configuration that changed, in the product's terms. Name the
     source of truth (file, doc page, vnext-meta entry) a reader checks when this page and the engine
     disagree. -->

## What this product must do

<!-- The concrete work for this product: fields, forms, schema entries, chart values, CLI commands,
     docs pages, tests. One bullet per change. -->

## Contract and examples

<!-- Request/response bodies, JSON component snippets, error codes, configuration keys — whatever the
     product needs to build against without reading runtime source. -->

## Acceptance

<!-- How the product team knows it is done: checks, test cases, the example flow that must work. -->

## Out of scope

<!-- What this product must not do or can leave for later. -->
