---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# Component version diagnostics (#1192) — Handoff for vnext-docs

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/1316
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1192
- Related: the floating-version resolution fix, released in v0.0.106 (known issue `floating-version-resolution-loads-every-version`)

## What changed in the runtime

New instances are pinned for life to the full version their request resolved to. When a domain team sees
new instances start on an unexpected version, it can now find out why without database access:

- `GET /api/v1/{domain}/components/{type}/{key}/versions` (new) lists a component's published versions,
  newest publish first, with its default-version pointer and what a request with no version resolves to,
  read from the database.
- `GET /api/v1/{domain}/components/{type}/{key}[/{version}]` (existing) returns what a request resolves to
  through the cache — the version new instances start on.
- `POST /api/v1/utilities/invalidate` (existing) forces re-resolution.
- The component lists (`GET {domain}/components`, `GET {domain}/components/{type}`) no longer load every
  stored body, and their `createdAt` is now the time each version was published (it was empty before).

Source of truth: `docs/domain/domain-cache-context.md` (section "Diagnosing which version a request
resolves to") and `docs/runtime/flow-default-version.md` in the vnext repo.

## What this product must do

- Add a "Which version does a new instance start on?" troubleshooting page for domain teams: the three
  calls above, how to compare the database answer with the cached one, and how to force re-resolution.
- State plainly on the flow default version page: publishing never moves the default-version pointer; with
  a pointer set, a newly published version stays pending until `set-default` moves it.

## Contract and examples

```http
GET /api/v1/contract/components/workflows/contract-approval-workflow/versions?page=1&pageSize=20
```

```json
{
  "type": "workflows",
  "componentTypeKey": "sys-flows",
  "domain": "contract",
  "key": "contract-approval-workflow",
  "totalVersions": 190,
  "latest": { "version": "1.0.0-pkg.1.0.350+contract", "outcome": "resolved", "source": "automatic" },
  "items": [
    { "version": "1.0.0-pkg.1.0.350+contract", "publishedAt": "2026-11-09T09:28:04Z", "latestRow": true },
    { "version": "1.0.0-pkg.1.0.349+contract", "publishedAt": "2026-11-08T14:02:11Z", "latestRow": false }
  ],
  "page": 1,
  "pageSize": 20,
  "hasNext": true
}
```

- `defaultVersion`: the `sys-version-registry` pointer; omitted when none is set (Automatic), as the API omits every null field. Workflows only.
- `latest.outcome`: `resolved`, `notFound` or `pointerInvalid` (the pointer matches no published version).
- `latest.source`: `automatic` (no pointer, the highest published version) or `default` (the pointer decided).
- `items` are newest publish first. `latestRow` marks the row the store flags latest — the highest version written so far, which is the definition the instance API shows for the component; publishing an older build after it leaves the flag on the higher one.
- `items` holds one entry per version, newest publish first; `totalVersions` counts the same versions.
- 404 when the component has no published version; 503 `Instance:100053` when the registry cannot be read;
  409 `Instance:100052` when the stored pointer is malformed — the same answers a start would get.
- Exposure: the same as the other component discovery reads (hidden from Swagger, behind the gateway like
  them); it returns version metadata and the pointer only, never a component body or instance data.

Re-resolution:

```http
POST /api/v1/utilities/invalidate
{ "domain": "contract", "flow": "sys-flows", "key": "contract-approval-workflow", "version": "1.0.0-pkg.1.0.350+contract" }
```

It bumps the component's cache generation (every cached resolution becomes unreachable) and re-warms the
common spellings from the stored versions.

## Acceptance

- The troubleshooting page walks through the #1192 case: publish several builds, compare
  `…/versions` → `latest` with `GET …/components/workflows/{key}`, and re-resolve with `utilities/invalidate`.
- The flow default version page states that publishing never moves the pointer.

## Out of scope

- A `wf` CLI command or a Forge view for the version list (possible follow-ups, not required by the runtime).
- Documenting the internal cache keys; the page describes behaviour, not Redis layout.
