# AI Agent State (`stateType: 6`)

Issue #1105. Status: **experimental**, Faz 1. An `AiAgent` state runs an AI agent when the instance
enters it. The agent may call tools and returns structured output. Its decision picks one of the
state's automatic transitions. The instance stays Busy while the agent runs, exactly as it does for an
`onEntry` task.

Code is the source of truth; start at `RunAgentStep`, `AgentStateRunner` and `AutoConditionEvaluator`.

## Components

| Piece | Where | What it is |
|---|---|---|
| `sys-agents` component | `Definitions/Agents/Agent.cs` | Reusable agent: provider profile name, instructions, inline tools, memory, output schema, policy, audit |
| State `agent` block | `Definitions/Agents/AgentStateBinding.cs` | Which agent, the prompt, where the output lands, the decision |
| Transition `description` | `Transition.Description` | Optional "when" text for a candidate; never evaluated as a rule |
| Mcp task, type 24 | `Definitions/Tasks/McpTask.cs` | See [MCP Task](../runtime/mcp-task.md) |

### `sys-agents` component

```jsonc
{
  "key": "triage-agent", "flow": "sys-agents", "version": "1.0.0", "domain": "core",
  "attributes": {
    "provider": "bank-llm",                       // AiAgent:Providers:bank-llm — never an endpoint or a key
    "instructions": { "text": "You classify customer messages." },
    "model": { "temperature": 0, "maxOutputTokens": 1024 },
    "tools": [
      { "kind": 1, "name": "get_customer", "description": "Customer by id.",
        "parameters": { "type": "object", "properties": { "customerId": { "type": "string" } }, "required": ["customerId"] },
        "binding": { "task": { "key": "get-customer", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
                     "mapping": { "location": "./src/GetCustomerTool.csx", "code": "<base64>" } } },
      { "kind": 2, "name": "days_since", "description": "Days since an ISO date.",
        "parameters": { "type": "object", "properties": { "date": { "type": "string" } }, "required": ["date"] },
        "binding": { "code": { "location": "dynamicExpresso", "encoding": "NAT",
                               "code": "(now - DateTime.Parse(args.date)).TotalDays" } } },
      { "kind": 3, "binding": { "task": { "key": "crm-mcp", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
                                "allow": ["search_customer"], "prefix": "crm_" } }
    ],
    "memory": { "mode": "state", "storage": "local", "maxMessages": 40, "retentionDays": 30 },
    "output": { "schema": { "type": "object", "properties": { "category": { "type": "string" } }, "required": ["category"] } },
    "policy": { "maxIterations": 6, "maxToolCalls": 10, "maxTokens": 30000, "timeoutSeconds": 60,
                "retry": { "maxAttempts": 2, "backoffMs": 500, "on": ["429", "5xx", "timeout"] },
                "onProviderError": "fallback" },
    "audit": { "transcript": "none", "persistToolResults": true }
  }
}
```

| Tool `kind` | Runs | Notes |
|---|---|---|
| `1` task | A `sys-tasks` component through the task engine, on its own DI scope and a parallel branch of the script context | `SuppressDataApply`: a tool never writes instance data. The arguments become the branch body (merged); an optional `IMapping` reshapes them. Each call writes an `InstanceTasks` journal row whose `ExecutionKey` is the task engine's hash of `agent:{runId}:{tool}:{callId}` (not the literal text). A FanOut task cannot be a tool. A SubProcess task (`sync: true`) is how a tool starts a child flow |
| `2` script | `dynamicExpresso` expression with `args`, `data` (read-only latest data) and `now`; or a C# `IToolHandler` | Return value is serialized for the model |
| `3` mcp | An Mcp task: the runtime lists the server's tools (memoized `discoveryCacheMinutes`, default 10) and calls them through the task engine | Only names in `allow` are exposed, prefixed by `prefix`. Name collisions are skipped and logged (`30010`) |

