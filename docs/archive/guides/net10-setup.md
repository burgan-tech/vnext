# .NET 10 Development Setup Guide

## Prerequisites

- .NET 10 SDK (10.0.101 or later)
- Docker (for container builds)

## PostSharp .NET 10 Compatibility

PostSharp requires `NETStandard.Library.Ref 2.1.0` targeting pack for .NET 10. This is automatically handled in Docker builds, but local development may require a one-time setup.

### Option 1: Automatic Setup (Recommended)

Run the setup script once after cloning the repository:

**Linux/macOS:**
```bash
chmod +x scripts/setup-netstandard-ref.sh
./scripts/setup-netstandard-ref.sh
```

**Windows (PowerShell):**
```powershell
.\scripts\setup-netstandard-ref.ps1
```

### Option 2: Manual Setup

If you prefer manual installation:

```bash
# Download the package
wget https://www.nuget.org/api/v2/package/NETStandard.Library.Ref/2.1.0 -O netstandard.nupkg

# Extract to NuGet cache
# macOS/Linux:
unzip netstandard.nupkg -d ~/.nuget/packages/netstandard.library.ref/2.1.0

# Windows:
# Expand-Archive netstandard.nupkg -DestinationPath $env:USERPROFILE\.nuget\packages\netstandard.library.ref\2.1.0
```

### Option 3: Skip PostSharp (Development Only)

If you're not working on aspect-oriented features, you can skip PostSharp during local builds:

```bash
dotnet build /p:PostSharpSkipPostCompilation=True
```

**⚠️ Warning:** This will disable all aspects (logging, caching, etc.) in your build.

## Verification

After setup, verify everything works:

```bash
dotnet restore
dotnet build
dotnet test
```

## Common Issues

### Issue: "Requested targeting pack NETStandard.Library.Ref, version=2.1.0 is not installed"

**Solution:** Run the setup script or manually install the targeting pack as described above.

### Issue: PostSharp warnings about .NET 10 support

**Status:** PostSharp 2026.0.5 shows warnings for .NET 10 but works correctly. These can be safely ignored.

## Docker Builds

Docker builds automatically install the required targeting pack. No additional setup needed:

```bash
docker build -f orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Dockerfile -t vnext-orchestration:latest .
```

## IDE Support

### Visual Studio 2022
- Install .NET 10 SDK
- Run setup script once
- Restart Visual Studio

### JetBrains Rider
- Install .NET 10 SDK
- Run setup script once
- Invalidate caches and restart if needed

### VS Code
- Install .NET 10 SDK
- Run setup script once
- Install C# Dev Kit extension
