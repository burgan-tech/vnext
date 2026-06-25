# Health Check Optimization Design

**Date:** 2026-06-24  
**Branch:** feature/phase3-healthcheck (mevcut branch)  
**Status:** Approved

## Problem

Clustered ortamda (k8s + Dapr) her pod için birden fazla kaynak sürekli health check yapar:
- k8s liveness + readiness probe
- Dapr sidecar health probe
- Monitoring sistemleri (Prometheus scrape, alertmanager vb.)

Tüm host'lar (Inbox, Outbox, Orchestration) `AddAppHealthChecks()` üzerinden `AddNpgSql(...)` kaydı yapıyor. Bu, her `/ready` isteğinin PostgreSQL'e direkt bağlantı açması anlamına geliyor. Yüksek probe frekansında bu DB bağlantı havuzunu tüketiyor.

## Kapsam Dışı

- Execution host: zaten kendi `AddExecutionHealthChecks()` metodunu kullanıyor, sadece `"self"` check içeriyor. Dokunulmayacak.
- k8s / Dapr probe interval ayarları: bu tasarımın kapsamı dışında.

## Tasarım

### Yaklaşım: Shared'dan DB çıkar, Orchestration'a taşı (Approach A)

```
HttpApi.Shared — AddAppHealthChecks()
  "self" check only  (live tag)   ← sadece bu kalır

Orchestration — OrchestratorServiceCollectionExtensions
  AddAppHealthChecks()            ← self check
  + TryAddSingleton<CachedHealthCheck>
      inner  = NpgSqlHealthCheck(connectionString)
      ttl    = 60s (IOptions<HealthCheckCacheOptions>)
  + healthChecks.Add("database",
      factory: sp => sp.GetRequired<CachedHealthCheck>(),
      tags: ["ready"], timeout: 2s)

Inbox / Outbox
  AddAppHealthChecks()            ← değişiklik yok; DB artık shared'da olmadığı için otomatik temizlenir
```

### Sonuç tablosu

| Host | `/live` | `/ready` |
|---|---|---|
| Inbox | self → Healthy | self → Healthy (DB yok) |
| Outbox | self → Healthy | self → Healthy (DB yok) |
| Orchestration | self → Healthy | CachedCheck → max 1 DB sorgu / 60s |
| Execution | self → Healthy | (mevcut durum korunur) |

## CachedHealthCheck

`HttpApi.Shared/HealthChecks/CachedHealthCheck.cs` — type tanımı burada kalır, Orchestration tarafından kullanılır.

**Garantiler:**
- İlk çağrıda (veya TTL dolunca) inner probe çekilir, sonuç 60 sn saklanır.
- TTL dolmadan gelen çağrılar cache'ten döner → sıfır DB bağlantısı.
- Eş zamanlı çağrılarda thundering herd olmaz: `SemaphoreSlim(1,1)` ile tek DB sorgusu.
- Singleton olarak register edilir → TTL state process boyunca korunur.

**Zaman kaynağı:** `TimeProvider` inject edilir → unit test'lerde `FakeTimeProvider` ile TTL davranışı simüle edilebilir.

## HealthCheckCacheOptions

```json
// appsettings.json (Orchestration)
"HealthChecks": {
  "Database": {
    "CacheTtlSeconds": 60
  }
}
```

Default: 60 sn. `IOptions<HealthCheckCacheOptions>` üzerinden okunur. Ortam bazlı override (`appsettings.Production.json` vb.) desteklenir.

**TTL seçimi gerekçesi:** 60 sn. DB gerçekten çökerse k8s en fazla 60 sn sonra readiness probe'u başarısız görür ve pod'u traffic'ten çıkarır. Buna karşılık DB'ye probe başına açılan bağlantı sayısı pratikte 0'a yaklaşır.

## Değişecek Dosyalar

| Dosya | Değişiklik |
|---|---|
| `src/BBT.Workflow.HttpApi.Shared/.../HealthChecksServiceCollectionExtensions.cs` | `AddNpgSql` satırını kaldır; `"self"` check + `ForwardToPrometheus` kalır |
| `src/BBT.Workflow.HttpApi.Shared/HealthChecks/CachedHealthCheck.cs` | Mevcut implementasyonu gözden geçir (singleton guarantee, SemaphoreSlim, TimeProvider) |
| `src/BBT.Workflow.HttpApi.Shared/HealthChecks/HealthCheckCacheOptions.cs` | `CacheTtlSeconds` default 60 olarak güncelle |
| `orchestration/.../OrchestratorServiceCollectionExtensions.cs` | `TryAddSingleton<CachedHealthCheck>` + `healthChecks.Add("database", ...)` ekle |
| `test/.../AppHealthChecksRegistrationTests.cs` | DB check olmadığını doğrula |
| `test/.../OrchestratorHealthChecksRegistrationTests.cs` (yeni) | Orchestration "database" check singleton doğrulaması |

## Test Stratejisi

1. **`AppHealthChecksRegistrationTests`** — `AddAppHealthChecks()` sonrası `"database"` check olmamalı; sadece `"self"` var.
2. **`OrchestratorHealthChecksRegistrationTests`** (yeni) — Orchestration DI sonrası `"database"` check var; factory iki kez çağrıldığında aynı `CachedHealthCheck` instance döner (singleton).
3. **`CachedHealthCheckTests`** — `FakeTimeProvider` ile: TTL dolmadan inner check çağrılmıyor; TTL geçtikten sonra tekrar çağrılıyor; eş zamanlı istek tek DB sorgusuyla sonuçlanıyor.
