---
product: vnext-helm-charts
milestone: v0.0.105
status: pending
---

# AI Agent state configuration — Handoff for vnext-helm-charts

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

- `orchestrator.appEnvConfig`: `AiAgent__Providers__<name>__Kind` (`openai-compatible` | `local`), `__Endpoint`,
  `__Model`, `__ApiKeySecretRef`, `__StructuredOutput` (`jsonSchema` | `json`), `__SupportsServiceSessions`;
  `AiAgent__MaxRunSeconds`, `AiAgent__MaxConcurrentRuns`, ceilings (`MaxIterations`, `MaxToolCalls`, `MaxTokens`),
  `AiAgent__DryRun__Roles__N`, `AiAgent__MaxSessionBytes`, `AiAgent__SessionSweepIntervalMinutes`.
- `Mcp__AllowedHosts__N` (empty denies every MCP call).
- Only the orchestration deployment needs these; execution and workers do not load the agents module.
- `RESOURCE_TUNING.md`: an agent run holds a pipeline hop while the provider answers (bounded by
  `policy.timeoutSeconds`, capped by `AiAgent:MaxRunSeconds`, default 120 s); size orchestration request/thread budgets accordingly.

## Contract and examples

Defaults: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json` § `AiAgent`, `Mcp`.

## Acceptance

- A domain with one `openai-compatible` profile and an MCP host allow-list deploys and runs `ai-agent-lab` against it.

## Out of scope

- Secrets themselves: provider keys come from the secret store by `secretRef`.
