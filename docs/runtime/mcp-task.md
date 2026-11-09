# MCP Task (type 24)

Issue #1105. Status: **experimental**. Calls one operation of a remote MCP (Model Context Protocol)
server over streamable HTTP, **in-process on the orchestration host** — there is no Execution hop and
no remote binding. Used standalone (onEntry, onExit, functions) and as the binding of an agent's
`kind: 3` tool ([AI Agent State](../domain/ai-agent-state.md)).

```jsonc
{
  "key": "crm-mcp", "flow": "sys-tasks", "version": "1.0.0", "domain": "core",
  "attributes": {
    "type": "24",
    "config": {
      "transport": "http",
      "endpoint": "https://crm-mcp.internal/mcp",
      "auth": { "header": "Authorization", "scheme": "Bearer", "secretRef": "vault/workflow-secret/CRM_MCP_TOKEN" },
      "headers": { "X-Tenant": { "secretRef": "CRM_TENANT" } },
      "operation": "callTool",          // callTool | listTools | readResource | listResources | getPrompt | listPrompts
      "tool": "search_customer",        // callTool; readResource → "uri"; getPrompt → "prompt"
      "arguments": { "q": "x" },        // static; an input mapping replaces them (task.SetArguments)
      "timeoutSeconds": 20              // 1..120
    }
  }
}
```

| Rule | Where |
|---|---|
| `transport` must be `http` | publish + call |
| `endpoint` host must be in `Mcp:AllowedHosts` (`*` allows any; empty denies all) | publish + every call (403) |
| Credentials only through `secretRef`; a plain-text header value is a publish error | publish |
| `readResource` needs `uri`, `getPrompt` needs `prompt`; `callTool` without `tool` fails the call (400) | publish / call |

Result (`TaskInvocationResult.Data`): `callTool` → the tool's `structuredContent`, else `{ "text": … }`
of its text blocks; a tool that reports `isError` fails the task with 422; list operations → the
protocol objects as an array; `listTools` → `[{ name, description, inputSchema }]`. Timeout → 408,
transport failure → 502, unresolvable secret → 500, MCP not available on the host → 503.
Input and output follow the standard `IMapping` contract.

The transport (`McpClientGateway`, ModelContextProtocol.Core) lives in the agents module and is
registered only by the orchestration host. Each call opens a short-lived client and disposes it.
Logs: `30012` `McpCallFailed`.

Configuration: `"Mcp": { "AllowedHosts": ["crm-mcp.internal"] }`.
