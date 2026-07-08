# Skill: vNext Domain Type Reference

Use this skill when you need to look up domain entities, enums, repository interfaces, or cache store methods while developing monitor services.

**Editing this codebase:** Bu skill'de referans verilen `vnext/src/BBT.Workflow.Domain` (ve ilgili `Infrastructure`) dosyalarinda gelistirme yapacaksan **mevcut metotlari veya davranisi degistirme**; ihtiyaci **yeni** repository/interface uyeleri ile karsila. Tam politika: `.cursor/rules/monitor-constraints.md`, `CLAUDE.md` §1.

## When to Use

- Mapping domain entity properties to monitor response DTOs
- Looking up available repository methods for queries
- Understanding entity relationships (Instance -> Transitions -> Tasks)
- Working with component definitions via cache store

## Domain Entities

### Instance (Aggregate Root)

**File**: `vnext/src/BBT.Workflow.Domain/Instances/Instance.cs`

Key properties:
| Property | Type | Description |
|----------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `Key` | `string?` | Business key |
| `Flow` | `string?` | Workflow (flow) key |
| `FlowVersion` | `string?` | Flow version at start |
| `Domain` | `string?` | Domain key |
| `Tags` | `List<string>?` | Tags assigned to instance |
| `CurrentState` | `string?` | Current internal state key |
| `EffectiveState` | `string?` | Effective state exposed to callers |
| `Status` | `InstanceStatus?` | Active, Completed, Faulted, Cancelled |
| `EffectiveStateType` | `StateType?` | Type of effective state |
| `EffectiveStateSubType` | `StateSubType?` | Subtype of effective state |
| `CompletedAt` | `DateTime?` | When completed (null if active) |
| `Duration` | `TimeSpan?` | Total duration (creation to completion) |
| `DataList` | `ICollection<InstanceData>` | All data versions |
| `LatestData` | `InstanceData?` | Most recent data entry |
| `ActiveCorrelations` | `ICollection<InstanceCorrelation>` | Active sub-flow correlations |
| `CreatedAt` | `DateTime` | Creation timestamp |
| `ModifiedAt` | `DateTime?` | Last modification timestamp |
| `CreatedBy` | `string?` | Creator user ID |
| `CreatedByBehalfOf` | `string?` | Creator behalf-of user ID |
| `ModifiedBy` | `string?` | Modifier user ID |
| `ModifiedByBehalfOf` | `string?` | Modifier behalf-of user ID |

### InstanceTransition

**File**: `vnext/src/BBT.Workflow.Domain/Instances/InstanceTransition.cs`

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `TransitionId` | `string?` | Transition definition key |
| `FromState` | `string?` | State before transition |
| `ToState` | `string?` | State after transition (null if in progress) |
| `StartedAt` | `DateTime` | When transition started |
| `FinishedAt` | `DateTime?` | When transition completed |
| `Duration` | `TimeSpan?` | Transition duration |
| `TriggerType` | `TriggerType` | How triggered (Manual, Auto, Scheduled, Event) |
| `CreatedBy` | `string?` | User who triggered |
| `CreatedByBehalfOf` | `string?` | Behalf-of user |

### InstanceTask

**File**: `vnext/src/BBT.Workflow.Domain/Instances/InstanceTask.cs`

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `TransitionId` | `Guid` | Parent transition ID |
| `TaskId` | `string?` | Task definition key |
| `Status` | `TaskExecutionStatus` | Infrastructure status |
| `BusinessStatus` | `TaskBusinessStatus` | Business-level status |
| `StartedAt` | `DateTime` | When task started |
| `FinishedAt` | `DateTime?` | When task finished |
| `Duration` | `TimeSpan?` | Task duration |
| `Request` | `JsonData` | Request payload |
| `Response` | `JsonData` | Response payload |

### InstanceData

**File**: `vnext/src/BBT.Workflow.Domain/Instances/InstanceData.cs`

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `InstanceId` | `Guid` | Parent instance ID |
| `Version` | `string?` | Semantic version identifier |
| `HistorySequence` | `int` | Ordering within same version |
| `VersionNo` | `int` | DB trigger maintained version number |
| `IsLatest` | `bool` | DB trigger maintained flag |
| `ETag` | `string?` | Representation ETag |
| `DataHash` | `string?` | SHA1 hash of normalized JSON |
| `EnteredAt` | `DateTime?` | When this version was entered |
| `Data` | `JsonData` | Raw JSON data (access via `.JsonElement`) |
| `Attributes` | `dynamic?` | Deserialized data (computed from JSON) |

