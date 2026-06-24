# Inbox & Outbox — Internals & Provider Guide

Bu belge, inbox/outbox altyapısının iç mimarisini açıklar ve yeni bir veritabanı sağlayıcısı
(örn. SQL Server) eklemek isteyen geliştiriciler için kılavuz niteliği taşır.

---

## Mimari Katmanlar

```
BBT.Aether.Abstractions
  └── IOutboxLeaseStore       ← kira (lease) kontratı
  └── IInboxLeaseStore
  └── IOutboxStore            ← yalnızca StoreAsync (write)
  └── IInboxStore             ← write + mark operasyonları
  └── OutboxMessageStatus     ← Pending / Processing / Processed / DeadLetter
  └── IncomingEventStatus     ← Pending / Processing / Processed / Discarded / DeadLetter

BBT.Aether.Core
  └── IOutboxProcessor        ← Task<int> RunAsync(...)
  └── IInboxProcessor
  └── NullOutboxLeaseStore    ← kayıt yoksa devreye girer, boş liste döner
  └── NullInboxLeaseStore
  └── AetherOutboxOptions
  └── AetherInboxOptions

BBT.Aether.Infrastructure
  └── EfCoreOutboxStore       ← StoreAsync (EF Core, provider-agnostic)
  └── EfCoreInboxStore        ← write/mark, dead letter mantığı
  └── OutboxProcessor         ← 3 fazlı işlem döngüsü
  └── InboxProcessor          ← lease-based, distributed lock YOK
  └── OutboxBackgroundService ← adaptive polling host
  └── InboxBackgroundService
  └── WorkerIdentity          ← pod/process/instance kimliği

BBT.Aether.Npgsql
  └── NpgsqlOutboxLeaseStore  ← FOR UPDATE SKIP LOCKED
  └── NpgsqlInboxLeaseStore
```

Temel ilke: **Infrastructure, ham SQL içermez.** Her ham SQL sorgusu sağlayıcı paketine taşınmıştır.

---

## Lease Mekanizması

### IOutboxLeaseStore / IInboxLeaseStore

```csharp
public interface IOutboxLeaseStore
{
    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);
}
```

`LeaseBatchAsync` tek bir atomik işlemde şunları yapar:

1. `Status = Pending` olan ve kilidi süresi dolmuş ya da hiç kilitlenmemiş mesajları seçer
2. `Status = Processing`, `LockedBy = workerId`, `LockedUntil = now + leaseDuration` olarak günceller
3. Güncellenen satırları döndürür

Bu yöntem, çağıranın açtığı aktif bir işlem içinde çalışmalıdır. `FOR UPDATE SKIP LOCKED`
(PostgreSQL) ya da `UPDLOCK, READPAST, ROWLOCK` (SQL Server) kilidi sayesinde birden fazla
worker aynı mesaj üzerinde yarışmaz.

### Null Fallback

`AddAetherOutbox` / `AddAetherInbox` çağrıldığında:

```csharp
services.TryAddScoped<IOutboxLeaseStore, NullOutboxLeaseStore>();
```

`TryAddScoped` kullandığı için, sağlayıcı paketi (Npgsql vb.) `AddScoped` ile gerçek
implementasyonu kaydetmişse null versiyon devreye girmez. Kayıt sırası önemli değildir.

---

## OutboxProcessor — 3 Fazlı İşlem Modeli

```
┌─────────────────────────────────────────────┐
│  FAZA 1 — Lease (kısa transaction)          │
│  IOutboxLeaseStore.LeaseBatchAsync(...)      │
│  → LockedBy = workerId, Status = Processing │
│  → Commit                                   │
└──────────────────┬──────────────────────────┘
                   │ messages[]
┌──────────────────▼──────────────────────────┐
│  FAZA 2 — Publish (transaction YOK)         │
│  IDistributedEventBus.PublishEnvelopeAsync  │
│  → her mesaj için Success / Failure kaydı  │
└──────────────────┬──────────────────────────┘
                   │ outcomes[]
┌──────────────────▼──────────────────────────┐
│  FAZA 3 — Outcome (kısa transaction)        │
│  ExecuteUpdateAsync (success path)          │
│  FirstOrDefaultAsync + in-memory (failure)  │
│  → Commit                                   │
└─────────────────────────────────────────────┘
```

### Faza 3 — Race Condition Koruması

