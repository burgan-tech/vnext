# Release fragments

Version-less descriptions of `vnext-meta` changes. This directory is never published to npm
(`package.json` `files` does not list it).

## Why

A pull request cannot know the version it will ship in, yet the shipped files (`features.json`,
`migrations.json`, `deprecations.json`, `known-issues.json`) need one. Two PRs that both guessed the
next version would also conflict on every release. A PR therefore adds one small fragment here, with the
placeholder `"next"` where the version belongs, and the release step stamps it.

## Format

`vnext-meta/unreleased/<slug>.json`, where the slug matches `^[a-z0-9][a-z0-9-]*$`:

| Key | Required | Meaning |
|-----|----------|---------|
| `releaseNote` | yes | `summary` (non-empty string, at most 200 characters), `breaking` (boolean), `docs` (array of `https://` strings, may be empty). Feeds the release blog. |
| `schemaVersion` | no | Semver; the schema version the release is built against. |
| `features` | no | `group -> key -> entry`; every entry has `"since": "next"`. A key that already exists in `features.json` or in another fragment is an error. |
| `migrations` | no | Array of entries, each with a string `id` and `"since": "next"`. |
| `deprecations` | no | Array of entries, each with a string `id`, `"deprecatedSince": "next"` and `"removedAt": null`. |
| `knownIssues` | no | Array of entries, each with a string `id`, an `affectedVersions` string containing the token `next` (for example `">=next"`) and `"fixedIn": null`. |
| `fixes` | no | `knownIssues`: ids of existing open known issues (`fixedIn` null); `deprecationsRemoved`: ids of existing deprecations with `removedAt` null. |

A fragment may carry only a `releaseNote`. Unknown top-level keys, and unknown keys inside `fixes`, are
errors (this catches typos such as `knownIssue`). An id may appear only once across the stamped files
and all fragments. A new known issue cannot be fixed in the same release that introduces it.

Entries are copied verbatim into the stamped files, so write them exactly as they should read there,
apart from the version placeholder.

## Version placeholders

`"next"` is the only placeholder, and it is valid only here. The lint (`META-SINCE`) rejects `"next"` in
a stamped file, and rejects any concrete version in a fragment entry (`META-FRAGMENT`).

## What the release does

At release time the stamp rewrites each shipped file with the real version: it replaces the value
`"next"` in `since`, `deprecatedSince`, `enforcedSince`, `fixedIn` and `removedAt`, and the token `next`
in `affectedVersions`. `fixes.knownIssues` sets `fixedIn` on the named known issue, and
`fixes.deprecationsRemoved` sets `removedAt` on the named deprecation. The fragments are then deleted
and their release notes go into the release manifest.

## Back-merge rule

When a release branch is merged back into the development branch, any fragment the release already
consumed must be deleted in that merge. A fragment that survives a release would be stamped a second
time with the next version.

## Example

`vnext-meta/unreleased/my-feature.json`:

```json
{
  "releaseNote": {
    "summary": "Adds the myFeature engine capability.",
    "breaking": false,
    "docs": ["https://burgan-tech.github.io/vnext-docs/docs/my-feature"]
  },
  "features": {
    "engine": { "myFeature": { "since": "next", "status": "stable", "description": "What it does." } }
  },
  "knownIssues": [
    { "id": "my-feature-limit", "affectedVersions": ">=next", "fixedIn": null, "description": "A limit users should know." }
  ],
  "fixes": { "knownIssues": ["an-existing-open-issue"] }
}
```

Check it with `node scripts/governance/meta-lint.mjs`.
