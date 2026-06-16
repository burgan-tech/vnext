# `vnext-runtime` MCP Server

A standalone **net10.0** [Model Context Protocol](https://modelcontextprotocol.io) server that lets
MCP-capable agents (Claude Code, Cursor, CI, hosted assistants) discover and read a vNext project's
**components**, **live runtime data**, and **static `vnext-meta`** over a single endpoint.

Thin and decoupled: it calls the Orchestration HTTP API via a typed `HttpClient`. It has **no**
DB / Redis / Dapr / Application references.

## Transports

| Mode | Command | Use |
|------|---------|-----|
| stdio (default) | `dotnet run --project tools/BBT.Workflow.Mcp.Server` | Local IDE (Claude Code, Cursor) |
| Streamable HTTP | `dotnet run --project tools/BBT.Workflow.Mcp.Server -- --transport http` | Hosted / remote / CI (`POST /` MCP endpoint via `MapMcp`) |

## Install

Two packagings from one codebase:

**A. dotnet tool (stdio, local IDE)** — published to NuGet as `BBT.Workflow.Mcp`, command `vnext-mcp`:

```bash
dotnet tool install -g BBT.Workflow.Mcp
claude mcp add vnext-runtime --env Mcp__OrchestrationBaseUrl=http://localhost:4201 -- vnext-mcp
```

**B. Docker image (Streamable HTTP, hosted/CI)** — `ghcr.io/burgan-tech/vnext/mcp-server`:

```bash
docker run --rm -p 5000:5000 \
  -e Mcp__OrchestrationBaseUrl=http://host.docker.internal:4201 \
  ghcr.io/burgan-tech/vnext/mcp-server:<tag>
```

The container defaults to `--transport http` on port 5000 (`ASPNETCORE_URLS=http://+:5000`); TLS is
terminated upstream (ingress). Deploy via the `vnext-helm-charts` repo (`image.repository` =
`ghcr.io/burgan-tech/vnext/mcp-server`).

## Configuration (`Mcp` section / `Mcp__*` env vars)

| Key | Default | Purpose |
|-----|---------|---------|
| `OrchestrationBaseUrl` | `http://localhost:4201` | Orchestration API base URL |
| `AllowMutations` | `false` | Register mutating tools (start/transition/retry/publish/invalidate) |
| `AllowCodeRead` | `true` | Register `get_mapping_code` (returns executable `.csx`) |
| `DomainApiKeys` | `{}` (empty) | Per-domain client keys (`domain` → key). **Empty ⇒ auth disabled** (local dev) |
| `ClientApiKey` | `null` | Presented key under **stdio** (no HTTP header available) |
| `MetaPackageName` | `@burgan-tech/vnext-meta` | npm package holding the `vnext-meta` JSON |
| `MetaPackageVersion` | `0.0.61` | **Pinned** meta version (never `latest`); bump to upgrade |

### Client authorization

The MCP server is the auth boundary — each domain has a fixed key configured in `Mcp:DomainApiKeys`
(`{ "my-domain": "<secret>" }`). A client may only use tools for a domain when it presents that domain's
key; otherwise the tool returns `401` (wrong/missing key) or `403` (domain not configured). When
`DomainApiKeys` is empty, authorization is **disabled** (handy for local dev).

- **HTTP transport:** client sends `Authorization: Bearer <key>` (see `claude mcp add --header` below).
- **stdio transport:** the key comes from `Mcp__ClientApiKey` (no HTTP request to carry a header).

`tools/list`, MetaTools, and non-domain calls (e.g. `get_runtime_config`) are not domain-gated.
Outbound calls to Orchestration carry `User-Agent: vnext-mcp/<version>` (provenance) and **no** API key —
the runtime has no inbound auth of its own; authorization lives entirely at the MCP server.

`vnext-meta` is fetched once at startup from the public npm registry (`registry.npmjs.org`) and held
in memory — no filesystem dependency, identical under stdio and HTTP. If npm is unreachable the host
still starts (live tools work) and a background task retries the meta load.

## Tool groups

- **ComponentTools** — `list_components`, `list_workflows`/`list_tasks`/`list_functions`/`list_views`/
  `list_extensions`/`list_schemas`/`list_mappings`, `get_component`, `get_mapping_code` *(gated by `AllowCodeRead`)*.
  Wraps the Orchestration **Component Discovery API** (`GET /{domain}/components/*`).
- **RuntimeTools** — `list_instances`, `get_instance`, `get_instance_data`, `get_instance_state`,
  `get_instance_history`, `get_instance_hierarchy`, `get_runtime_config`. Wrap existing instance endpoints.
- **MetaTools** — `query_features`, `get_version_info`, `list_known_issues`, `get_deprecations`,
  `check_security_policy`, `list_meta_components`. Read the `vnext-meta` npm package (loaded at startup).
- **MutatingRuntimeTools** *(gated by `AllowMutations`)* — `start_instance`, `run_transition`,
  `retry_instance`, `publish_definitions`, `invalidate_cache`.

> Documentation discovery is intentionally **not** in this server — use the **Context7 MCP** server
> (`burgan-tech/vnext-runtime` / `vnext-docs`), which already indexes the public docs with semantic search.

Every tool takes `domain` as a parameter; one server instance serves all domains.

## Register with Claude Code (stdio)

```bash
claude mcp add vnext-runtime \
  --env Mcp__OrchestrationBaseUrl=http://localhost:4201 \
  --env Mcp__MetaPackageVersion=0.0.61 \
  -- dotnet run --project tools/BBT.Workflow.Mcp.Server
```

Or, after `dotnet tool install -g BBT.Workflow.Mcp`:

```bash
claude mcp add vnext-runtime --env Mcp__OrchestrationBaseUrl=http://localhost:4201 -- vnext-mcp
```

## Register with Claude Code (Streamable HTTP)

The HTTP transport exposes the MCP endpoint at the **root path** (`/`, via `MapMcp`), so the URL is
just the server's base address — no `/mcp` suffix.

1. Start the server in HTTP mode (Docker per [Install B](#install), or locally):

   ```bash
   ASPNETCORE_URLS=http://localhost:5000 \
     dotnet run --project tools/BBT.Workflow.Mcp.Server -- --transport http
   ```

   The port comes from `ASPNETCORE_URLS` (or `--urls`); there is no separate `--port` flag.

2. Register the remote server with Claude Code:

   ```bash
   claude mcp add --transport http vnext-runtime http://localhost:5000/
   ```

   Behind an authenticating ingress, pass a header (and use `--scope user` to share across projects):

   ```bash
   claude mcp add --transport http --scope user vnext-runtime https://vnext-mcp.example.com/ \
     --header "Authorization: Bearer <token>"
   ```

Alternatively, commit a project-scoped `.mcp.json` for the team:

```json
{
  "mcpServers": {
    "vnext-runtime": {
      "type": "http",
      "url": "http://localhost:5000/"
    }
  }
}
```

Verify with `claude mcp list` (expect **Connected**) or `/mcp` inside Claude Code.

> `Mcp__OrchestrationBaseUrl` is a **server-side** setting (how the MCP server reaches the Orchestration
> API). The URL you give Claude is the **MCP server** itself — don't conflate the two.

Tools then appear as `mcp__vnext-runtime__*`.
