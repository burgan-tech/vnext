# Workflow Usage Documentation

## Introduction
This document explains the structure and usage of the .NET Core template project created using the BBT.Aether SDK. This template includes all the necessary configurations and structure for developers to get started quickly.

## Folder Structure

The project folder structure is as follows:

```
.vscode
etc
  └── dapr
      └── components
          └── config.yaml
          └── pubsub.yaml
          └── secretstore.yaml
          └── state.yaml
      └── config.yaml
  └── docker
      └── config
          └── otel
          └── prometheus
          └── setup-service
          └── vault
          └── setup.sh
      └── .env.execution.dev
      └── .env.execution.stage
      └── .env.orchestration.dev
      └── .env.orchestration.stage
      └── docker-compose.yml
      └── docker-compose.dev.yml
      └── docker-compose.state.yml
      └── run-docker.sh
src
  └── BBT.Workflow.Application
  └── BBT.Workflow.Domain
  └── BBT.Workflow.Infrastructure
  └── BBT.Workflow.HttpApi.Host (LEGACY)
  └── BBT.Workflow.HttpApi.Shared
orchestration
  └── BBT.Workflow.Orchestration.HttpApi.Host
execution
  └── BBT.Workflow.Execution.HttpApi.Host
test
  └── BBT.Workflow.Application.Tests
  └── BBT.Workflow.Domain.Tests
  └── BBT.Workflow.Infrastructure.Tests
  └── BBT.Workflow.TestBase
 
 api-tests
 examples
.gitattributes
.gitignore
.prettierrc
BBT.Workflow.sln
BBT.Workflow.sln.DotSettings
common.props
delete-bin-obj.ps1
global.json
NuGet.Config
```

### .vscode
Contains configuration files for Visual Studio Code.

### etc
Contains configuration files.
- **dapr**: Contains Dapr components and configuration files.
  - `components`: Dapr component configurations.
  - `config.yaml`: Dapr general configuration file.
- **docker**: Contains Docker configuration files.
  - `config`: Docker configurations.
  - `docker-compose.yml`: Docker Compose configuration file.
  - `docker-compose.dev.yml`: Docker Compose DEV configuration file.
  - `docker-compose.stage.yml`: Docker Compose STAGE configuration file.

### src
Contains the application source code.
- **BBT.Workflow.Application**: Application layer with modular organization including Orchestration, Execution, SubFlow, Extensions, and Persistence modules.
- **BBT.Workflow.Domain**: Domain layer.
- **BBT.Workflow.Infrastructure**: Infrastructure configurations (Data, AutoMapper, Cache etc.).
- **BBT.Workflow.HttpApi.Host**: (LEGACY) HTTP API Host - Legacy implementation.
- **BBT.Workflow.HttpApi.Shared**: Shared components and configurations for both API hosts.

### orchestration
Contains the Orchestration API project.
- **BBT.Workflow.Orchestration.HttpApi.Host**: Primary API for external clients. Handles workflow and instance management operations. This is the main API that clients should interact with.

### execution
Contains the Execution API project.
- **BBT.Workflow.Execution.HttpApi.Host**: Internal API for task execution. Only consumed by the Orchestration API. Handles task processing and execution logic.

### test
Contains the test projects.
- **BBT.Workflow.Application.Tests**: Tests for the Application layer.
- **BBT.Workflow.Domain.Tests**: Tests for the Domain layer.
- **BBT.Workflow.Infrastructure.Tests**: Tests for the Infrastructure layer.
- **BBT.Workflow.TestBase**: Base classes and utilities for tests.

### Other Files
- **.gitattributes**: Specifies Git attributes.
- **.gitignore**: Specifies files to be ignored by Git.
- **.prettierrc**: Prettier configuration file.
- **BBT.Workflow.sln**: Visual Studio solution file.
- **BBT.Workflow.sln.DotSettings**: Visual Studio settings file.
- **common.props**: Contains common project properties.
- **delete-bin-obj.ps1**: PowerShell script to clean `bin` and `obj` folders.
- **global.json**: Specifies the .NET SDK version.
- **NuGet.Config**: Contains NuGet sources and settings.

---

## Architecture Overview

The system is now built with a **microservices architecture** using two separate API projects:

### 1. Orchestration API (`BBT.Workflow.Orchestration.HttpApi.Host`)
- **Purpose**: Primary API for external clients
- **Responsibilities**:
  - Workflow management and definition
  - Instance lifecycle management
  - Client-facing operations
  - API gateway functionality
- **Access**: Public API consumed by external clients
- **Port**: Default 4201 (HTTP), 7189 (HTTPS)