Publish validation (`AgentComponentValidator`): provider and instructions required, `output.schema` and
every tool `parameters` must be `type: object`, tool names `^[a-zA-Z0-9_-]{1,64}$` and unique (MCP names
after the prefix), a binding per kind, script code decodable, policy positive and within the environment
ceilings, `requiresApproval: true` rejected (approval is not implemented yet). Task references are not
resolved at publish; an unresolvable task fails the tool call.

### State `agent` block

```jsonc
{
  "key": "classifying", "stateType": 6, "subType": 5,
  "agent": {
    "agent": { "key": "triage-agent", "domain": "core", "flow": "sys-agents", "version": "1.0.0" },
    "prompt": { "template": "<message>{{ data.message }}</message>" },   // or a C# IAgentPromptMapping (location + code)
    "output": { "path": "$.triage" },
    "memory": { "mode": "flow" },                // overrides the component
    "tools": { "exclude": ["days_since"] },
    "decision": {
      "mode": "model",                           // model | rules | script
      "fallback": "classify-failed",             // required
      "hints": { "is-fraud": "The customer reports fraud." },
      "exclude": [], "hintsMapping": null, "mapping": null, "onExhausted": null
    },
    "policy": { "timeoutSeconds": 30 }           // narrows only
  },
  "transitions": [
    { "key": "is-question", "target": "drafting", "triggerType": 1, "description": "The customer asks for information." },
    { "key": "is-fraud", "target": "escalated", "triggerType": 1 },
    { "key": "vip", "target": "priority", "triggerType": 1, "rule": { "location": "dynamicExpresso", "code": "..." } },
    { "key": "classify-failed", "target": "failed", "triggerType": 1 }
  ]
}
```

Template placeholders: `{{ data.* }}` (latest instance data), `{{ body.* }}` (transition payload),
`{{ instance.id }}`, `{{ instance.key }}`, `{{ state }}`. Strings render as-is, other values as JSON.

## Candidates and the decision

**Candidates** are the state's own automatic transitions **without a rule**, minus the reserved exits
(`fallback`, `onApprovalRequest`, `onExhausted`) and `decision.exclude` (`AgentCandidateSet`). An
automatic transition **with** a rule is never a candidate; it keeps its rule (mixed mode). Shared
transitions are not candidates: they never run as automatic transitions.

"When" text per candidate, first found wins: `hintsMapping` result → `decision.hints` → transition
`description` → first label → key. The model sees only `{ key, when }`, never the target.

| `mode` | Who decides | How |
|---|---|---|
| `model` (default) | The model | The output schema gains a required `decision` enum of candidate keys and a `reason` |
| `script` | `IAgentDecisionMapping` | Returns a candidate key |
| `rules` | The transitions' rules | Every candidate must carry a rule (validator); the agent only produces data |

The fallback is taken when the run fails, times out, exceeds a budget, returns a decision that is not a
candidate, or finds no free slot in the bulkhead. There is no silent default.
`policy.onProviderError: fault` instead fails the transition, so the error boundary decides.
`policy.maxEntries` + `decision.onExhausted` leave the state without a provider call once the state
was entered more often than allowed.

### How the decision reaches the transition

`RunAgentStep` (order 65) does not request the next transition. It leaves the run's decision on
`TransitionExecutionContext.AgentDecision`, and `RunAutomaticTransitionsStep` (80) picks the winner. On an AiAgent
state `AutoConditionEvaluator` evaluates every rule-less automatic transition from that in-hop decision:

| In-hop decision | Rule-less candidate | Reserved exit |
|---|---|---|
| None (updateData, resume at 79) | Not satisfied | Not satisfied |
| `model` / `script` | Satisfied only if selected | Satisfied only if selected |
| `rules`, completed run | (none exist) | Fallback satisfied, evaluated last |

Evaluation order on an AiAgent state: rule-bearing transitions and candidates in their usual order,
reserved exits last. A failed run whose decision is a reserved exit is taken alone — no rule is
evaluated against data the agent never produced. In rules mode the winner is written back to the
pending decision row in the same unit of work.

