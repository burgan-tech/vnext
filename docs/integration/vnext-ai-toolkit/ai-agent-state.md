---
product: vnext-ai-toolkit
milestone: v0.0.105
status: pending
---

# AI Agent state authoring skills — Handoff for vnext-ai-toolkit

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

- New skill `component-agent`: inline tools per kind, decision mode, memory, output schema, policy.
- `component-task`: type 24 Mcp (secretRef-only credentials, host allow-list).
- `workflow-scaffold`: AiAgent state with rule-less automatic candidates carrying `description`, a fallback and an
  optional `onExhausted`.
- `reviewer`: model mode needs ≥ 2 candidates, fallback required and automatic, tool names unique;
  `security-reviewer`: no plain-text MCP headers, empty allow-list, egress.

- Phase 2 (stacked PR): `component-agent` covers `requiresApproval`, `memory.compaction`; `workflow-scaffold`
  covers team states (`mode` + `agents[]`) and an approval round trip (`onApprovalRequest` → any state →
  transition back with `{ "approval": { "approved": true } }`).

## Contract and examples

Examples: vnext-example `core/Workflows/ai-agent-lab/`.

## Acceptance

- The toolkit scaffolds a flow equivalent to `ai-agent-lab` that publishes and runs on the `local` provider.

## Out of scope

- Prompt engineering guidance.
