---
name: vnext-meta-validator
description: Validates vnext-meta package JSON files for schema compliance, version consistency, and codebase alignment. Use when the user says "validate meta", "meta check", "meta kontrol", "vnext-meta validate", or after modifying vnext-meta JSON files.
---

# vNext Meta Validator

## Trigger

Activate when:

- The user says any of: **validate meta**, **meta check**, **meta kontrol**, **vnext-meta validate**, **meta dogrula** (Turkish spelling variants may omit diacritics: **dogrula** / **doğrula**).
- **Immediately after** any edit under `vnext-meta/` (same session / follow-up turn).

Do **not** skip categories because the change touched only one file — run all applicable checks in [CHECKS.md](CHECKS.md) and report PASS/FAIL per category.

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
3. **Run checks 1–8** — read [CHECKS.md](CHECKS.md) now; it holds every category's goal, files and
   checklist. Run them in order and emit **PASS** or **FAIL** per category with evidence.
4. **Summarize** using the Output Format template; for every FAIL, give **concrete suggested fixes** (file + field or code symbol).

**Tools**: When `graphify-out/graph.json` exists, use `graphify explain "<symbol>"` to locate a type before grepping (see `.claude/rules/graphify-navigation.md`); otherwise targeted `grep` on the symbol name. Open the C# files referenced in `performance-profiles.json` `sources` when verifying numeric limits.

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
