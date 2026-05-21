---
name: vnext-meta-validator
description: Validates vnext-meta package JSON files for schema compliance, version consistency, and codebase alignment. Use when the user says "validate meta", "meta check", "meta kontrol", "vnext-meta validate", or after modifying vnext-meta JSON files.
---

# vNext Meta Validator

## Trigger

Activate when:

- The user says any of: **validate meta**, **meta check**, **meta kontrol**, **vnext-meta validate**, **meta dogrula** (Turkish spelling variants may omit diacritics: **dogrula** / **doğrula**).
- **Immediately after** any edit under `vnext-meta/` (same session / follow-up turn).

Do **not** skip categories because the change touched only one file — run all applicable checks below and report PASS/FAIL per category.

---

## Scope

**Root**: `vnext-meta/` at the repository root.

| File | Role |
|------|------|
| `package.json` | NPM package identity; `version` must match repo release. |
| `version-manifest.json` | Released runtime versions and `schemaVersion`. |
| `features.json` | Feature catalog keyed by area (`engine`, `api`, …). |
| `component-registry.json` | Tasks, functions, extensions with `key`, `since`, `stable`, `domains`. |
| `performance-profiles.json` | Numeric limits + `sources` mapping to C# constants. |
| `deprecations.json` | Structured deprecation items. |
| `security-policy.json` | Policy statements tied to enforcement in code. |
| `migrations.json` | Migration notes (structure as in repo). |
| `known-issues.json` | Known issues register (structure as in repo). |
| `index.js` | Package entry (non-JSON; only if user changed it or asks for full package health). |

---

## Workflow

1. **Collect current versions** — Read `common.props` (`<Version>`) and `vnext-meta/package.json` (`version`). Note the effective "current" runtime version string.
2. **Parse JSON** — Ensure every `*.json` under `vnext-meta/` parses; record line/column on failure.
3. **Run checks 1–8** below in order; for each category emit **PASS** or **FAIL** with evidence.
4. **Summarize** using the Output Format template; for every FAIL, give **concrete suggested fixes** (file + field or code symbol).

**Tools**: Prefer the `code-review-graph` MCP tools (semantic_search_nodes, query_graph) over Grep when possible. Open the C# files referenced in `performance-profiles.json` `sources` when verifying numeric limits.

---

## 1. Schema compliance

**Goal**: Each JSON file matches the **expected structural contract** used by this package (fields, nesting, types).

### 1.1 `version-manifest.json`

- [ ] Top-level object with `versions` map.
- [ ] Each version key is a SemVer-like string; value has at least `schemaVersion` (string), `releasedAt` (ISO date string), optional `releaseNotes` (path string).
- [ ] `schemaVersion` matches the parent key unless documented otherwise.

### 1.2 `package.json`

- [ ] `name`, `version`, `files` array includes all shipped JSON artifacts (per `files` list in repo).

### 1.3 `features.json`

- [ ] Has `runtimeVersion` (string).
- [ ] Group objects (e.g. `engine`, `api`) contain feature entries; each entry has `since` (string) and descriptive fields (`description` and/or `endpoint` as appropriate).
- [ ] No empty or malformed feature keys.

### 1.4 `component-registry.json`

- [ ] Top-level arrays: `tasks`, `functions`, `extensions` (all arrays of objects).
- [ ] Each item: `key` (string), `since`, `stable` (boolean), `domains` (array), `configSchema` (nullable).

### 1.5 `performance-profiles.json`

