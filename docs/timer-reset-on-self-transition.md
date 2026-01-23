# Timer Reset on Self-Transition

## Overview

Bu geliştirme, workflow'larda self-transition (kendi kendine geçiş) yapıldığında scheduled timer'ların otomatik olarak resetlenmesini sağlar.

## Problem

Bir state'e girildiğinde scheduled transition'lar için timer job'ları oluşturulur. Aynı state'e tekrar geçiş yapıldığında (self-transition), eski timer job'ları cancel edilmiyordu. Bu durum:

- Timer'ın resetlenmemesine
- Duplicate job execution'larına
- Beklenmeyen transition tetiklenmelerine

neden oluyordu.

## Çözüm

`ScheduleTransitionsStep` pipeline step'ine timer cancellation logic'i eklendi:

1. State'e her girildiğinde, önce o instance'a ait **tüm aktif job'lar** cancel edilir
2. Ardından yeni timer job'ları schedule edilir
3. Bu sayede timer her zaman sıfırdan başlar

## Teknik Detaylar

### Değişen Dosyalar

| Dosya | Değişiklik |
|-------|------------|
| `ScheduleTransitionsStep.cs` | `CancelExistingScheduledJobsAsync` metodu eklendi |
| `WorkflowLogs.cs` | `ExistingTimerJobsCanceled` log message eklendi |
| `WorkflowEventIds.cs` | Event ID 10015 eklendi |
| `TransitionExecutionContextExtensions.cs` | `IsSelfTransition()` extension metodu eklendi |

### Akış

```
State'e Giriş
    │
    ▼
┌─────────────────────────────────┐
│ HasScheduledTransitions?        │
└─────────────────────────────────┘
    │ Evet
    ▼
┌─────────────────────────────────┐
│ CancelExistingScheduledJobsAsync│
│ - Instance'ın TÜM aktif job'ları│
│ - Dapr'dan sil                  │
│ - DB'de IsActive = false        │
└─────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────┐
│ ScheduleNewJobsAsync            │
│ - Yeni timer job'ları oluştur   │
└─────────────────────────────────┘
```

### Kod Örneği

```csharp
private async Task<Result> CancelExistingScheduledJobsAsync(
    TransitionExecutionContext context,
    CancellationToken cancellationToken)
{
    var activeJobs = await jobRepository.GetListActiveAsync(context.InstanceId, cancellationToken);
    
    if (!activeJobs.Any())
    {
        return Result.Ok();
    }

    foreach (var job in activeJobs)
    {
        try
        {
            // Dapr'dan sil
            await jobScheduler.DeleteAsync(
                TransitionTimerJobHandler.HandlerName, 
                job.JobName, 
                cancellationToken);
            
            // DB'de işaretle
            job.MarkAsProcessed();
            await jobRepository.UpdateAsync(job, true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel timer job: JobName={JobName}", job.JobName);
        }
    }

    logger.ExistingTimerJobsCanceled(activeJobs.Count, context.InstanceId);
    return Result.Ok();
}
```

## Güvenlik

| Kapsam | Davranış |
|--------|----------|
| Başka Instance | ❌ Etkilenmez - `GetListActiveAsync(instanceId)` filtresi |
| Başka Flow | ❌ Etkilenmez - Her flow ayrı schema'da |
| Aynı Instance | ✅ Tüm aktif job'lar cancel edilir |

## Workflow Tanımı Örneği

```json
{
  "key": "control-state",
  "type": "intermediate",
  "transitions": [
    {
      "key": "retry",
      "target": "$self",
      "type": "manual"
    },
    {
      "key": "timeout",
      "target": "$self",
      "type": "scheduled",
      "timer": {
        "duration": "PT5M"
      }
    }
  ]
}
```

Bu örnekte:
- `retry` manual olarak tetiklendiğinde
- `timeout` timer'ı resetlenir ve 5 dakika yeniden başlar

## Logging

Timer cancellation işlemleri loglanır:

```
Canceled 1 existing timer job(s) for instance xxx before scheduling new timers
Scheduled timer job: JobId=xxx, JobName=trans-xxx-to-control-state, InstanceId=xxx
```

## Test Senaryoları

1. **Self-Transition ile Timer Reset**
   - State'e gir → Timer başlar
   - Self-transition yap → Timer resetlenir
   - Beklenen: Timer sıfırdan sayar

2. **Normal State Değişikliği**
   - State A'da timer var
   - State B'ye geç
   - Beklenen: State A'nın timer'ı cancel olur

3. **Birden Fazla Scheduled Transition**
   - State'de 2 farklı timer var
   - Self-transition yap
   - Beklenen: Her iki timer da resetlenir
