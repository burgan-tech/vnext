---
product: vnext-docs
milestone: v0.0.105
status: pending
---

# AI Agent state, sys-agents component and Mcp task — Handoff for vnext-docs

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

- New pages: AiAgent state (lifecycle, Busy while running, decision and candidates, reserved exits), agent
  component (tools, memory and session resume, output, policy), Mcp task, `agent-runs` and `dry-run` functions.
- Client consumption: the client loop does not change (the instance is Busy while the agent runs; poll the
  state); the `$agent` summary in instance data is the client-facing result.
- Configuration and operations: provider profiles, MCP host allow-list, cost/latency ceilings.

- Phase 2 (stacked PR): pages for tool approval (the `approval` body on re-entry, any target state), agent
  teams (sequential, concurrent, handoff), prompt seed messages and data context, transcripts on the
  agent-runs function; sessions are stored unencrypted.

## Contract and examples

Source pages in this repo: `docs/domain/ai-agent-state.md`, `docs/runtime/mcp-task.md`.

## Acceptance

- A reader can author the vnext-example `ai-agent-lab` flows from the docs alone.

## Out of scope

- Provider-specific model tuning guidance.