**InstanceDataVersionComparer** (`Instances/InstanceDataVersionComparer.cs`):
- `IComparer<InstanceData>` — SemVer string comparison, then `HistorySequence` as tiebreaker
- `FindBestMatch(versions, requested)` — null/empty/"latest" → en yuksek versiyon; exact match; partial/major match
- `Instance` aggregate'in `Data` ve `LatestData` property'leri bu comparer ile `OrderByDescending` kullanir
- Monitor'da data version history gosterirken ayni siralama kullanilmali: `OrderBy(d, InstanceDataVersionComparer.Instance)`

### InstanceCorrelation

**File**: `vnext/src/BBT.Workflow.Domain/Instances/InstanceCorrelation.cs`

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `ParentState` | `string?` | Parent state where sub-flow was triggered |
| `SubFlowInstanceId` | `Guid` | Sub-flow instance ID |
| `SubFlowDomain` | `string?` | Sub-flow domain |
| `SubFlowName` | `string?` | Sub-flow name |
| `SubFlowVersion` | `string?` | Sub-flow version |
| `SubFlowType` | `CorrelationType` | Type code (S = SubFlow, P = SubProcess) |
| `SubFlowCurrentState` | `string?` | Current state of sub-flow |

## Enums

| Enum | Values |
|------|--------|
| `InstanceStatus` | Active, Completed, Faulted, Cancelled |
| `StateType` | Initial (1), Intermediate (2), Final (3), SubFlow (4), Wizard (5) |
| `StateSubType` | Further classification |
| `TriggerType` | Manual (0), Auto (1), Scheduled (2), Event (3) |

## Value Objects

| Type | Purpose |
|------|---------|
| `JsonData` | Wraps `JsonElement` for instance data. Access via `.JsonElement` property. |

## Repository Interfaces

### IInstanceRepository

**File**: `vnext/src/BBT.Workflow.Domain/Instances/IInstanceRepository.cs`

Extends `IRepository<Instance, Guid>`.

Key methods for monitor:
| Method | Returns | Usage |
|--------|---------|-------|
| `FindByIdentifierAsReadOnlyAsync(string identifier, CancellationToken)` | `Instance?` | Find by GUID or key (includes DataList + ActiveCorrelations) |
| `GetPagedResultsWithGroupsAsync(page, pageSize, filter?, groupBy?, aggregations?, sort?, ct)` | Paged result with optional groups | List instances with filtering |
| `GetActiveDataListAsync(filter?, page?, pageSize?, sort?, ct)` | Data list | Instance data queries |

### IInstanceTransitionRepository

**File**: `vnext/src/BBT.Workflow.Domain/Instances/IInstanceTransitionRepository.cs`

| Method | Returns | Usage |
|--------|---------|-------|
| `GetByInstanceIdAsReadOnlyAsync(Guid instanceId, CancellationToken)` | `List<InstanceTransition>` | All transitions for an instance |

### IInstanceTaskRepository

**File**: `vnext/src/BBT.Workflow.Domain/Instances/IInstanceTaskRepository.cs`

| Method | Returns | Usage |
|--------|---------|-------|
| `GetByTransitionIdAsync(Guid transitionId, CancellationToken)` | `List<InstanceTask>` | Tasks for a specific transition |

### IInstanceCorrelationRepository

**File**: `vnext/src/BBT.Workflow.Domain/Instances/IInstanceCorrelationRepository.cs`

### IInstanceJobRepository

**File**: `vnext/src/BBT.Workflow.Domain/Instances/IInstanceJobRepository.cs`

## Component Cache Store

**Interface**: `IComponentCacheStore` (registered by `AddApplicationCacheModule`)

