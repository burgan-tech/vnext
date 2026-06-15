# VNext Init Service

Node.js service for downloading npm packages and publishing them to the vnext API.

## Features

- Downloads npm packages dynamically
- Processes component files (workflows, tasks, views, etc.)
- Merges with custom components if available
- **Automatic version generation** using extended SemVer format
- **Opt-in domain replacement** via `replaceDomain: true` + `appDomain` (standard publish)
- **Cross-domain exclusion** — components/references marked `crossDomain: true` are skipped during replacement
- Publishes to vnext API endpoint
- Provides REST API for package management
- **Two specialized endpoints** for different package types

## API Endpoints

### GET `/health`

Health check endpoint that verifies both the API server and vnext app are healthy.

**Response (200):**
```json
{
  "status": "healthy",
  "vnextApp": "healthy",
  "timestamp": "2024-01-01T00:00:00.000Z"
}
```

**Response (503):**
```json
{
  "status": "unhealthy",
  "vnextApp": "unreachable",
  "error": "Error message",
  "timestamp": "2024-01-01T00:00:00.000Z"
}
```

---

### POST `/api/package/runtime/publish`

**For Runtime Package Only** (`@burgan-tech/vnext-core-runtime`)

This endpoint is specifically designed for the core runtime package with special processing:
- `appDomain` is **REQUIRED**
- **Special ordering**: Workflows processed first, `sys-flows.json` loaded first
- **Full domain replacement** at all levels

**Request Body:**
```json
{
  "version": "latest",
  "appDomain": "my-domain",
  "npmRegistry": "https://registry.npmjs.org/",
  "npmToken": "optional-token",
  "npmUsername": "optional-username",
  "npmPassword": "optional-password",
  "npmEmail": "optional-email"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `appDomain` | ✅ **Yes** | Target domain for replacement (e.g., "core", "my-domain") |
| `version` | No | Package version (default: `latest`) |
| `npmRegistry` | No | NPM registry URL (default: `https://registry.npmjs.org/`) |
| `npmToken` | No | NPM token for token-based auth (npmjs.org compatible) |
| `npmUsername` | No | Username for TFS/Azure DevOps Artifacts auth |
| `npmPassword` | No | Password for TFS/Azure DevOps Artifacts auth (will be Base64 encoded) |
| `npmEmail` | No | Email for TFS Artifacts auth (default: `unused@dev.azure.com`) |