- [ ] `profiles` array; each profile has `since`, `limits` (nested objects), `sources` (map string → fully qualified C# symbol string).

### 1.6 `deprecations.json`

- [ ] `items` array; each item has identifiers (`id`, `type`, …), version fields (`deprecatedSince`, optional `removedAt`), `severity`, `message`.

### 1.7 `security-policy.json`

- [ ] `since`; `policies` array with `id`, `scope`, `enforcedSince`, `description`.

### 1.8 `migrations.json` / `known-issues.json`

- [ ] Validate against prevailing shapes in-repo (arrays/maps with stable `id` or issue keys); flag unexpected `null` roots or type drift.

---

## 2. Version consistency

**Goal**: Published meta versions agree with the **canonical MSBuild version**.

### Sources of truth

- **`common.props`**: `<Version>` — authoritative runtime/package alignment target.
- **`vnext-meta/package.json`**: `version` must equal `<Version>`.
- **`vnext-meta/features.json`**: `runtimeVersion` must equal `<Version>` when documenting the current tip (unless intentionally documenting a fork — then FAIL with explanation).
- **`vnext-meta/version-manifest.json`**: Must contain an entry for the current `<Version>` if that version is released and described in meta; `schemaVersion` inside that entry must match the key.

### Checks

- [ ] `common.props` Version == `package.json` version.
- [ ] `features.json` `runtimeVersion` == `common.props` Version.
- [ ] `version-manifest.json` includes current Version with consistent `schemaVersion`.
- [ ] Scan all `since`, `deprecatedSince`, `enforcedSince`, `releasedAt` references: no impossible ordering (e.g. feature `since` newer than manifest without corresponding manifest entry — use judgment and list anomalies).

---

## 3. Component registry alignment

**Goal**: Registry keys mirror **runtime constants and enums**.

### Tasks → `TaskTypes`

- **File**: `src/BBT.Workflow.Execution.Abstractions/TaskTypes.cs`
- **Rule**: Every `public const string` value must appear exactly once as a `tasks[].key` in `component-registry.json`, and **every** `tasks[].key` must match a const value (lowercase naming per file comments).

### Functions → `FunctionTypeConst`

- **File**: `src/BBT.Workflow.Domain/Definitions/Functions/FunctionTypeConst.cs`
- **Rule**: Const **values** (e.g. `"state"`, `"permissions"`) must match `functions[].key`. Cover **all** consts; registry must not invent undocumented function keys.

### Extensions → `ExtensionType`

- **File**: `src/BBT.Workflow.Domain/Definitions/Extensions/ExtensionEnums.cs` — enum `ExtensionType`
- **Rule**: JSON uses **camelCase** keys derived from enum names (`Global` → `global`, `DefinedFlowAndRequested` → `definedFlowAndRequested`). Every enum member must have a registry row and vice versa.

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

### Checks

- [ ] `runtimeVersion` / `package.json` / `common.props` — aligned (see §2).
- [ ] All `since` fields reference versions present or plausible on the timeline (`version-manifest.json` optional entries noted).
- [ ] `component-registry.json` `since` ≤ features referencing same components where cross-linked.
- [ ] `performance-profiles.json` profile `since` compatible with manifest.
- [ ] Links/paths (`releaseNotes`) resolve under repo if stored in-repo.

---

## Output format

Emit validation results in **markdown** using this structure:

```markdown
## vnext-meta validation summary

**Scope**: `vnext-meta/` (+ referenced `common.props`, C# sources)
**Baseline version**: `<Version from common.props>`

### 1. Schema compliance — PASS | FAIL
- Details: ...

### 2. Version consistency — PASS | FAIL
- Details: ...

### 3. Component registry alignment — PASS | FAIL
- Details: ...

### 4. Performance profile accuracy — PASS | FAIL
- Details: ...

### 5. Feature existence — PASS | FAIL
- Details: ...

### 6. Deprecation tracking — PASS | FAIL
- Details: ...

### 7. Security policy grounding — PASS | FAIL
- Details: ...

### 8. Cross-reference integrity — PASS | FAIL
- Details: ...

## Mismatches (if any)

| Location | Expected | Actual | Suggested fix |
|----------|----------|--------|---------------|
| ... | ... | ... | ... |

## Suggested fixes

- Ordered list of concrete actions (edit file X, add const Y, update limit Z to match `WorkflowConstants.*`, …)
```

**Rules**

- Use **PASS** only if all sub-bullets in that section succeed; one defect → **FAIL**.
- Prefer **tables and file paths** over vague prose.
- For large FAIL sets, cap the table at the **top 25** issues and note "truncated"; still mark section FAIL.

---

## Notes for agents

- After localized edits, **re-run full validation** before declaring the meta package release-ready.
- If JSON schema files are added later under `vnext-meta/`, extend §1 using those schemas as machine-verifiable contracts.
- Keep suggestions minimal and reversible: align meta **to code** unless the user explicitly intends to document future behavior.
