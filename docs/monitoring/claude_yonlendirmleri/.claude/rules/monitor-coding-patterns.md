---
description: Monitoring API kodlama desenleri ve mimari kurallar
globs:
  - "vnext/monitoring/**/*.cs"
alwaysApply: false
---

# Monitor API — Coding Patterns & Architecture Rules

Bu kural dosyasi, `vnext/monitoring/` altindaki C# dosyalarinda calisirken gecerlidir (`globs` ile eslesen dosyalar).

`BBT.Workflow.Domain` / `BBT.Workflow.Infrastructure` icinde calisiyorsan: desen olarak vNext ile uyumlu kalmak ve **yalnizca yeni uyeler eklemek** zorunludur; mevcut metotlari degistirme. Tam kapsam: `.cursor/rules/monitor-constraints.md` ve kok `CLAUDE.md` §1.

## DI Module Composition Pattern

Service kayitlari `IServiceCollection` extension method'lari ile yapilir. Namespace her zaman `Microsoft.Extensions.DependencyInjection` olmalidir.

```csharp
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Monitor application-layer services.
/// </summary>
public static class MonitorApplicationModuleServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorApplicationModule(this IServiceCollection services)
    {
        services.AddAetherApplication();
        services.AddScoped<IMonitorFooService, MonitorFooService>();
        return services;
    }
}
```

Kurallar:
- `AddScoped` kullan (per-request lifetime).
- `IServiceCollection` don (fluent chaining).
- XML doc comments zorunlu.
- Yeni servis eklerken `MonitorApplicationModuleServiceCollectionExtensions.AddMonitorApplicationModule` icerisine ekle.

## Controller Pattern

```csharp
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorXController(
    IMonitorXService xService) : AetherControllerBase
{
    /// <summary>Action aciklamasi.</summary>
    /// <response code="200">Basarili</response>
    /// <response code="404">Bulunamadi</response>
    [HttpGet("{domain}/workflows/{workflow}/some-route")]
    [ProducesResponseType(typeof(XResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetXAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetXInput { Domain = domain, Workflow = workflow };
        var result = await xService.GetXAsync(input, cancellationToken);
        return FromResult(result);
    }
}
```

Kurallar:
- `AetherControllerBase`'den turet.
- `sealed class` + primary constructor.
- Route prefix: instance controller'lar `api/v{version:apiVersion}/monitor`, diger controller'lar `api/v{version:apiVersion}`.
- `[ServiceFilter(typeof(ResponseHeaderFilter))]` her controller'da.
- `FromResult(result)` ile `Result<T>` -> HTTP status donusumu.
- `[ProducesResponseType]` her action'da.
- XML `<summary>` ve `<response>` tag'leri her action'da.
- `CancellationToken` her async action'in son parametresi.

## Application Service Pattern

```csharp
public sealed class MonitorFooService(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository)
    : ApplicationService(serviceProvider), IMonitorFooService
{
    /// <inheritdoc />
    public async Task<Result<FooResponse>> GetFooAsync(
        MonitorGetFooInput input,
        CancellationToken cancellationToken = default)
    {
        var entity = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        if (entity is null)
            return Result<FooResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        return Result<FooResponse>.Ok(MapToResponse(entity));
    }

    private static FooResponse MapToResponse(Instance instance)
    {
        return new FooResponse { /* ... */ };
    }
}
```

Kurallar:
- `sealed class` + primary constructor.
- Ilk parametre `IServiceProvider serviceProvider`, sonra domain repository'ler.
- `ApplicationService(serviceProvider)` base class.
- Read-only query'ler: `AsReadOnly` / `FindByIdentifierAsReadOnlyAsync` kullan.
- Manuel mapping: AutoMapper yok, `private static MapToResponse(...)` metodu.
- `CancellationToken` her metotta.

## DTO Pattern

**Inputs** (`Monitor*Input`):
```csharp
public sealed class MonitorGetFooInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;
}
```

