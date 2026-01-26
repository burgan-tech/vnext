# vNext Platform

The vNext workflow runtime is a .NET-based orchestration system built with Clean Architecture
and DDD. It ships two API hosts:

- **Orchestration API**: client-facing workflow/instance operations
- **Execution API**: internal task execution and background processing

## Prerequisites

- .NET 10 SDK (10.0.101 or later)
- Docker (for container builds)

### First-Time Setup (.NET 10)

If you're building with .NET 10 for the first time, run the setup script once:

**macOS/Linux:**
```bash
./scripts/setup-netstandard-ref.sh
```

**Windows:**
```powershell
.\scripts\setup-netstandard-ref.ps1
```

This installs the `NETStandard.Library.Ref` targeting pack required by PostSharp. See [.NET 10 Setup Guide](docs/guides/net10-setup.md) for details.

## Quick Start

```bash
dotnet restore
dotnet build
```

Start with Docker (recommended):

```bash
cd etc/docker
./run-docker.sh
```

Local development (no Docker):

```bash
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host
```

## Repository Layout

- `orchestration/`: Orchestration API host
- `execution/`: Execution API host
- `workers/`: Inbox/Outbox workers
- `src/`: Domain, Application, Infrastructure, shared libs
- `etc/`: Docker, Dapr, and environment configs
- `docs/`: developer documentation

## Docs

- `docs/getting-started.md`
- `docs/architecture/overview.md`
- `docs/implementation/application-services.md`
- `docs/implementation/remote-routing-and-discovery.md`

## Health Endpoints

- Orchestration: `http://localhost:4201/health`
- Execution: `http://localhost:4202/health`
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




