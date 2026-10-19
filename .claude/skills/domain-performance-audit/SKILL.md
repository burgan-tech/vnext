---
name: domain-performance-audit
description: "Performance audit of a vNext domain package (e.g. vnext-onboarding). Points to the vnext-ai-toolkit plugin's canonical domain-check and adds runtime-source verification. Triggers: \"domain performans\", \"performans incele\", \"bu domaini incele\", \"domain audit\". For a diff of this repo use pr-review."
---

# Domain Performance Audit → vnext-ai-toolkit `domain-check`

The audit moved to the **vnext-ai-toolkit** plugin, where domain teams can run it inside their own
package without a runtime checkout ([burgan-tech/vnext-ai-toolkit#27](https://github.com/burgan-tech/vnext-ai-toolkit/issues/27)).
The plugin copy is canonical: procedure, the 20 checks, the scanner and the report style are in
`skills/domain-check/{SKILL.md,CHECKS.md,scan.py}` of
[burgan-tech/vnext-ai-toolkit](https://github.com/burgan-tech/vnext-ai-toolkit). Do not copy them
back here — fix a wrong check in the plugin repo, so the two cannot drift.

## Run it

- **Plugin installed:** `/vnext-ai-toolkit:domain-check <domain-root> [--config <file>]`, or say
  "bu domaini incele: ../vnext-onboarding" and follow the plugin's `domain-check` skill.
- **Plugin not installed:** clone `burgan-tech/vnext-ai-toolkit` and run its scanner directly:
  `python3 <toolkit>/skills/domain-check/scan.py <domain-root> [--json]`. It is read-only and
  stdlib-only. Enum labels come from the domain's pinned `@burgan-tech/vnext-schema`
  (`node_modules`, else the raw-GitHub tag); a `!! ENUM` or `!! SCHEMA` line means a label could not
  be resolved, and no finding may depend on it.

## What a platform engineer adds

The plugin cites public docs and the schema, because domain teams have no runtime checkout. From this
repo you can do more:

1. **Confirm every "the runtime does X" claim against the code.** `.claude/rules/` and `/docs`
   describe the runtime; when they disagree with the code, the code wins. A wrong claim costs the
   domain team real work. If the plugin's `CHECKS.md` is wrong, open an issue or PR on the plugin.
2. **Compare the domain's `runtimeVersion` with the release it will run on** (`common.props`,
   `vnext-meta/version-manifest.json`) and drop advice that needs a newer runtime.
3. **Measurement stays a rule:** the audit is static. Close the report with how to measure
   (`Instance.Activation`, `Step.*`, `Task.Execute`, `Script.Execute` span durations from
   OpenObserve, before and after) and never state an unmeasured latency gain — see
   `.claude/rules/mcp-observability-verification.md`.

The scope is unchanged: read-only, the report file is the only write, never edit the domain package's
components, never commit, never push. Ask where the report goes and match the target repo's
convention.