Broker işlemi sırasında kiranın süresi dolarsa, başka bir worker aynı mesajı lease alabilir.
Bu riski ortadan kaldırmak için başarı durumunda `ExecuteUpdateAsync` WHERE koşulu içerir:

```csharp
var affected = await dbContext.OutboxMessages
    .Where(m => m.Id == outcome.MessageId
             && m.LockedBy == workerId        // hâlâ bu worker'ın
             && m.LockedUntil > now)          // kira süresi geçmemiş
    .ExecuteUpdateAsync(s => s
        .SetProperty(m => m.Status, OutboxMessageStatus.Processed)
        .SetProperty(m => m.ProcessedAt, now)
        .SetProperty(m => m.LockedBy, (string?)null)
        .SetProperty(m => m.LockedUntil, (DateTime?)null), ct);

if (affected == 0)
    logger.LogWarning("Lease expired or taken by another worker — outcome skipped");
```

`affected == 0` durumunda mesaj hata fırlatılmadan geçilir. Kirası dolmuş mesaj başka bir
worker tarafından tekrar alınacak ve yeniden yayımlanacaktır (at-least-once semantiği).

---

## Dead Letter Geçişi

### Outbox

`OutboxProcessor`, başarısız yayımlama sonrasında şu mantığı uygular:

```
if (RetryCount + 1 >= MaxRetryCount)
    → Status = DeadLetter, LockedBy = null, LockedUntil = null
else
    → RetryCount++, NextRetryAt = now + RetryBaseDelay * 2^(RetryCount-1), Status = Pending
```

### Inbox

`EfCoreInboxStore.MarkAsFailedAsync` aynı mantığı EF Core üzerinden uygular:

```
if (RetryCount + 1 >= MaxRetryCount)
    → Status = DeadLetter, LockedBy = null, LockedUntil = null
else
    → RetryCount++, NextRetryTime = ..., Status = Pending, LockedBy = null, LockedUntil = null
```

Lease sorguları (`WHERE Status = Pending`) otomatik olarak `DeadLetter` mesajları dışarıda bırakır.
`DeadLetter` mesajlar otomatik olarak yeniden denenmez; manuel müdahale gerektirir.

---

## WorkerIdentity

`WorkerIdentity` singleton'ı, işlem sırasında kimin hangi mesajı tuttuğunu tespit etmek için kullanılır.

```
{ApplicationName}/{HOSTNAME or MachineName}/{ProcessId}/{InstanceGuid[0..8]}
```

Örnek: `order-service/pod-abc123/12345/f3a9c1b2`

