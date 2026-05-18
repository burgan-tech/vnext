# @burgan-tech/vnext-meta

npm metadata bundle for the vNext workflow runtime: structured JSON for tooling that runs **offline** without loading .NET assemblies.

## Contents

| File | Purpose |
|------|---------|
| `version-manifest.json` | Released runtime versions, schema versions, release pointers. |
| `features.json` | Engine, API, and integration capability catalog. |
| `deprecations.json` | Deprecated APIs and behaviors with timelines or replacements. |
| `migrations.json` | Breaking-change and upgrade guidance between versions. |
| `known-issues.json` | Documented limitations, caveats, and operational notes. |
| `component-registry.json` | Task types and component identifiers understood by the runtime. |
| `performance-profiles.json` | Documented limits and performance-related constants. |
| `security-policy.json` | Input validation and authorization-related policy summary. |

## Usage

Install from your org registry (configure `.npmrc` for `@burgan-tech` as needed):

```bash
npm install @burgan-tech/vnext-meta
```

CommonJS:

```js
const meta = require('@burgan-tech/vnext-meta');
console.log(meta.features);
```

ESM (via default import from a loader or bundler that resolves JSON):

```js
import meta from '@burgan-tech/vnext-meta';
```

The package exposes each JSON document as a named export on the default export object (see `index.js`).

## Version alignment

The npm package **version should match** the runtime release defined in the repo root `common.props` (`Version`). Consumers should pin `@burgan-tech/vnext-meta` to the same semver as the orchestration/execution hosts they target.

## Offline-first design

All payloads are **static JSON** shipped inside the package. There is **no runtime dependency** on live services or the .NET hosts to read manifests—suitable for Forge Studio, CLIs, code generators, and documentation pipelines running in isolated environments.