> **Note:** Use either `npmToken` OR `npmUsername`+`npmPassword`, not both. See [NPM Authentication](#npm-authentication) for details.

**Success Response (200):**
```json
{
  "success": true,
  "message": "Runtime package processed and published successfully",
  "packageName": "@burgan-tech/vnext-core-runtime",
  "appDomain": "my-domain",
  "results": {
    "successful": ["core/Workflows/sys-flows.json", ...],
    "failed": [],
    "skipped": []
  }
}
```

**Error Response (400) - Missing appDomain:**
```json
{
  "error": "appDomain is required for runtime package publish",
  "message": "Please provide appDomain parameter (e.g., \"core\", \"my-domain\")"
}
```

---

### POST `/api/package/publish`

**For Standard Packages** (any npm package)

This endpoint is for publishing any npm package with optional domain replacement.

**Request Body:**
```json
{
  "packageName": "@my-org/my-workflow-package",
  "version": "latest",
  "appDomain": "my-domain",
  "replaceDomain": true,
  "npmRegistry": "https://registry.npmjs.org/",
  "npmToken": "optional-token",
  "npmUsername": "optional-username",
  "npmPassword": "optional-password",
  "npmEmail": "optional-email"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `packageName` | ✅ **Yes** | The npm package name to download |
| `replaceDomain` | No | Set `true` to enable domain replacement. Requires `appDomain`. Default: `false` |
| `appDomain` | No | Target domain for replacement. Only used when `replaceDomain: true`. |
| `version` | No | Package version (default: `latest`) |
| `npmRegistry` | No | NPM registry URL |
| `npmToken` | No | NPM token for token-based auth (npmjs.org compatible) |
| `npmUsername` | No | Username for TFS/Azure DevOps Artifacts auth |
| `npmPassword` | No | Password for TFS/Azure DevOps Artifacts auth (will be Base64 encoded) |
| `npmEmail` | No | Email for TFS Artifacts auth (default: `unused@dev.azure.com`) |

> **Note:** Use either `npmToken` OR `npmUsername`+`npmPassword`, not both. See [NPM Authentication](#npm-authentication) for details.

**Success Response (200):**
```json
{
  "success": true,
  "message": "Package processed and published successfully",
  "packageName": "@my-org/my-workflow-package",
  "appDomain": "my-domain",
  "domainReplacement": true,
  "results": {
    "successful": ["core/Workflows/my-flow.json", ...],
    "failed": [],
    "skipped": []
  }
}
```

**Success Response (200) - Without Domain Replacement:**
```json
{
  "success": true,
  "message": "Package processed and published successfully",
  "packageName": "@my-org/my-workflow-package",
  "appDomain": null,
  "domainReplacement": false,
  "results": {
    "successful": ["core/Workflows/my-flow.json", ...],
    "failed": [],
    "skipped": []
  }
}
```

---

## Endpoint Comparison

| Feature | `/api/package/runtime/publish` | `/api/package/publish` |
|---------|-------------------------------|------------------------|
| Package | `@burgan-tech/vnext-core-runtime` only | Any npm package |
| `appDomain` | ✅ **Required** | ⭕ Only with `replaceDomain: true` |
| `replaceDomain` | N/A (always active) | Set `true` to enable replacement |
| Domain Replacement | Always (when appDomain provided) | Only if `replaceDomain: true` + `appDomain` |
| Cross-domain exclusion | ✅ Yes | ✅ Yes |
| Special Ordering | Yes (Workflows first, sys-flows first) | No |

---

## Domain Replacement

### Standard Package (`/api/package/publish`)

Domain replacement is **opt-in**. It is enabled only when **both** conditions are met:
- `replaceDomain: true` is set in the request body
- `appDomain` contains a non-empty target domain

When enabled, **all** domain fields are replaced at every level:

| Field | Replaced? |
|-------|-----------|
| Root `domain` | ✅ Yes (unless `crossDomain: true` on root) |
| `attributes.domain` | ✅ Yes (unless enclosing object has `crossDomain: true`) |
| `data[].domain` | ✅ Yes (unless `crossDomain: true` on the data item) |
| `data[].attributes.domain` | ✅ Yes (unless enclosing object has `crossDomain: true`) |
| Any nested `domain` field | ✅ Yes (unless enclosing object has `crossDomain: true`) |
| Any field inside `config` | ❌ Never (preserved as-is) |
| Any field inside `process` | ❌ Never (preserved as-is) |

### Runtime Package (`/api/package/runtime/publish`)

Domain replacement is **always active** when `appDomain` is provided (required). The same cross-domain exclusion rules apply.

### Cross-Domain Exclusion

Components or references that intentionally target a different domain can opt out of domain replacement by setting `crossDomain: true` on the relevant object.

**Root-level exclusion** — the entire component is skipped:
```json
{
  "key": "cross-domain-task",
  "domain": "payments",
  "crossDomain": true,
  "flow": "sys-tasks",
  "version": "1.0.0"
}
```

**Reference-level exclusion** — only that reference is skipped; the rest of the component is processed normally:
```json
{
  "key": "my-workflow",
  "domain": "account",
  "flow": "sys-flows",
  "attributes": {
    "steps": [
      {
        "task": {
          "key": "cross-domain-task",
          "domain": "payments",
          "crossDomain": true,
          "version": "1.0.0",
          "flow": "sys-tasks"
        }
      },
      {
        "task": {
          "key": "local-task",
          "domain": "account",
          "version": "1.0.0",
          "flow": "sys-tasks"
        }
      }
    ]
  }
}
```

After replacement with `appDomain: "my-domain"`:
- `my-workflow.domain` → `"my-domain"`
- `local-task.domain` → `"my-domain"`
- `cross-domain-task.domain` → `"payments"` (unchanged)

### Example

**Before (`replaceDomain: true`, `appDomain: "my-domain"`):**
```json
{
  "key": "my-flow",
  "domain": "core",
  "version": "1.0.0",
  "attributes": {
    "domain": "core",
    "type": "F"
  },
  "data": [
    {
      "key": "sub-flow",
      "domain": "core",
      "attributes": {
        "domain": "core"
      }
    }
  ]
}
```

**After:**
```json
{
  "key": "my-flow",
  "domain": "my-domain",
  "version": "1.0.0-pkg.1.0.0+core",
  "attributes": {
    "domain": "my-domain",
    "type": "F"
  },
  "data": [
    {
      "key": "sub-flow",
      "domain": "my-domain",
      "attributes": {
        "domain": "my-domain"
      }
    }
  ]
}
```

---

## Version Format

The service generates versions in the format: `MAJOR.MINOR.PATCH-pkg.PKG_VERSION+PKG_NAME`

### Components

| Part | Description | Source |
|------|-------------|--------|
| `MAJOR.MINOR.PATCH` | Artifact version | Component's `version` field |
| `-pkg.PKG_VERSION` | Package version | `vnext.config.json` → `version` |
| `+PKG_NAME` | Build metadata (package name) | `vnext.config.json` → `domain` |

### Examples

| Original Version | Package Version | Domain | Generated Version |
|-----------------|-----------------|--------|-------------------|
| `1.0.0` | `1.17.0` | `account` | `1.0.0-pkg.1.17.0+account` |
| `2.1.3` | `2.5.1` | `customer` | `2.1.3-pkg.2.5.1+customer` |
| `1.0.0-alpha.1` | `1.17.0` | `account` | `1.0.0-alpha.1-pkg.1.17.0+account` |

### Version Ordering

According to SemVer, package versions affect ordering:
- `1.0.0-pkg.1.18.0 > 1.0.0-pkg.1.17.0`
- `2.0.0-pkg.1.0.0 > 1.0.0-pkg.9.9.9`

The build metadata (`+PKG_NAME`) does **not** affect ordering.

---

## vnext.config.json

Each package must contain a `vnext.config.json` file with the following structure:

```json
{
  "version": "1.17.0",
  "domain": "account",
  "paths": {
    "componentsRoot": "core",
    "workflows": "Workflows",
    "tasks": "Tasks",
    "views": "Views",
    "functions": "Functions",
    "extensions": "Extensions",
    "schemas": "Schemas"
  }
}
```

---

## NPM Authentication

The service supports two authentication strategies for npm registries:

### Strategy 1: Token-based Authentication (`_authToken`)

For npmjs.org and registries that support token-based authentication:

```json
{
  "npmRegistry": "https://registry.npmjs.org/",
  "npmToken": "your-npm-token"
}
```

This generates an `.npmrc` file like:
```ini
registry=https://registry.npmjs.org/
//registry.npmjs.org/:_authToken=your-npm-token
```

### Strategy 2: TFS/Azure DevOps Artifacts Authentication (`_password`)

For Azure DevOps / TFS Artifacts npm feeds that require username/password authentication:

```json
{
  "npmRegistry": "https://pkgs.dev.azure.com/org/project/_packaging/feed/npm/registry/",
  "npmUsername": "my-user",
  "npmPassword": "my-password-or-pat",
  "npmEmail": "user@example.com"
}
```

This generates an `.npmrc` file like:
```ini
registry=https://pkgs.dev.azure.com/org/project/_packaging/feed/npm/registry/
//pkgs.dev.azure.com/org/project/_packaging/feed/npm/registry/:username=my-user
//pkgs.dev.azure.com/org/project/_packaging/feed/npm/registry/:_password=BASE64_ENCODED_PASSWORD
//pkgs.dev.azure.com/org/project/_packaging/feed/npm/registry/:email=user@example.com
always-auth=true
```

> **Note:** The password is automatically Base64 encoded by the service.

### Auth Strategy Decision

| Condition | Strategy Used |
|-----------|---------------|
| `npmToken` provided | Token-based (`_authToken`) |
| `npmUsername` + `npmPassword` provided | TFS Artifacts (`_password` + `username`) |
| Neither provided | No authentication (public registry) |

> ⚠️ **Important:** Do not provide both `npmToken` and `npmUsername`+`npmPassword` simultaneously. The service will prioritize `npmToken` if both are provided.

---

## Environment Variables

### Core Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `NPM_REGISTRY` | NPM registry URL | `https://registry.npmjs.org/` |
| `NPM_TOKEN` | NPM token for private registries | - |
| `VNEXT_APP_URL` | VNext app URL | `http://host.docker.internal:4201` |
| `PACKAGE_API_PORT` | Port for the API server | `3000` |

> **Note:** There is no default value for `appDomain`. It must be explicitly provided in the request when domain replacement is needed.

### Server Timeout Configuration

These environment variables control HTTP server timeouts. Increase these values for long-running pipelines with many components.

| Variable | Description | Default |
|----------|-------------|---------|
| `SERVER_TIMEOUT_MS` | Total request timeout in milliseconds | `600000` (10 min) |
| `SERVER_KEEP_ALIVE_TIMEOUT_MS` | Keep-alive connection timeout in milliseconds | `600000` (10 min) |
| `SERVER_HEADERS_TIMEOUT_MS` | Headers timeout in milliseconds (must be > keep-alive) | `610000` (10 min + 10 sec) |

#### Example: 30 Minute Timeout

For pipelines with many components that take longer to process:

```yaml
# docker-compose.yml
services:
  vnext-init:
    environment:
      SERVER_TIMEOUT_MS: 1800000        # 30 minutes
      SERVER_KEEP_ALIVE_TIMEOUT_MS: 1800000
      SERVER_HEADERS_TIMEOUT_MS: 1810000
```

Or via command line:

```bash
docker run -e SERVER_TIMEOUT_MS=1800000 \
           -e SERVER_KEEP_ALIVE_TIMEOUT_MS=1800000 \
           -e SERVER_HEADERS_TIMEOUT_MS=1810000 \
           your-image
```

> **Tip:** If you're experiencing timeout errors during package publishing, try increasing these values. The `SERVER_HEADERS_TIMEOUT_MS` should always be slightly larger than `SERVER_KEEP_ALIVE_TIMEOUT_MS`.

---

## Operation Mode

The service runs as an **API server only** - no automatic initialization. All package downloads must be initiated via API calls.

```bash
docker run -e PACKAGE_API_PORT=3000 your-image
```

The service will:
1. Start the HTTP API server
2. Wait for requests to download and process packages
3. Check vnext app health on each request

---

## Testing

Use the `test.http` file with VS Code REST Client extension or similar tools.

### Using VS Code REST Client

1. Install the [REST Client](https://marketplace.visualstudio.com/items?itemName=humao.rest-client) extension
2. Open `test.http` file
3. Adjust the variables at the top if needed
4. Click "Send Request" above each request

### Using cURL

**Runtime Package:**
```bash
curl -X POST http://localhost:3000/api/package/runtime/publish \
  -H "Content-Type: application/json" \
  -d '{
    "version": "latest",
    "appDomain": "my-domain"
  }'
```

**Standard Package:**
```bash
curl -X POST http://localhost:3000/api/package/publish \
  -H "Content-Type: application/json" \
  -d '{
    "packageName": "@my-org/my-workflow-package",
    "version": "latest",
    "appDomain": "my-domain"
  }'
```

**Standard Package (without domain replacement):**
```bash
curl -X POST http://localhost:3000/api/package/publish \
  -H "Content-Type: application/json" \
  -d '{
    "packageName": "@my-org/my-workflow-package",
    "version": "latest"
  }'
```

---

## File Processing

The service processes files based on `vnext.config.json` paths configuration:

### Component Directories

The following directories are scanned (as defined in `paths`):
- `tasks` (default: `core/Tasks`)
- `views` (default: `core/Views`)
- `functions` (default: `core/Functions`)
- `extensions` (default: `core/Extensions`)
- `workflows` (default: `core/Workflows`)
- `schemas` (default: `core/Schemas`)

### Processing Order

**For Runtime Package (`/api/package/runtime/publish`):**
1. **Workflows** processed first
   - `sys-flows.json` loaded first
   - Then other workflow files
2. **Other components** (tasks, views, functions, extensions, schemas)

**For Standard Package (`/api/package/publish`):**
- All component directories processed in default order
- No special priority

### Recursive Processing

All JSON files in these directories (including subdirectories) are processed:
1. Read and parse JSON file
2. Apply version transformation
3. Apply domain replacement (if `appDomain` provided)
4. Upload to vnext API

---

## Processing Pipeline

For each component file, the service follows this pipeline:

1. **Download Package** - Download npm package from registry
2. **Read vnext.config.json** - Get package version and domain
3. **Update Versions** - Generate new version format for all components
4. **Domain Replacement** - Replace all domains if `appDomain` provided
5. **Upload** - Publish to vnext API

### Version Update Rules

The following fields are updated with the new version format:
- Root level `version` field
- Root level `flowVersion` field (if present)
- Each item's `version` field in the `data[]` array

**Important:** The `version` field inside `attributes` is **never** modified!

---

## Docker Compose

The service is configured in `docker-compose.yml`:

```yaml
init:
  build:
    context: ../../init
    dockerfile: VNext.Init.Host/Dockerfile
  environment:
    - PACKAGE_API_PORT=3000
    - VNEXT_APP_URL=http://vnext-app:4201
  ports:
    - "3000:3000"
  healthcheck:
    test: ["CMD", "/app/healthcheck.sh"]
    interval: 10s
    timeout: 5s
    retries: 3
    start_period: 30s
```

The Docker healthcheck automatically verifies:
- API server is responding on `/health` endpoint
- VNext app is healthy and reachable