**Responses** (`Monitor*Response`):
```csharp
public sealed class MonitorFooResponse
{
    /// <summary>Property aciklamasi.</summary>
    public string? Name { get; set; }

    /// <summary>JSON payload.</summary>
    public JsonElement? Data { get; set; }
}
```

Kurallar:
- `sealed class` her zaman.
- Input'lar: `[Required]` attribute, `IHasDomain` implement et, `{ get; set; }` + default value.
- Response'lar: XML `<summary>` her property'de, `JsonElement?` dynamic veriler icin, `List<T>` (not `IReadOnlyList<T>`).
- Dosya isimlendirme: `Monitor{Feature}Inputs.cs`, `Monitor{Feature}Responses.cs`.
- Namespace: `BBT.Workflow.Monitor.{Feature}.DTOs`.

## Result Pattern

| Durum | Kod |
|-------|-----|
| Basari | `Result<T>.Ok(value)` |
| Bulunamadi | `Result<T>.Fail(Error.NotFound("code", "message"))` |
| Validasyon | `Result<T>.Fail(Error.Validation("code", "message"))` |
| Try-catch | `ResultExtensions.TryAsync(async ct => { ... }, cancellationToken)` |
| Controller donus | `FromResult(result)` |
| Liste + links | `result.ToActionResult(HttpContext)` |

## ConditionalResult Pattern (ETag / HTTP 304)

Orchestration'da `GetInstanceAsync`, `GetInstanceDataAsync`, `GetInstanceStateAsync` gibi endpoint'ler `ConditionalResult<T>` kullanir. Bu, ETag tabanlı conditional GET destegi saglar.

```csharp
// ConditionalResult<T> (Domain/ConditionalResult.cs):
// readonly record struct - Result<T> + IsNotModified flag
ConditionalResult<T>.Success(value)      // Normal basari
ConditionalResult<T>.NotModified()       // HTTP 304 - degismedi
ConditionalResult<T>.Fail(error)         // Hata
// Implicit conversion: Result<T> -> ConditionalResult<T>
```

Monitor'da su an ConditionalResult kullanilmiyor (standard Result<T> yeterli). Eger ETag destegi eklenecekse:
- Service metodu `Task<ConditionalResult<T>>` donmeli
- Input'a `string? IfNoneMatch` eklenmeli
- `IRepresentationEtagService` ile ETag olusturulmali
- Controller'da `IsNotModified` kontrolu ile 304 donulmeli

## Error Codes ve HTTP Status Mapping

vNext error code'lari `WorkflowErrorCodes` sinifinda tanimlidir (Domain). Monitor'da kullanilabilecek onemli kodlar:

| Error Code | HTTP Status | Aciklama |
|------------|-------------|----------|
| `Instance:100001` (NotFoundDomain) | 400 | Domain bulunamadi |
| `Instance:100005` (NotFoundWorkflow) | 404 | Workflow bulunamadi |
| `Instance:100007` (InstanceNotFound) | 404 | Instance bulunamadi (ozel kod) |
| `Transition:300001` (NotFoundTransition) | 404 | Transition bulunamadi |
| `Transition:300003` (InvalidState) | 400 | Gecersiz state |
| `Cache:600001` (CacheItemNotFound) | - | Cache'te bulunamadi |
| `Cache:600003` (CacheTypeNotSupported) | - | Desteklenmeyen cache tipi |
| `Function:800001` (FunctionNotInWorkflow) | - | Function workflow'da tanimli degil |

Monitor servislerde hata donmek icin `Error.NotFound("instance.notFound", ...)` gibi kisa kodlar kullanilabilir. Uzun formatlari `WorkflowErrorCodes` sabitleriyle eslestirmek gerekirse orchestration'daki `WorkflowExceptionHandlingExtensions` referans alinmali.

## Pagination — Standart Yapı

Monitor list endpoint'lerinde **her zaman** `MonitorPagedResponse<T>` kullanilir
(`BBT.Workflow.Monitor.Application/Common/DTOs/MonitorPagedResponse.cs`).