- **ApplicationName** → `IHostEnvironment.ApplicationName`
- **HOSTNAME** → Kubernetes pod adı; yoksa `Environment.MachineName`
- **ProcessId** → `Environment.ProcessId` (aynı pod'da birden fazla proses)
- **InstanceGuid** → startup'ta üretilen 8 karakterlik rastgele kimlik (rolling restart koruması)

Her processor kendi sonek'ini ekler:

```csharp
var workerId = $"{workerIdentity.Value}/outbox";   // OutboxProcessor
var workerId = $"{workerIdentity.Value}/inbox";    // InboxProcessor
```

Bu sayede `LockedBy` alanındaki değer, hangi pod, proses ve servis türünün mesajı tuttuğunu açıkça gösterir.

---

## Adaptive Polling

`OutboxBackgroundService` ve `InboxBackgroundService` aynı algoritmayı kullanır:

```
delay = IdlePollingInterval   // başlangıç

loop:
    processed = processor.RunAsync()

    if processed > 0:
        delay = BusyPollingInterval          // anında tekrar
    else:
        delay = min(delay * 2, MaxPollingInterval)   // üstel geri çekilme

    await Task.Delay(delay)
```

| Senaryo | Gecikme |
|---|---|
| Mesaj bulundu | `BusyPollingInterval` (varsayılan: 100ms) |
| Mesaj bulunamadı — ilk boş tur | `IdlePollingInterval * 2` (varsayılan: 10s) |
| Sürekli boş | Her turda 2x büyür → `MaxPollingInterval` (varsayılan: 60s) |
| Exception | `MaxPollingInterval` (hemen tekrar denemez) |

Bu yaklaşım, yüksek trafik altında gecikmeyi minimumda tutarken boşta çalışan pod'ların
veritabanına gereksiz bağlantı açmasını önler.

---

## Kayıt Sırası ve Override Modeli

```
AddAetherOutbox<TDbContext>()
  → services.TryAddScoped<IOutboxLeaseStore, NullOutboxLeaseStore>()   ← fallback

AddAetherNpgsql<TDbContext>()
  → if (IHasEfCoreOutbox)
        services.AddScoped<IOutboxLeaseStore, NpgsqlOutboxLeaseStore<TDbContext>>()   ← override
```

`AddScoped` her zaman `TryAddScoped` sonraki kaydının önüne geçer — sıra farklı olsa bile.
Böylece uygulama hem `AddAetherOutbox` hem `AddAetherNpgsql` çağrıyorsa gerçek PostgreSQL
implementasyonu aktif olur.

---

## Yeni Sağlayıcı Ekleme (örn. SQL Server)

SQL Server için `UPDLOCK, READPAST, ROWLOCK` hint'leriyle bir outbox lease store yazmak gerekir.

### 1. LeaseStore implementasyonu

```csharp
// framework/src/BBT.Aether.SqlServer/BBT/Aether/Events/SqlServerOutboxLeaseStore.cs
public class SqlServerOutboxLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IOutboxLeaseStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    public async Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize, string workerId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        // SQL Server paterni:
        // UPDATE TOP (@batchSize) tablo
        // SET Status = @processing, LockedBy = @workerId, LockedUntil = @lockedUntil
        // OUTPUT INSERTED.*
        // FROM tablo WITH (UPDLOCK, READPAST, ROWLOCK)
        // WHERE Status = @pending
        //   AND (LockedUntil IS NULL OR LockedUntil < @now)
        //   AND (NextRetryAt IS NULL OR NextRetryAt <= @now)
        // ...
    }
}
```

### 2. Registration

```csharp
// framework/src/BBT.Aether.SqlServer/Microsoft/Extensions/DependencyInjection/...
public static IServiceCollection AddAetherSqlServer<TDbContext>(
    this IServiceCollection services, string connectionString, ...)
    where TDbContext : AetherDbContext<TDbContext>
{
    services.AddAetherDbContext<TDbContext>(new SqlServerAetherProvider(), connectionString);

    if (typeof(IHasEfCoreOutbox).IsAssignableFrom(typeof(TDbContext)))
        services.AddScoped(typeof(IOutboxLeaseStore),
            typeof(SqlServerOutboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

    if (typeof(IHasEfCoreInbox).IsAssignableFrom(typeof(TDbContext)))
        services.AddScoped(typeof(IInboxLeaseStore),
            typeof(SqlServerInboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

    return services;
}
```

### 3. Dikkat edilmesi gerekenler

- SQL Server `FOR UPDATE SKIP LOCKED` desteklemez — `READPAST` ile satır atlar
- `OUTPUT INSERTED.*` hem lock hem de döndürme işlemini tek sorguda yapar
- SQL Server tek şemada çalışır; `ICurrentSchema.Change(...)` tablo adını değiştirmez
- Tablo adı model üzerindeki `HasDefaultSchema` / `ToTable(schema:)` ile sabittir
- `EfCoreInboxStore.MarkAsFailedAsync` ve `EfCoreOutboxStore.StoreAsync` değişmez — sağlayıcıdan bağımsızdır

---

## Anahtar Dosyalar

| Dosya | Sorumluluk |
|---|---|
| `Abstractions/BBT/Aether/Events/IOutboxLeaseStore.cs` | Lease kontratı |
| `Abstractions/BBT/Aether/Events/IInboxLeaseStore.cs` | Lease kontratı |
| `Core/BBT/Aether/Events/NullOutboxLeaseStore.cs` | Boş fallback |
| `Core/BBT/Aether/Events/AetherOutboxOptions.cs` | Polling + lease + retry ayarları |
| `Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs` | StoreAsync (write) |
| `Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs` | Write + mark + dead letter |
| `Infrastructure/BBT/Aether/Events/WorkerIdentity.cs` | Yapısal worker kimliği |
| `Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs` | 3 fazlı işlem döngüsü |
| `Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs` | Lease-based inbox döngüsü |
| `Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs` | Adaptive polling host |
| `Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs` | FOR UPDATE SKIP LOCKED |
| `Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs` | FOR UPDATE SKIP LOCKED |
