---
product: vnext-forge
milestone: v0.0.105
status: pending
---

# AI Agent state, sys-agents component and Mcp task — Handoff for vnext-forge

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

- State palette: `AiAgent` (type 6) with an `agent` block editor: agent reference picker (`sys-agents`), prompt
  (template or script), `output.path`, memory override, `tools.exclude`, decision (mode, fallback, onExhausted,
  hints per candidate) with a live preview of the candidate set (rule-less automatic transitions minus reserved
  exits and `exclude`).
- Transition editor: optional `description` field.
- Agent component editor (`sys-agents`): provider profile name, instructions, model settings, inline tool list
  with a form per kind (task reference + mapping; dynamicExpresso or C# handler; MCP task + allow list + prefix),
  memory, output schema, policy, audit.
- Mcp task form (type 24): `secretRef` required for credentials, no plain-text header values.
- Dry-run button (`POST {domain}/definitions/agents/{key}/dry-run`) and an agent-runs viewer
  (`functions/agent-runs`, run → tool calls → decision).

- Phase 2 (stacked PR): team editor (`mode`, participants with name, agent picker, description); prompt seed
  messages and context map; `requiresApproval` toggle per tool; `onApprovalRequest` transition picker (any
  target state); `memory.compaction`; agent-runs viewer shows `participant`, `ApprovalPending`/`Denied` tool
  calls and `include=transcript`.

## Contract and examples

See the vnext-schema handoff `docs/integration/vnext-schema/ai-agent-state.md` for the JSON shapes.

## Acceptance

- vnext-example `ai-agent-lab` flows open, edit and save without losing `agent`, `description` or tool fields.

## Out of scope

- Running agents inside the designer beyond the dry-run endpoint.