**Servis donusu (sayfalı liste):**
```csharp
// Service return type
Task<Result<MonitorPagedResponse<T>>> GetXListAsync(...)

// Service implementation
var items = pagedList.Items.Select(MapToResponse).ToList();
return new MonitorPagedResponse<T>
{
    Pagination = new MonitorPaginationInfo
    {
        Page     = pagedList.CurrentPage,
        PageSize = pagedList.PageSize,
        HasNext  = pagedList.HasNext
    },
    Items = items
};
```

**groupBy / aggregation sonucunda pagination yoktur:**
```csharp
return new MonitorPagedResponse<object>
{
    // Pagination set edilmez → JSON'da "pagination" field'ı görünmez
    Items = groups.Cast<object>().ToList()
};
```

**Controller (pagination mantığı service'te; controller sadece sonucu iletir):**
```csharp
var result = await xService.GetXListAsync(input, cancellationToken);
return result.ToActionResult(HttpContext);
```

> `totalCount` eklenmez. `IPaginationLinkGenerator` / `HateoasPagedList` / `IUrlTemplateBuilder` monitor list endpoint'lerinde kullanılmaz.

## Repository Kullanimi

| Interface | Entity | Sik Kullanilan Metodlar |
|-----------|--------|------------------------|
| `IInstanceRepository` | `Instance` | `FindByIdentifierAsReadOnlyAsync`, `GetPagedResultsWithGroupsAsync` |
| `IInstanceTransitionRepository` | `InstanceTransition` | `GetByInstanceIdAsReadOnlyAsync` |
| `IInstanceTaskRepository` | `InstanceTask` | `GetByTransitionIdAsync` |
| `IInstanceCorrelationRepository` | `InstanceCorrelation` | correlation sorulari |
| `IInstanceJobRepository` | `InstanceJob` | job sorulari |
| `IComponentCacheStore` | Definition cache | `GetFlowAsync`, `GetTaskAsync`, `GetSchemaAsync`, `GetViewAsync`, `GetFunctionAsync`, `GetExtensionAsync`, `GetAllExtensionsAsync` |

## JSON Serialization

vNext stack genelinde `JsonSerializerConstants.JsonOptions` kullanilir (Domain/Shared/JsonSerializerConstants.cs):
- CamelCase property ve dictionary key naming
- Case-insensitive okuma
- Null degerler yazilmaz (`JsonIgnoreCondition.WhenWritingNull`)
- `ReferenceHandler.IgnoreCycles`
- `MaxDepth = 256`
- `JsonStringEnumConverter` (camelCase enum serialization)
- `ExpandoObjectJsonConverter`

Monitor controller'larda MVC JSON options ayni `JsonSerializerConstants` uzerinden konfigure edilir (`AddWorkflowAspNetCore` icerisinde). Manuel `JsonSerializer` kullanirken:
```csharp
var json = JsonSerializer.Serialize(value, JsonSerializerConstants.JsonOptions);
```

## Multi-Schema Context

vNext multi-tenant PostgreSQL schema destegi kullanir. Her workflow farkli bir schema altinda calisir.

- Schema coozumleme: `X-Workflow` header, `workflow` query string veya route parametresi
- `ICurrentSchema` ile aktif schema set edilir
- Repository'ler `currentSchema.Name ?? "public"` kullanir
- Monitor pipeline `UseSchemaResolution()` icerdigi icin schema otomatik cozumlenir
- Controller route'larindaki `{workflow}` parametresi schema'yi belirler

Monitor servislerinde schema yonetimi **otomatiktir** — ekstra islem gerekmez. Ancak birden fazla domain/schema'ya cross-query yapmak icin `ICurrentSchema.Use(schemaName)` blogu kullanilmalidir.

## Logging Conventions

vNext **ASLA** dogrudan `logger.LogInformation()` kullanmaz. Tum loglar `WorkflowLogs` sinifindaki source-generated extension method'lar ile yapilir:

```csharp
// Domain/Logging/WorkflowLogs.cs
[LoggerMessage(EventId = 10001, Level = LogLevel.Information, Message = "Transition started for instance {InstanceId}")]
public static partial void TransitionStarted(this ILogger logger, Guid instanceId);
```

Monitor icin yeni log method'lari gerekirse:
1. Yeni event ID range'i sec (mevcut ID'ler 10000-10200 civarinda)
2. `WorkflowLogs.cs` icerisine partial method ekle (ANCAK bu dosya read-only!)
3. Alternatif: Monitor.Application icerisinde kendi `MonitorLogs` partial class'ini olustur

