---
product: vnext-schema
milestone: v0.0.105
status: pending
---

# AI Agent state, sys-agents component and Mcp task — Handoff for vnext-schema

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

- `workflow-definition`: add `6` to `stateType`; add the `agent` block, required when `stateType == 6`
  (turn the state's single `if/then` into `allOf` of `if/then`s); add optional `description` to every transition
  type, including the `additionalProperties: false` start/cancel/exit/updateData shapes and `sharedTransitions`.
- New `agent-definition.schema.json` (+ `index.js` / `index.d.ts`): the `sys-agents` attributes above; tool `kind`
  1/2/3 with `if/then` bindings (`binding.task`, `binding.code` as `ScriptCode`, `binding.allow` + `prefix`),
  tool `name` pattern `^[a-zA-Z0-9_-]{1,64}$`, `parameters` and `output.schema` of `type: object`.
- `task-definition`: add `"23"` (Python, already in the runtime) and `"24"` with the Mcp `config` branch; header
  values only as `{ "secretRef": "…" }` (plain strings are a publish error in the runtime).
- `core-header`: allow `flow: sys-agents`.
- `test/validate-*` examples for each.

## Contract and examples

```jsonc
{ "key": "classifying", "stateType": 6, "subType": 5,
  "agent": { "agent": { "key": "triage-agent", "domain": "core", "flow": "sys-agents", "version": "1.0.0" },
             "prompt": { "template": "<message>{{ data.message }}</message>" },
             "output": { "path": "$.triage" },
             "decision": { "mode": "model", "fallback": "classify-failed" } },
  "transitions": [ { "key": "is-question", "target": "drafting", "triggerType": 1, "description": "The customer asks." } ] }
```
Full examples: vnext-example `core/Workflows/ai-agent-lab/*.json`, `core/Agents/ai-agent-lab/ai-triage-agent.json`.

## Acceptance

- vnext-example `ai-agent-lab` flows and the agent pass `npm run validate` against the released schema.

## Out of scope

- Semantic checks the runtime validator owns (candidate count, hints keys, reserved exits being automatic).
