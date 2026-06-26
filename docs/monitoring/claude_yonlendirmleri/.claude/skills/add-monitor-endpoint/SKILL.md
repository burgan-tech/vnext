# Skill: Add Monitor Endpoint

Use this skill when implementing a new monitoring API endpoint. This provides a step-by-step guide with the exact file locations, naming conventions, and code patterns.

## When to Use

- Adding a new GET endpoint to mirror an orchestration read endpoint
- Creating a new monitor-only endpoint for dashboard features
- Extending an existing controller with a new action

## Prerequisites

**Constraints:** `.cursor/rules/monitor-constraints.md` (always applied) and root `CLAUDE.md` §1. Monitoring kodu `vnext/monitoring/` altinda; Domain/Infra (`vnext/src/BBT.Workflow.Domain`, `...Infrastructure`) ancak **ekleme-only** ile dokunulabilir. `BBT.Workflow.Application` ve diger vNext kodu **tuketilir**, degistirilmez. Yeni ihtiyacta once **Aether SDK**, sonra **`vnext/src`** icinde mevcut yardimci var mi bak.

Before starting, identify:
1. Which orchestration endpoint you are mirroring (check `.cursor/rules/monitor-endpoint-map.md`)
2. The orchestration source files to reference (controller + app service + DTOs)
3. Which domain repositories or cache stores you need (mevcut read-only repository metotlarini tercih et; yoksa Domain/Infra'ya **yeni** metot ekle — mevcut imzayi degistirme)

## Step-by-Step Guide

### Step 1: Create DTOs

**Location**: `vnext/monitoring/BBT.Workflow.Monitor.Application/{Feature}/DTOs/`

Create two files:

**`Monitor{Feature}Inputs.cs`**:
```csharp
using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.{Feature}.DTOs;

/// <summary>
/// Input for retrieving {description}.
/// </summary>
public sealed class MonitorGet{Feature}Input : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    // Add feature-specific properties...
}
```

**`Monitor{Feature}Responses.cs`**:
```csharp
namespace BBT.Workflow.Monitor.{Feature}.DTOs;

/// <summary>
/// Response for {description}.
/// </summary>
public sealed class Monitor{Feature}Response
{
    /// <summary>Property description.</summary>
    public string? PropertyName { get; set; }
}
```

### Step 2: Create Service Interface

**Location**: `vnext/monitoring/BBT.Workflow.Monitor.Application/{Feature}/IMonitor{Feature}Service.cs`

```csharp
using BBT.Aether.Results;
using BBT.Workflow.Monitor.{Feature}.DTOs;

namespace BBT.Workflow.Monitor.{Feature};

/// <summary>
/// Read-only query service for {description}.
/// </summary>
public interface IMonitor{Feature}Service
{
    /// <summary>
    /// {Method description}.
    /// </summary>
    Task<Result<Monitor{Feature}Response>> Get{Feature}Async(
        MonitorGet{Feature}Input input,
        CancellationToken cancellationToken = default);
}
```

### Step 3: Create Service Implementation

**Location**: `vnext/monitoring/BBT.Workflow.Monitor.Application/{Feature}/Monitor{Feature}Service.cs`

```csharp
using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.{Feature}.DTOs;

namespace BBT.Workflow.Monitor.{Feature};

/// <summary>
/// Read-only query service implementation for {description}.
/// </summary>
public sealed class Monitor{Feature}Service(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository)
    : ApplicationService(serviceProvider), IMonitor{Feature}Service
{
    /// <inheritdoc />
    public async Task<Result<Monitor{Feature}Response>> Get{Feature}Async(
        MonitorGet{Feature}Input input,
        CancellationToken cancellationToken = default)
    {
        // 1. Query domain repository (read-only)
        var entity = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        // 2. Handle not-found
        if (entity is null)
            return Result<Monitor{Feature}Response>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        // 3. Map and return
        return Result<Monitor{Feature}Response>.Ok(MapToResponse(entity));
    }

    private static Monitor{Feature}Response MapToResponse(Instance instance)
    {
        return new Monitor{Feature}Response
        {
            // Manual mapping - no AutoMapper
        };
    }
}
```

### Step 4: Register in DI

**File**: `vnext/monitoring/BBT.Workflow.Monitor.Application/Microsoft/Extensions/DependencyInjection/MonitorApplicationModuleServiceCollectionExtensions.cs`

Add inside `AddMonitorApplicationModule`:
```csharp
services.AddScoped<IMonitor{Feature}Service, Monitor{Feature}Service>();
```

### Step 5: Add Controller Action

**Location**: `vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host/Controllers/Monitor{Feature}Controller.cs`

If the feature belongs to an existing controller (like instance queries go to `MonitorInstanceController`), add the action there. Otherwise create a new controller:

```csharp
using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Monitor.{Feature};
using BBT.Workflow.Monitor.{Feature}.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>
/// Read-only monitoring endpoints for {description}.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class Monitor{Feature}Controller(
    IMonitor{Feature}Service featureService) : AetherControllerBase
{
    /// <summary>
    /// {Action description}.
    /// </summary>
    /// <response code="200">{Success description}</response>
    /// <response code="404">{Not found description}</response>
    [HttpGet("{domain}/workflows/{workflow}/{route}")]
    [ProducesResponseType(typeof(Monitor{Feature}Response), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get{Feature}Async(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGet{Feature}Input
        {
            Domain = domain,
            Workflow = workflow
        };

        var result = await featureService.Get{Feature}Async(input, cancellationToken);
        return FromResult(result);
    }
}
```

### Step 6: Verify

1. Projenin build oldugunu dogrula: `dotnet build vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host`
2. Kullanicidan test etmesini iste

## Checklist

- [ ] DTOs olusturuldu (Input + Response)
- [ ] Service interface olusturuldu
- [ ] Service implementation olusturuldu
- [ ] DI'a kaydedildi
- [ ] Controller action eklendi
- [ ] XML doc comments eklendi
- [ ] `sealed class` kullanildi
- [ ] `CancellationToken` her metotta var
- [ ] Read-only repository metodlari kullanildi
- [ ] Build basarili

## Reference Files

Mevcut implementasyonu ornek almak icin:

| Ornek | Dosya |
|-------|-------|
| Controller | `vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host/Controllers/MonitorInstanceController.cs` |
| Service interface | `vnext/monitoring/BBT.Workflow.Monitor.Application/Instances/IMonitorInstanceQueryService.cs` |
| Service impl | `vnext/monitoring/BBT.Workflow.Monitor.Application/Instances/MonitorInstanceQueryService.cs` |
| Input DTOs | `vnext/monitoring/BBT.Workflow.Monitor.Application/Instances/DTOs/MonitorInstanceInputs.cs` |
| Response DTOs | `vnext/monitoring/BBT.Workflow.Monitor.Application/Instances/DTOs/MonitorInstanceResponses.cs` |
| DI registration | `vnext/monitoring/BBT.Workflow.Monitor.Application/Microsoft/Extensions/DependencyInjection/MonitorApplicationModuleServiceCollectionExtensions.cs` |