**Simdilik**: Monitor basit query servisleri icin loglama ihtiyaci minimum. Gerektiginde `ILogger<T>` inject edip `LogInformation` kullanilabilir ama tercih edilen yol structured logging extension method'laridir.

## Genel Kodlama Standartlari

- C# 10+, .NET 10 (`net10.0`).
- File-scoped namespace (`namespace X;`).
- Primary constructors (class seviyesinde DI).
- `sealed class` tercihi.
- `async/await` her yerde, `.Result` / `.Wait()` yasak.
- `CancellationToken` her public async metotta.
- XML doc comments zorunlu: class, interface, method, property.
- `/// <inheritdoc />` interface implementation'larda.
- Narration comment yazma ("// get the instance" gibi seyler yasak).

## Naming Conventions

| Element | Convention | Ornek |
|---------|-----------|-------|
| Class | PascalCase | `MonitorInstanceQueryService` |
| Interface | I + PascalCase | `IMonitorInstanceQueryService` |
| Method | PascalCase + Async | `GetInstanceAsync` |
| Parametre | camelCase | `cancellationToken` |
| Private field | camelCase | `instanceRepository` |
| Namespace | `BBT.Workflow.Monitor.*` | `BBT.Workflow.Monitor.Instances` |
| DI extension NS | `Microsoft.Extensions.DependencyInjection` | her zaman |
| DTO | `Monitor*Input` / `Monitor*Response` | `MonitorGetInstanceInput` |

## Middleware Pipeline Detay

Monitor host middleware sirasi (degistirilemez, UseMonitorApiModule icerisinde):

```
1.  UseAetherAmbientServiceProvider    — Aether scoped SP
2.  UseAppVersion                       — X-App-Version header
3.  UseExceptionHandler                 — Global exception → ProblemDetails
4.  UseAppResponseCompression           — Gzip/Brotli
5.  UseHttpsRedirection                 — HTTPS enforce
6.  UseRuntime                          — Server header (amorphie-runtime/...)
7.  UseCorrelationId                    — X-Correlation-Id propagation
8.  UseSecurityHeaders                  — HSTS, X-Content-Type-Options vb.
9.  UseCurrentUser                      — User context from headers
10. UseAetherApiVersioning              — API version routing
11. UseRouting                          — Endpoint routing
12. UseSchemaResolution                 — Multi-schema context (X-Workflow)
13. UseAetherUnitOfWork                 — UoW per request
14. UseWorkflowHttpMetrics              — Request duration/size/error metrics
15. UseHttpMetrics                      — Prometheus HTTP metrics
16. MapMetrics                          — /metrics endpoint
17. UseHttpBodyLogging                  — Request/response body log
18. MapControllers                      — MVC controller routing
19. MapAppHealthChecks                  — /health, /ready, /live, /version
```

Orchestration'da ek olarak bulunan ama Monitor'da OLMAYAN middleware:
- `UseCloudEvents` / `MapSubscribeHandler` (Dapr pub/sub)
- `UseDaprScheduledJobHandler` (Dapr scheduled jobs)
- `UseParentInstanceIdEnrichment` (parent instance telemetry)
- `EnsureDatabaseCreatedInDevelopment` (DB auto-create)
- `MigrateMessagingDbContext` (messaging DB migration)
