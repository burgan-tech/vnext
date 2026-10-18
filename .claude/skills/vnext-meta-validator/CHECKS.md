# vnext-meta validation checks 1–8

Loaded by the `vnext-meta-validator` skill at workflow step 3. Run every category in order and
report PASS/FAIL per category with evidence.

---

## Mechanical checks — run the lint first

The deterministic parts of §1, §2, §3 and §8 are `scripts/governance/meta-lint.mjs`, which the
`PR Contract` workflow also runs on every PR regardless of its title:

```bash
node scripts/governance/meta-lint.mjs                       # exit 1 on any error
node scripts/governance/meta-lint.mjs --milestone v0.0.101  # a `since` may name the upcoming release (back-compat)
node scripts/governance/meta-lint.mjs --json                # machine-readable findings
```

Quote every finding by its id in the category it belongs to. Do not re-derive these checks by hand;
the sections below say which ids cover them and what is left for the agent.

| Id | What it checks |
|---|---|
| `META-PARSE` | Every meta JSON file exists and parses; `index.js` can be required. |
| `META-SHAPE` | Root shapes: manifest `versions` object, features `runtimeVersion` string, registry `tasks` / `functions` / `extensions` arrays, `items` arrays in deprecations / migrations / known-issues. |
| `META-MANIFEST-ORDER` | Manifest keys are semver, strictly descending, with `releasedAt` `YYYY-MM-DD` not increasing down the list. |
| `META-MANIFEST-ENTRY` | Each manifest entry has a semver `schemaVersion` and an `https://` `releaseNotes` link. |
| `META-RUNTIME-VERSION` | `features.json` `runtimeVersion` equals the newest manifest key. |
| `META-SINCE` | Every `since` / `deprecatedSince` / `enforcedSince` / `fixedIn` is null or a released version; one ahead of the newest release must equal `--milestone`; `"next"` is rejected outside `vnext-meta/unreleased/`. |
| `META-REGISTRY-KEY` | Registry keys are unique per kind and each exists in code (see §3). |
| `META-ID-UNIQUE` | `id`s are unique in deprecations, migrations and known-issues, across the stamped files and every release fragment. |
| `META-FRAGMENT` | Each `vnext-meta/unreleased/<slug>.json` fragment: slug, shape, unknown keys, `releaseNote`, version placeholders are `"next"`, feature-key collisions, `fixes` targets exist. Format: [`vnext-meta/unreleased/README.md`](../../../vnext-meta/unreleased/README.md). |

**Release stamp.** A PR describes a change as a version-less fragment in `vnext-meta/unreleased/`; the
release's `prepare` job runs `scripts/governance/meta-stamp.mjs --version X.Y.Z`, which writes the real
version into the shipped files, adds the manifest row and deletes the fragments, then re-lints. The
recommended path for a new `since` is therefore a fragment; naming the upcoming release through
`--milestone` stays valid for back-compat.

---

## 1. Schema compliance

**Goal**: Each JSON file matches the **expected structural contract** used by this package (fields, nesting, types).

**Mechanical**: `META-PARSE`, `META-SHAPE`, `META-MANIFEST-ORDER`, `META-MANIFEST-ENTRY`,
`META-ID-UNIQUE`. **Agent**: the item-level fields below, which the lint does not read.

### 1.1 `version-manifest.json`

- [ ] `schemaVersion` is the `@burgan-tech/vnext-schema` version that runtime release ships with. It
  does **not** equal the parent key (`0.0.100` → `0.0.56`); do not flag the difference. The release
  stamp sets it: the highest of the previous row and any fragment's `schemaVersion`, carried over when
  none, or the `schema_version` dispatch input.

### 1.2 `package.json`

- [ ] `name` and a `files` array that includes all shipped JSON artifacts. The `version` field is
  **not** checked: the publish job overwrites it.

### 1.3 `features.json`

- [ ] Group objects (e.g. `engine`, `api`) contain feature entries; each entry has `since` (string) and descriptive fields (`description` and/or `endpoint` as appropriate).
- [ ] No empty or malformed feature keys.

### 1.4 `component-registry.json`

- [ ] Each item: `key` (string), `since`, `stable` (boolean), `domains` (array), `configSchema` (nullable).

### 1.5 `performance-profiles.json`

