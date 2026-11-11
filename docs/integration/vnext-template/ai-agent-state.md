---
product: vnext-template
milestone: v0.0.105
status: pending
---

# Agents folder and example agent — Handoff for vnext-template

## Context

- vNext PR: the AI Agent state Faz 1 PR of milestone v0.0.105 (branch `claude/vnext-ai-tool-concept-df6363`)
- Milestone: v0.0.105
- Issue: <https://github.com/burgan-tech/vnext/issues/1105>
- Runtime reference: `docs/domain/ai-agent-state.md`, `docs/runtime/mcp-task.md`, vnext-meta fragment
  `vnext-meta/unreleased/ai-agent-state.json` (features `aiAgentState`, `mcpTask`, `agentRunsFunction`, all experimental)

## What changed in the runtime

- New state type `AiAgent = 6`. The state carries an `agent` block: `agent` (reference to a `sys-agents`
  component), `prompt` (`template` or a C# `IAgentPromptMapping`), `output.path`, optional `memory`,
  `tools.exclude`, `policy` (narrowing only) and `decision` (`mode: model | script | rules`, required
  `fallback`, optional `onApprovalRequest`, `onExhausted`, `hints`, `hintsMapping`, `mapping`, `exclude`).
- Decision candidates are the state's automatic transitions **without a rule**, minus the reserved exits and
  `decision.exclude`. A rule-less automatic transition on an AiAgent state is valid (it is agent-decided).
- New optional transition field `description` (the "when" text shown to the model).
- New component type `sys-agents` (schema `sys_agents`): `provider` (profile name), `instructions`, `model`,
  inline `tools[]` (kind 1 task, 2 script, 3 mcp), `memory`, `output.schema`, `policy`, `audit`.
- New task type `Mcp = 24` (`config.transport: http`, `endpoint`, `auth.secretRef`, `headers{…: {secretRef}}`,
  `operation`, `tool | uri | prompt`, `arguments`, `timeoutSeconds` 1..120).
- New read/try surfaces: `GET …/instances/{id}/functions/agent-runs` (`?state`, `?skip`, `?take`, `?runId`) and
  `POST {domain}/definitions/agents/{key}/dry-run`.

## What this product must do

- Project skeleton: `Agents/` folder and `vnext.config.json` `paths.agents`.
- Example `triage-agent.json` (provider `local`), an Mcp task example (type 24) and an `.http` dry-run request.

- A **migration document** for domain repos generated from an older template (see below). Those repos
  keep their own copies of `build.js`, `validate.js`, `index.js`, `setup.js` and `test.js`, so a template
  release does not reach them.

## Migration for repos created from an older template

An existing domain repo still validates and builds after it adds `Agents/`, so the gap is silent. The
template must ship a migration document that lists these steps, and its release note must point to it:

| File | Older template | Change |
|---|---|---|
| `validate.js` | No `agents → 'agent'` entry in the folder-to-schema map, so files in `Agents/` are never schema-validated, without a warning | Take the new file: the map entry, the warning for a folder whose schema the installed package lacks, and the `addVocabularies` registration |
| `build.js` | The runtime build copies `Agents/` with the domain tree. The reference build's component-folder list has no `Agents`, and `exports.agents` resolves only when `paths.agents` is set | Take the new file |
| `index.js` | No `getAgents()`, and the component list in `getPathsConfig` defaults has no `agents` | Take the new file |
| `setup.js`, `test.js` | Folder lists without `Agents` | Take the new files |
| `vnext.config.json` | No `paths.agents` and no `exports.agents` | Add `"agents": "Agents"` under `paths` and `"agents": []` under `exports` |
| `package.json` | `@burgan-tech/vnext-schema` older than the AI Agent release | Raise it to the release that adds `stateType` 6, task type 24 and the `agent` schema. An older package rejects a workflow with an AiAgent state |

Observed on 2026-11-11 in vnext-example, which was generated from an older template: with `Agents/`
present and the scripts unchanged, `npm run validate` reported no agent files at all.

## Contract and examples

Copy from vnext-example `core/Agents/ai-agent-lab/ai-triage-agent.json`.

## Acceptance

- `npm run validate` passes on a fresh project (after the vnext-schema release).
- The migration document is published and linked from the template release note; following it on a repo
  generated from an older template makes `npm run validate` schema-validate `Agents/`.

## Out of scope

- Real provider profiles.
