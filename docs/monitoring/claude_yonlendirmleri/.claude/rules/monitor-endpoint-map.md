---
description: Orchestration vs Monitor API endpoint haritasi ve mevcut durum
globs:
  - "vnext/monitoring/**/Controllers/**/*.cs"
alwaysApply: false
---

# Orchestration vs Monitor — Endpoint Map

Bu dosya, Orchestration API'nin tum endpoint'lerini ve Monitor API'deki durumunu icerir.
Yeni bir monitor endpoint gelistirirken bu haritayi referans al.

## Instance Endpoints (Orchestration: InstanceController)

| # | HTTP | Orchestration Route | Monitor Durumu | Monitor Route |
|---|------|-------------------|----------------|---------------|
| 1 | POST | `{domain}/workflows/{workflow}/instances/start` | UYGULANMAZ (write) | — |
| 2 | POST | `{domain}/workflows/{workflow}/sub/instances/start` | UYGULANMAZ (write) | — |
| 3 | POST | `{domain}/workflows/{workflow}/instances/{instance}/complete` | UYGULANMAZ (write) | — |
| 4 | POST | `{domain}/workflows/{workflow}/instances/{instance}/sub/state` | UYGULANMAZ (write) | — |
| 5 | PATCH | `{domain}/workflows/{workflow}/instances/{instance}/transitions/{transitionKey}` | UYGULANMAZ (write) | — |
| 6 | POST | `{domain}/workflows/{workflow}/instances/{instance}/retry` | UYGULANMAZ (write) | — |
| 7 | GET | `{domain}/workflows/{workflow}/instances/{instance}` | TAMAMLANDI | `monitor/{domain}/workflows/{workflow}/instances/{instance}` |
| 8 | GET | `{domain}/workflows/{workflow}/instances` | TAMAMLANDI | `monitor/{domain}/workflows/{workflow}/instances` |
| 9 | GET | `{domain}/workflows/{workflow}/instances/{instance}/transitions` | TAMAMLANDI (/timeline) | `monitor/{domain}/workflows/{workflow}/instances/{instance}/timeline` (params: `transitionId`, `taskId`, `includeTasks`) |
| 10 | GET | `{domain}/workflows/{workflow}/instances/{instance}/data` | TAMAMLANDI | `monitor/{domain}/workflows/{workflow}/instances/{instance}/data` |

## Function Endpoints (Orchestration: FunctionController)

| # | HTTP | Orchestration Route | Monitor Durumu | Notlar |
|---|------|-------------------|----------------|--------|
| 11 | GET | `{domain}/functions` | YAPILACAK | Domain icin tum function'lari listele |
| 12 | GET | `{domain}/functions/{function}` | YAPILACAK | Key ile function al |
| 13 | GET | `{domain}/workflows/{workflow}/instances/{instance}/functions/{function}` | YAPILACAK | Instance-scoped function (state, view, data, schema, extensions, authorize, hierarchy) |

## Definition Endpoints (Orchestration: DefinitionController)

| # | HTTP | Orchestration Route | Monitor Durumu | Notlar |
|---|------|-------------------|----------------|--------|
| 14 | POST | `definitions/publish` | UYGULANMAZ (write) | — |
| 15 | GET | `definitions/re-initialize` | UYGULANMAZ (admin) | — |

## Utility Endpoints (Orchestration: UtilityController)

| # | HTTP | Orchestration Route | Monitor Durumu | Notlar |
|---|------|-------------------|----------------|--------|
| 16 | GET | `config` | YAPILACAK | Runtime config bilgisi |
| 17 | POST | `utilities/invalidate` | UYGULANMAZ (write) | — |
| 18 | POST | `utilities/discovery/refresh` | UYGULANMAZ (write) | — |
| 19 | POST | `utilities/cache/invalidate` | UYGULANMAZ (write) | — |

## Monitor-Only Endpoints (Orchestration'da karsiligi yok)

| # | HTTP | Monitor Route | Durum | Notlar |
|---|------|-------------|--------|--------|
| M1 | GET | `monitor/{domain}/workflows/{workflow}/instances/{instance}/timeline?transitionId=&taskId=&includeTasks=` | TAMAMLANDI | Birlesik timeline: tum akis / tek transition / tek task. `tasks` ve `tasks/timeline` rotalari bu tek `timeline` rotasinda birlestirildi. |
| M2 | GET | `monitor/{domain}/components?type=&key=&version=` | TAMAMLANDI | Tam liste: snapshot bos ise `IRuntimeService` + cache warm. Tek kayit: `key`. |
| M3 | GET | `monitor/health/detail` | TAMAMLANDI | Health check detay JSON |

## Orchestration Kaynak Dosyalari

Mirroring yaparken su dosyalari referans al:

| Controller | Dosya Yolu |
|-----------|-----------|
| InstanceController | `vnext/orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs` |
| FunctionController | `vnext/orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Functions/FunctionController.cs` |
| DefinitionController | `vnext/orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Definitions/DefinitionController.cs` |
| UtilityController | `vnext/orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Utilities/UtilityController.cs` |

## Application Service Interface'leri

| Interface | Dosya Yolu | Onemli Metodlar |
|-----------|-----------|-----------------|
| `IInstanceQueryAppService` | `vnext/src/BBT.Workflow.Application/Instances/IInstanceQueryAppService.cs` | `GetInstanceAsync`, `GetInstanceListAsync`, `GetInstanceHistoryAsync`, `GetInstanceDataAsync`, `GetInstanceStateAsync`, `GetViewAsync`, `GetSchemaAsync`, `GetExtensionsAsync`, `GetInstanceHierarchyAsync` |
| `IFunctionAppService` | `vnext/src/BBT.Workflow.Application/Functions/IFunctionAppService.cs` | `GetFunctionByKeyAsync`, `GetFunctionByInstanceAsync`, `GetFunctionsAsync` |
| `IDefinitionAppService` | `vnext/src/BBT.Workflow.Application/Definitions/IDefinitionAppService.cs` | `PublishAsync`, `InvalidateCacheAsync`, `ReInitializeAsync` |

## InstanceController Detay (Mirroring Referansi)

Orchestration'daki GET endpoint'lerinin kabul ettigi parametreler:

| Endpoint | Headers | Query Params | Notlar |
|----------|---------|-------------|--------|
| `GET instances/{instance}` | `If-None-Match` | `extensions[]`, `version` | ETag / 304 destegi, extension inject |
| `GET instances` | — | `filter`, `page`, `pageSize`, `sort`, `orderBy`, `version`, `extensions[]` | HATEOAS links, GraphQL filter |
| `GET instances/{instance}/transitions` | — | `extensions[]`, `version` | Transition history |
| `GET instances/{instance}/data` | `If-None-Match` | `version` | ETag / 304 destegi |

## FunctionController Detay

| Endpoint | Parametre | Davranis |
|----------|----------|---------|
| `GET {domain}/functions` | — | Domain'deki tum function'lari listeler |
| `GET {domain}/functions/{function}` | `version` query | Key ile function definition doner |
| `GET .../instances/{instance}/functions/{function}` | `FunctionQueryParameters`, `If-None-Match` | `IInstanceFunctionHandlerFactory` ile handler dispatch: state, view, data, schema, extensions, authorize, authorization-matrix, hierarchy |