- [ ] `profiles` array; each profile has `since`, `limits` (nested objects), `sources` (map string → fully qualified C# symbol string).

### 1.6 `deprecations.json`

- [ ] Each item has identifiers (`id`, `type`, …), version fields (`deprecatedSince`, optional `removedAt`), `severity`, `message`.

### 1.7 `security-policy.json`

- [ ] `since`; `policies` array with `id`, `scope`, `enforcedSince`, `description`.

### 1.8 `migrations.json` / `known-issues.json`

- [ ] Items follow the prevailing in-repo shape; flag unexpected `null` fields or type drift.

---

## 2. Version consistency

**Goal**: Version fields agree with the **release timeline in `version-manifest.json`**.

**Mechanical**: `META-RUNTIME-VERSION` (`features.json` `runtimeVersion` == newest manifest key) and
`META-SINCE` (every version reference is a released version, or the PR milestone). Pass the PR's
milestone with `--milestone` when meta documents the upcoming release.

Not checked, by design: `vnext-meta/package.json` `version` (the publish job overwrites it, so it never
equals `common.props`), and `schemaVersion` against the key (§1.1).

**Agent**:

- [ ] `common.props` `<Version>` is the version being developed; when meta already documents it, the
  manifest has (or the PR adds) its entry. A `since` naming that version without `--milestone` is the
  lint's warning, not a defect.

---

## 3. Component registry alignment

**Goal**: Every registry key exists in code (**registry ⊆ code**). Completeness — every code constant
having a registry row — is a policy decision and is **not** required; do not FAIL a category for a
missing row.

**Mechanical**: `META-REGISTRY-KEY`. Sources:

| Kind | Valid keys |
|---|---|
| `tasks[].key` | `public const string` values in `src/BBT.Workflow.Execution.Abstractions/TaskTypes.cs` ∪ lowercased `TaskType` enum members in `src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs` |
| `functions[].key` | `public const string` values in `src/BBT.Workflow.Domain/Definitions/Functions/FunctionTypeConst.cs` |
| `extensions[].key` | `ExtensionType` enum members in `src/BBT.Workflow.Domain/Definitions/Extensions/ExtensionEnums.cs`, camelCased (`DefinedFlowAndRequested` → `definedFlowAndRequested`) |

**Agent**: none beyond quoting the findings. Missing rows may be listed as INFO.

---

## 4. Performance profile accuracy

**Goal**: Numeric **limits** match live C# constants and private caps.

### Primary files (non-exhaustive — follow `sources` map in JSON)

| Area | Typical symbols |
|------|-----------------|
| Workflow string limits | `BBT.Workflow.Definitions.WorkflowConstants` (`src/BBT.Workflow.Domain/Definitions/WorkflowConstants.cs`) |
| Transition / version strategy | `TransitionConstants`, `LanguageLabelConstants` |
| Query / security input | `BBT.Workflow.Domain.Security.InputValidator` (`src/BBT.Workflow.Domain/Security/InputValidator.cs`) |
| Auto-chain depth | `TransitionPipeline`: `MaxChainDepth` (`src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs`) — must align with `limits.runtime.maxAutoTransitionChainDepth` |
| Serialization depth | Json serializer / trigger task limits as named in `sources` |

### Checks

- [ ] For **each** entry in `performance-profiles.json` → `sources`, open the referenced symbol and confirm the JSON number matches (or document intentional divergence — default is **FAIL** if mismatch).
- [ ] Every meaningful limit under `limits` has a `sources` entry, or explicitly document in the report why it is derived.
- [ ] Profile `since` versions are plausible relative to manifest.

---

## 5. Feature existence

**Goal**: Entries in `features.json` correspond to **real implementations**, not aspirations.

### Method

For each feature block (especially `engine` and `api`):

- [ ] Extract concrete type or API names from the `description` / `endpoint`.
- [ ] Confirm via search: classes (`PipelineProfileResolver`, `TransitionPipeline`, `AsyncTransitionStrategy`, `ResourceLockStep`, etc.), HTTP routes, or worker jobs **exist** and behave as described.
- [ ] If the text claims a specific **Behavior** (e.g. "bounded at 50"), cross-check with code (same as section 4 where overlap exists).

Mark **FAIL** if the feature reads as shipped but symbols or routes are missing or behavior clearly differs.

---

## 6. Deprecation tracking

**Goal**: `deprecations.json` matches **`[Obsolete]`** and related migration reality.

### Method

- [ ] For each `items[]` entry, search the codebase for matching API surface: property paths (`path`), task names, or messages similar to `message`.
- [ ] Find `[Obsolete]` attributes: **message**, `error`/`warning`, and effective version if present; align with `deprecatedSince` / `removedAt`.
- [ ] Flag **obsolete code** with **no** deprecation item, and **deprecation items** with **no** obsolete marker or leftover API (stale meta).

Severity in JSON should align with compiler warning vs error where applicable.

---

## 7. Security policy grounding

**Goal**: Each `security-policy.json` policy is **traceable** to enforcement code.

### Method

- [ ] Parse `description` for type names (`InputValidator`, `ActorAuthorizationSpecification`, `ITransitionAuthorizationManager`, etc.).
- [ ] Open cited files and confirm the rule is implemented (same limits, same actor checks).
- [ ] FAIL if the policy references a symbol that no longer exists or behavior has moved.

---

## 8. Cross-reference integrity

**Goal**: **Shared version references** and identifiers agree across meta files.

**Mechanical**: `META-RUNTIME-VERSION`, `META-SINCE` (every `since`-style field is on the release
timeline), `META-MANIFEST-ENTRY` (`releaseNotes` is an `https://` link).

### Agent checks

- [ ] `component-registry.json` `since` ≤ features referencing same components where cross-linked.
- [ ] `performance-profiles.json` profile `since` compatible with manifest.