| Method | Parameters | Returns |
|--------|-----------|---------|
| `GetFlowAsync` | `(domain, key, version?, ct)` | Workflow definition |
| `GetTaskAsync` | `(domain, key, version?, ct)` | Task definition |
| `GetSchemaAsync` | `(domain, key, version?, ct)` | Schema definition |
| `GetViewAsync` | `(domain, key, version?, ct)` | View definition |
| `GetFunctionAsync` | `(domain, key, version?, ct)` | Function definition |
| `GetExtensionAsync` | `(domain, key, version?, ct)` | Extension definition |
| `GetAllExtensionsAsync` | `(domain, ct)` | All extensions for domain |

## Entity Relationships

```
Instance (AggregateRoot)
  ├── DataList: InstanceData[]         (versioned data history)
  ├── LatestData: InstanceData?        (most recent data)
  ├── ActiveCorrelations: InstanceCorrelation[]  (sub-flows)
  ├── InstanceTransition[]             (via IInstanceTransitionRepository)
  │     └── InstanceTask[]             (via IInstanceTaskRepository, by TransitionId)
  └── InstanceJob[]                    (via IInstanceJobRepository)
```

## Common Mapping Pattern

Domain entity'den Monitor DTO'ya mapping ornegi:

```csharp
private static MonitorInstanceResponse MapToResponse(Instance instance, string domain)
{
    return new MonitorInstanceResponse
    {
        Id = instance.Id,
        Key = instance.Key,
        Flow = instance.Flow,
        FlowVersion = instance.FlowVersion,
        Domain = domain,
        Tags = instance.Tags,
        Metadata = new MonitorInstanceMetadata
        {
            CurrentState = instance.CurrentState,
            EffectiveState = instance.EffectiveState,
            Status = instance.Status,
            EffectiveStateType = instance.EffectiveStateType,
            CompletedAt = instance.CompletedAt,
            Duration = instance.Duration?.TotalSeconds,
            CreatedAt = instance.CreatedAt,
            ModifiedAt = instance.ModifiedAt,
            CreatedBy = instance.CreatedBy,
            CreatedByBehalfOf = instance.CreatedByBehalfOf
        }
    };
}
```

## Instance Aggregate Internals

Monitor servisleri gelistirirken Instance aggregate'in ic yapisini anlamak onemli:

- `DataList` encapsulated collection: `_dataList` (private `List<InstanceData>`) → `IReadOnlyCollection<InstanceData>` expose edilir
- `_childCorrelations` → `IReadOnlyCollection<InstanceCorrelation> ChildCorrelations`
- `ActiveCorrelations` = `ChildCorrelations.Where(c => !c.IsCompleted)`
- `Subflow` = ilk non-completed SubFlow type correlation
- `Data` property (dynamic) = thread-safe lock ile `_dataList.OrderByDescending(InstanceDataVersionComparer).First()?.Attributes`
- `LatestData` = ayni siralama ile `InstanceData?` nesnesi
- Lifecycle flags: `IsCompleted`, `IsBusy`, `IsActive`, `IsSubFlow`, `IsSubItem`, `HasActiveSubFlow`
- `CreateSnapshot()` deep-copy yapar (data + correlations)

**Dikkat**: `FindByIdentifierAsReadOnlyAsync` iki graph icerir: `DataList` ve `ChildCorrelations` (split query ile). `ActiveCorrelations` memory'de filtrelenir.

## Extensions Mekanizmasi

vNext'te "extensions" workflow'a bagli veya global (`ExtensionType.Global`) task tanimi olarak modellenir.

- `IInstanceExtensionService.ProcessExtensionsAsync(requestedKeys, scriptContext, workflow, scope, ct)` → `Result<Dictionary<string, object>>`
- Core (global) extensions: `GetAllExtensionsAsync` ile cache'ten cekilir
- Workflow extensions: `workflow.Extensions` referanslarindan cekilir
- Her extension icin `ITaskCoordinator.ExecuteAsync` calistirilir
- Cikti, extension key ile dictionary'ye yazilir

Monitor'da extension verileri:
- Orchestration'da GET instance/data/history endpoint'lerine `extensions[]` query param ile eklenebilir
- Monitor simdilik extension-free calisir (performans icin)
- Gelecekte extension destegi eklenirse `IInstanceExtensionService` inject edilip kullanilabilir

## Workflow Definition Model

Cache store'dan donen definition tipleri:

### Workflow
`Key`, `Domain`, `Flow`, `Version`, `Type`, `Timeout`, `Cancel`, `UpdateData`, `Exit`, `ErrorBoundary`, `StartTransition`, `Schema` ref, `Labels[]`, `Functions[]`, `Features[]`, `SharedTransitions[]`, `Extensions[]`, `States[]`, `QueryRoles[]`

### State
`Key`, `StateType`, `SubType`, `VersionStrategy`, `SubFlow`, `View` (ViewDefinition), `ErrorBoundary`, `Transitions[]`, `OnEntries[]`, `OnExits[]`, `QueryRoles[]`

### Transition
`Key`, `From`, `Target`, `TriggerType`, `TriggerKind`, `VersionStrategy`, `Timer`/`Rule`/`Mapping` scripts, `Schema`, `AvailableIn`, `Roles[]`, `Labels[]`, `OnExecutionTasks[]`, optional `View`

### Function
`Key`, `Domain`, `Flow`, `Version`, `Scope`, `Task` or `OnExecutionTasks`, `Output` script, `Roles`

### View
`Key`, `Domain`, `Flow`, `Version`, `Type` (JSON/HTML/Markdown), `Content`, `Display`, `Labels[]`

### SchemaDefinition
`Key`, `Domain`, `Flow`, `Version`, `Type`, `Schema` (JsonElement)

## InstanceListWithGroupsResponse

`Application/Instances/DTOs/InstanceListWithGroupsResponse.cs` — list endpoint'lerinde kullanilir:
- `Links` — HATEOAS links (controller set eder)
- `Items` — `List<object>` (instance listesi VEYA group summary'ler)
- `FromPagedList(pagedList)` — groupBy yoksa instance listesi
- `FromGroups(groups)` — groupBy varsa sadece group summary
- GroupBy aktifken Items icinde instance nesneleri OLMAZ, sadece aggregation sonuclari olur

## Transition Pipeline (Bilgi Amacli)

Monitor pipeline'i KULLANMAZ ama davranisini anlamak icin:
1. `TransitionPipeline.RunAsync` → distributed lock al → step'leri calistir → post-commit → lock birak → otomatik sonraki transition chain
2. `ITransitionStep` implementations (sirayla): HandleCancelPreflight, HandleUpdateDataPreflight, ... ResolveAvailable
3. Her step `Result<StepOutcome>` doner (continue, stop, replan)
4. Sync mode (`sync=true`): tum pipeline tamamlanip sonuc dogrudan response'ta doner
5. Async mode: pipeline arka planda calisir, instance status "Busy" olur

## Test Conventions

Mevcut test yapisi (yeni monitor testleri icin ornek):
- **xUnit** test framework
- **Shouldly** assertion library
- **Moq** mocking
- `WorkflowTestBase<TEntry>` base class (mock HttpContext, test headers)
- `ApplicationTestBase`, `DomainTestBase`, `InfrastructureTestBase` katman bazli base'ler
- Test project adlandirma: `BBT.Workflow.{Layer}.Tests`
- Monitor icin: `BBT.Workflow.Monitor.Application.Tests` veya `BBT.Workflow.Monitor.Tests` olusturulabilir

## Documentation Sources

Daha derin bilgi icin:
| Konu | Dosya |
|------|-------|
| Domain modelleri | `vnext/docs/architecture/domain-models.md` |
| Instance filtering | `vnext/docs/features/instance-filtering.md` |
| Caching strategy | `vnext/docs/features/caching-strategy.md` |
| Application services | `vnext/docs/implementation/application-services.md` |
| Multi-schema | `vnext/docs/architecture/multi-schema.md` |
| Transition pipeline | `vnext/docs/architecture/transition-pipeline.md` |
| Task invoker | `vnext/docs/architecture/task-invoker.md` |
| Scripting engine | `vnext/docs/features/scripting-engine.md` |
| Instance hierarchy | `vnext/docs/features/instance-hierarchy-function-en.md` |
| View selection | `vnext/docs/features/rule-based-view-selection-en.md` |
| OpenTelemetry logging | `vnext/docs/infrastructure/opentelemetry-logging.md` |
| Inbox/outbox workers | `vnext/docs/infrastructure/inbox-outbox-workers.md` |
