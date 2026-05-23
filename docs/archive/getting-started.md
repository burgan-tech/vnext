# Getting Started

## Prerequisites

- **.NET SDK 9.0** (see `global.json`)
- **Docker Desktop** (recommended for local dependencies)
- **PostgreSQL** and **Redis** if running without Docker

Optional:

- Dapr CLI (for local Dapr components)
- Visual Studio 2022 or VS Code

## Clone and Build

```bash
git clone <repository-url>
cd vnext
dotnet restore
dotnet build
```

## Run with Docker (recommended)

From `etc/docker`:

```bash
./run-docker.sh
```

Modes:

- `./run-docker.sh` → infrastructure + Dapr
- `./run-docker.sh dev` → app containers with hot reload
- `./run-docker.sh stage` → stage compose

## Run Locally (without Docker)

You need PostgreSQL and Redis running locally and correct connection strings in:

- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`
- `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json`

Start both APIs:

```bash
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host
```

## Configuration Notes

Orchestration app settings include:

- `ConnectionStrings.Default`
- `Redis`
- `vNextApi` (remote routing + API version)
- `ServiceDiscovery` (optional, disabled by default)

Execution app settings include:

- `Redis`
- `OrchestrationApi.AppId` (Dapr service invocation)

Docker environment defaults live in:

- `etc/docker/env.default`
- `etc/docker/env.dev`
- `etc/docker/env.stage`

## Health Checks

When running locally:

- Orchestration: `http://localhost:4201/health`
- Execution: `http://localhost:4202/health`

If Swagger is enabled in the hosts, it will be under `/swagger`.


## Architecture Overview

With the new microservices architecture:

1. **Orchestration API** handles all client interactions
2. **Execution API** processes tasks internally
3. The Orchestration API communicates with the Execution API when task execution is needed
4. Both APIs share the same domain and infrastructure layers

```
[Client] → [Orchestration API] → [Execution API]
           ↓
       [Shared Domain & Infrastructure]
```

## Development Tips

### 1. Debugging Multiple APIs

When debugging, you can:
- Use different IDE instances for each API
- Use Docker Compose for easier management
- Check logs from both services

### 2. API Communication

- The Orchestration API should be your primary entry point
- The Execution API is for internal use only
- Monitor both APIs' health endpoints

### 3. Configuration Management

- Keep both APIs' configurations in sync for shared resources (database, Redis)
- Use different instance names for Redis to avoid conflicts
- Environment-specific configurations are maintained separately

## Troubleshooting

### Common Issues

1. **APIs won't start**: Check if ports 4201 and 4202 are available
2. **Database connection errors**: Verify PostgreSQL is running and connection strings are correct
3. **Redis connection errors**: Ensure Redis is running and accessible
4. **Internal API communication errors**: Verify the Execution API base URL configuration

### Log Locations

- **Orchestration API logs**: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Logs/`
- **Execution API logs**: `execution/BBT.Workflow.Execution.HttpApi.Host/Logs/`

For more detailed information, refer to the [Architecture Overview](architecture-overview.md) and other documentation files. 