**Without an in-hop decision nothing the agent decides fires.** updateData does not run the agent
(the self-target profile excludes order 65), so its Auto step must not take the fallback. A
`DefaultAutoTransition` (kind 10) on an AiAgent state may only be the fallback, because it would
otherwise fire whenever no agent ran.

## Persistence and DB cost

| Table | One row per | Indexes |
|---|---|---|
| `InstanceAgentRuns` | state entry | UX `ExecutionKey` (`sha256(transitionId:state:entryNo)`); `(InstanceId, StartedAt DESC) INCLUDE (State, Status, AgentKey, TotalTokens)` |
| `InstanceAgentToolCalls` | tool call | `(RunId, Sequence)`, `(InstanceId)` |
| `InstanceAgentDecisions` | run | UX `RunId` |
| `AgentSessions` | memory scope | PK `ScopeKey`; partial `(ExpiresAt)`; `(OwnerInstanceId)` |

All four live in the flow schema and cascade with the instance. None is included by any hot read path
(instance GET/list, state function); the client reads the `$agent` summary in instance data.

Write path of one agent entry:

| Work | Database commands |
|---|---|
| Entry count (`CountRunsAsync`) | 1 index seek |
| Session read (memory on) | 1 primary-key lookup |
| Output + `$agent` summary | one instance-data append (the write funnel) |
| Session write (memory on, completed run) | 1 upsert statement |
| Run, tool-call and decision rows | added to the hop's unit of work; one batch at its next save or commit |
| Each kind-1/kind-3 tool call | the task engine's own journal row, as for any task |

No database connection is used while waiting on the provider. The rows are **not** atomic with the
instance-data append or Finalize: the hop's unit of work is not transactional
(`TransitionRunner`), Finalize uses `ExecuteUpdate` and instance data is written by its own funnel.
A crash after the provider call but before the commit loses the run rows; the instance stays Busy and
the stranded-Busy recovery applies.

### Sessions

`memory.mode`: `none` (no row), `state` (`i:{instanceId}:{flow}:{state}`), `flow`
(`i:{instanceId}:{flow}`), `key` (`k:{domain}:{agentKey}:{sha256(rendered memory.key)}`, shared across
instances). Local storage keeps the MAF-serialized session, trimmed to `maxMessages` (never starting on
a tool result); provider storage (`storage: provider` and a profile with `SupportsServiceSessions`)
keeps only the provider's conversation id.

The message list is gzip-compressed and AES-256-GCM encrypted with a fresh random key per write,
stored on the row; the scope key is the associated data, so ciphertext moved to another row does not
decrypt. Deleting the row — or its instance, which cascades — destroys the key with the content.
Writes are guarded by `xmin`: a writer that did not read the row, or read an older version, loses and
logs `30008` (only possible for `k:*`). A session is reset (logged `30007`) when the agent version
changes (`resetOn: agentVersionChange`, default) or the instructions change (`instructionsChange`).
Rows expire `retentionDays` after their last write; `AgentSessionSweeperHostedService` deletes them in
batches of 1000 every `AiAgent:SessionSweepIntervalMinutes` (0 disables it), and reads ignore expired rows.

## Read surface

- `GET …/instances/{instance}/functions/agent-runs` — runs newest first with the decision; `?state=`,
  `?skip=`, `?take=` (≤ 100). `?runId=` returns one run with its tool calls. Metadata only: prompts,
  outputs, tool arguments/results, transcripts and sessions are never served. One statement for the
  list (decision joined), two for a run. Errors `Instance:100054` (run not found, 404),
  `Instance:100055` (bad `runId`, 400). No role gate of its own, like `tasks`.
- `POST {domain}/definitions/agents/{key}/dry-run` — runs the agent once on sample data: no instance,
  no journal, no session, stub tools. Allowed for the roles in `AiAgent:DryRun:Roles` (empty disables).
