# VNext Init Service

Node.js service for downloading npm packages and publishing them to the vnext API.

## Features

- Downloads npm packages dynamically
- Processes sys-* files (sys-flows.json, sys-tasks.json, etc.)
- Merges with custom components if available
- Applies domain replacement rules
- Publishes to vnext API endpoint
- Provides REST API for package management

## Usage

### Environment Variables

- `NPM_REGISTRY` - NPM registry URL (default: `https://registry.npmjs.org/`)
- `NPM_TOKEN` - NPM token for private registries (optional)
- `APP_DOMAIN` - Application domain for component initialization (default: `core`)
- `VNEXT_APP_URL` - VNext app URL (default: `http://host.docker.internal:4201`)
- `CUSTOM_COMPONENTS_PATH` - Path to custom components directory (default: `/app/custom-components`)
- `PACKAGE_API_PORT` - Port for the API server (default: `3000`)

### Operation Mode

The service runs as an **API server only** - no automatic initialization. All package downloads must be initiated via API calls.

```bash
docker run -e PACKAGE_API_PORT=3000 your-image
```

The service will:
1. Start the HTTP API server
2. Wait for requests to download and process packages
3. Check vnext app health on each request

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

### POST `/api/package/download`

Download and publish an npm package to the vnext API.

**Request Body:**
```json
{
  "packageName": "@burgan-tech/vnext-core-runtime",
  "version": "latest",
  "npmRegistry": "https://registry.npmjs.org/",
  "npmToken": "optional-token",
  "appDomain": "core",
  "customComponentsPath": "/app/custom-components"
}
```

**Required Fields:**
- `packageName` - The npm package name to download

**Optional Fields:**
- `version` - Package version (default: `latest`)
- `npmRegistry` - NPM registry URL (default: from `NPM_REGISTRY` env var)
- `npmToken` - NPM token for private registries
- `appDomain` - Application domain (default: from `APP_DOMAIN` env var or `core`)
- `customComponentsPath` - Custom components directory path

**Success Response (200):**
```json
{
  "success": true,
  "message": "Package processed and published successfully",
  "results": {
    "successful": ["sys-flows.json", "sys-tasks.json", ...],
    "failed": []
  }
}
```

**Error Response (400/500):**
```json
{
  "success": false,
  "error": "Error message"
}
```

## Testing

Use the `test.http` file with VS Code REST Client extension or similar tools.

### Using VS Code REST Client

1. Install the [REST Client](https://marketplace.visualstudio.com/items?itemName=humao.rest-client) extension
2. Open `test.http` file
3. Adjust the variables at the top if needed
4. Click "Send Request" above each request

### Using cURL

```bash
curl -X POST http://localhost:3000/api/package/download \
  -H "Content-Type: application/json" \
  -d '{
    "packageName": "@burgan-tech/vnext-core-runtime",
    "version": "latest",
    "appDomain": "core"
  }'
```

### Using Docker Compose

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

## File Processing Order

The service processes files in a specific order:

1. **sys-flows.json** (CRITICAL - processed first)
2. sys-tasks.json
3. sys-extensions.json
4. sys-functions.json
5. sys-views.json
6. sys-schemas.json
7. Additional workflow files (non-sys-* files)

## Domain Replacement

Domain replacement is applied when `appDomain` is different from `"core"`. The replacement follows specific rules:

- Only replaces domain if the object has `key`, `flow`, `version`, and `domain` fields
- Recursively processes all nested objects and arrays
- Preserves all other data structure

## Custom Components

Custom components can be merged with core components by placing JSON files in the custom components directory structure:

```
custom-components/
├── Workflows/
│   └── custom-workflow.json
├── Tasks/
│   └── custom-task.json
├── Extensions/
│   └── custom-extension.json
├── Functions/
│   └── custom-function.json
├── Views/
│   └── custom-view.json
└── Schemas/
    └── custom-schema.json
```

Each custom JSON file should have a `data` array containing the items to merge.