### 2. Execution API (`BBT.Workflow.Execution.HttpApi.Host`)
- **Purpose**: Internal API for task execution
- **Responsibilities**:
  - Task processing and execution
  - Task-specific operations
  - Background job processing
- **Access**: Internal API, only consumed by Orchestration API
- **Port**: Default 4202 (HTTP), 7190 (HTTPS)

## Required Tools and Setup Steps

### Prerequisites
- [.NET SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/get-started)
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/)

### Setup Steps
1. Clone the repository:
    ```sh
    git clone https://github.com/your-repo/Workflow.git
    cd Workflow
    ```

2. Install the required .NET SDK version:
    ```sh
    dotnet --version
    ```

3. Restore the dependencies:
    ```sh
    dotnet restore
    ```

4. Build the project:
    ```sh
    dotnet build
    ```

5. Run the projects:
    ```sh
    ./run-docker.sh
    ```

## Running the Application

### Development Mode
Both APIs will start simultaneously when using Docker Compose:

```bash
cd etc/docker

# Run both APIs in development mode
./run-docker.sh 

# Run both APIs in debugging mode (with debugger and hot reload)
./run-docker.sh dev

# Run both APIs in stage mode (no debugger)
./run-docker.sh stage
```

### Individual API Startup
You can also run each API individually:

```bash
# Run Orchestration API
cd orchestration/BBT.Workflow.Orchestration.HttpApi.Host
dotnet run

# Run Execution API (in a separate terminal)
cd execution/BBT.Workflow.Execution.HttpApi.Host
dotnet run
```

## API Endpoints

### Orchestration API
- **Base URL**: `http://localhost:4201`
- **Swagger**: `http://localhost:4201/swagger`
- **Health Check**: `http://localhost:4201/health`

### Execution API
- **Base URL**: `http://localhost:4202`
- **Swagger**: `http://localhost:4202/swagger`
- **Health Check**: `http://localhost:4202/health`

## Docker Debugging Setup

The project includes a Docker setup that supports both development (with debugging) and production environments.

### Docker Configuration

- **Dockerfile**: Contains multi-stage build with conditional debugging support
- **docker-compose.yml**: Development configuration with debugging enabled for both APIs

### Running with Docker

Navigate to the docker directory and use the helper script:

```bash
cd etc/docker

# Run in development mode
./run-docker.sh 

# Run in debugging mode (with debugger and hot reload)
./run-docker.sh dev

# Run in stage mode (no debugger)
./run-docker.sh stage
```

### Debugging with VS Code

1. Start the application in development mode:
   ```bash
   cd etc/docker
   ./run-docker.sh
   ```

2. Once the application is running:
   - Open VS Code's "Run and Debug" view (Ctrl+Shift+D or Cmd+Shift+D)
   - Select the "Attach to Docker" configuration
   - Choose which API to debug (Orchestration or Execution)
   - Press F5 to start debugging

3. VS Code will connect to the running container and you can set breakpoints and debug as normal

### Conditional Debugging

The Dockerfile is configured to:
- Only install debugging tools when `ENVIRONMENT=dev`
- Set `ASPNETCORE_ENVIRONMENT=Development` only in development mode
- Use a non-root user for better security

This approach ensures that production images remain small and secure while development images include all necessary debugging tools.

#### Mac
To run the script on MacOS, you need to install PowerShell. You can find the official documentation for installing PowerShell on MacOS [here](https://docs.microsoft.com/en-us/powershell/scripting/install/installing-powershell-core-on-macos).

---

## Troubleshooting

### PostSharp Targeting Pack Error

If you encounter the following error during compilation:

```
POSTSHARP : error : error: Unhandled exception (PostSharp.Compiler.Hosting.CommandLine.dll 2025.1.10 release | .NET 9.0.11 (Arm64)): Requested targeting pack NETStandard.Library.Ref, version=2.1.0 is not installed in
```

**Solution:**

1. First, clean the `bin` and `obj` folders. You can use one of the following methods:

   **Option A - Using shell command (Linux/macOS):**
   ```bash
   find . -type d \( -name bin -o -name obj \) -exec rm -rf {} + 2>/dev/null
   ```

   **Option B - Using PowerShell script (Windows/macOS with PowerShell):**
   ```powershell
   ./delete-bin-obj.ps1
   ```

2. Then, rebuild the project:
   ```bash
   dotnet clean
   dotnet restore
   dotnet build
   ```

This issue typically occurs when there are stale build artifacts that conflict with PostSharp's targeting pack resolution.

---

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.