- Instance data: `output.path` gets the structured output plus `$agent`: `runId`, `status`, `decision`,
  `decisionSource`, `reason`, `stopReason`, `iterations`, `attempts`, `toolCalls[{name, ok, durationMs}]`,
  `usage`, `session{scope, resumed}`. Every run rewrites every `$agent` field with a non-null value
  (`""`, `[]`, `session.scope: "none"`), because a `null` never clears a stored key. In `rules` mode
  `decision` is `""`: the winner is chosen after the write, by the Auto step, and lands on the decision
  row (`agent-runs`). A run that produced no output (failed, fallback, exhausted) leaves the output keys
  of an earlier run in place; `$agent.status` and `$agent.runId` say which run they belong to.

## Configuration (orchestration host)

```jsonc
"AiAgent": {
  "Providers": {
    "bank-llm": { "Kind": "openai-compatible", "Endpoint": "https://llm.internal/v1", "Model": "qwen3-32b",
                  "ApiKeySecretRef": "vault/workflow-secret/LLM_KEY", "StructuredOutput": "jsonSchema" },
    "local":    { "Kind": "local", "Model": "local" }   // deterministic, no network (tests, local runs)
  },
  "MaxRunSeconds": 120, "MaxIterations": 12, "MaxToolCalls": 20, "MaxTokens": 100000,
  "MaxConcurrentRuns": 32, "MaxSessionBytes": 262144, "MaxTranscriptBytes": 1048576,
  "SessionSweepIntervalMinutes": 60,
  "DryRun": { "Roles": [] }
}
```

`StructuredOutput: json` sends JSON mode with the schema in the instructions, for endpoints without
native JSON-schema output. The OpenAI SDK's own retries are off; `policy.retry` owns them. The `local`
provider calls every offered tool once with schema-derived arguments, then answers with an object built
from the schema (`const` → `default` → first `enum` → empty), so a decision enum resolves to its first
candidate.

`secretRef` forms: `store/bundle/key` reads the Dapr secret store (script secret cache); anything else
reads configuration (Vault is loaded at startup), `/` mapped to `:`.

## Placement and packages

The runtime (`modules/BBT.Workflow.Modules.Agents`: Microsoft Agent Framework 1.27, Microsoft.Extensions.AI
10.11, OpenAI 2.15, ModelContextProtocol.Core 2.3.1) is referenced **only** by the orchestration host
(`AddAgentsModule`). Hosts without it get `UnavailableAgentRuntime` and `UnavailableMcpClientGateway`:
every run takes its fallback and every MCP call fails with 503. Check with
`dotnet list execution/BBT.Workflow.Execution.HttpApi.Host package --include-transitive`.

## Observability

Spans: `Agent.Run/{state}` (source `BBT.Workflow.Agents.Run`), one `gen_ai` span per model call
(source `BBT.Workflow.Agents`, MEAI OpenTelemetry), task-engine spans per kind-1/kind-3 tool call. Both
sources are in the orchestration `AdditionalSources`. Logs `30001`–`30014` (`WorkflowLogs`, AI Agent region).

## Known limits (Faz 1)

- Tool approval (`requiresApproval`, `onApprovalRequest`) is not implemented; publish rejects it.
- `ScriptContext.Agent` (reading earlier runs from scripts) is not implemented; rules read `$agent`.
- Tool-call rows do not link to their `InstanceTasks` row (`InstanceTaskId` stays null); the journal key
  `agent:{runId}:{tool}:{callId}` correlates them.
- The prompt reads instance data as stored; `x-masking` is not applied to what the model sees.
- No `Idempotency-Key` header is added to HTTP tool calls yet (`IdempotencyKey` is recorded on the row).
- `vnext-schema` does not know `stateType: 6`, `agent`, `description` on transitions, task type 24 or
  `sys-agents` yet; domain CI validation fails until it does.
