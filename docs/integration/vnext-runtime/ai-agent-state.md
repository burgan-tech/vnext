---
product: vnext-runtime
milestone: v0.0.105
status: pending
---

# AI Agent state local defaults — Handoff for vnext-runtime

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

- Compose templates: orchestration env `AiAgent__Providers__local__Kind=local` (deterministic, no network) as the
  local default, and `Mcp__AllowedHosts__0` for the developer's MCP servers.
- `create-domain.sh`: create an `Agents/` folder.

## Contract and examples

Reference: this repo's `etc/docker/.env.orchestration.dev`.

## Acceptance

- A freshly created local domain runs an AiAgent state on the `local` provider without any secret.

## Out of scope

- Real provider credentials.
