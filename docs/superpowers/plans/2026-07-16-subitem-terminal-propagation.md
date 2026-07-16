# SubItem Terminal Propagation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SubFlow ve SubProcess child instance'larinin Completed, Faulted ve Canceled sonuclarini parent correlation'a kayipsiz ve idempotent bicimde yansitmak; parent kaynakli fault/cancel cascade'lerinde yukari bildirim dongusunu engellemek.

**Architecture:** `InstanceCorrelation` terminal sonucun kalici ve tek dogruluk kaynagi olur. Child terminal event'i child state ile ayni UOW outbox'ina yazilir; hizli hook yalniz commit sonrasinda calisir ve outbox kaydini bastirmaz. Parent consumer `RequiresNew` UOW icinde correlation tipine gore karar verir: SubProcess yalniz correlation kapatir, blocking SubFlow fault mevcut boundary akisini korur, blocking SubFlow cancel ise mapping/incident olmadan pipeline'i resume eder.

**Tech Stack:** .NET 10, C# 14, EF Core 10, PostgreSQL/Npgsql, BBT.Aether UOW + Inbox/Outbox, xUnit, NSubstitute/Moq, Dapr service invocation.

## Global Constraints

- Ilk commit edilen terminal outcome kazanir; ayni outcome duplicate basarili no-op, farkli outcome conflict log + no-op olur.
- `ParentCascade` ile sonlanan child yukari terminal event'i uretmez.
- Ayni `InitiatorInstanceId` ve `CascadeId` nested cascade boyunca korunur.
- Blocking SubFlow cancel data map etmez, incident olusturmaz ve error boundary calistirmaz.
- SubProcess fault/cancel parent status, data, incident veya pipeline'ini degistirmez.
- Parent davranisi event payload'indaki tipe degil, kayitli `InstanceCorrelation.SubFlowType` degerine guvenir.
- Terminal event outbox'a child state ile ayni transaction'da yazilir; basarili hook outbox kaydini bastirmaz.
- Parent terminal handling `RequiresNew` UOW kullanir; hard resume hatasi correlation'i yeni UOW'da revert eder.
- Mevcut kullanici degisiklikleri stage edilmez; her commit'te sadece ilgili task dosyalari eklenir.
- Tasarim kaynagi: `docs/superpowers/specs/2026-07-16-subitem-terminal-propagation-design.md`.

---

## File map

| Sorumluluk | Dosyalar |
|---|---|
| Terminal outcome aggregate modeli | `src/BBT.Workflow.Domain/Instances/InstanceCorrelation.cs`, yeni `SubItemTerminalOutcome.cs`, yeni `TerminalOutcomeApplyResult.cs` |
| Cascade context ve event contract'lari | yeni `TerminationOrigin.cs`, `TerminationContext.cs`, `SubItemType.cs`, `InstanceSubCanceledEvent.cs`; mevcut fault/downward event'leri |
| Child event uretimi | `src/BBT.Workflow.Domain/Instances/Instance.cs`, pipeline execution context/factory dosyalari |
| Parent fault/cancel handling | `SubflowFaultService.cs`, yeni `SubflowCancellationService.cs` ve DTO/contract'lari |
| Local/remote routing | `IInstanceCommandGateway.cs`, local/remote/routed gateway'ler, remote app service, URL templates, controller |
| Inbox ve hook adaptasyonlari | yeni canceled handler/hook; mevcut fault ve downward handler/hook'lari |
| Durable post-commit hook | `EventHookAttribute.cs`, `HookedDistributedEventBus.cs`, `TransitionRunner.cs` |
| Persistence | `InstancesModelCreatingExtensions.cs`, yeni WorkflowDb migration + snapshot |
| Regression coverage | Domain, Application ve Infrastructure test projeleri |

---

### Task 1: Correlation terminal outcome modelini kalici hale getir

**Files:**
- Create: `src/BBT.Workflow.Domain/Instances/SubItemTerminalOutcome.cs`
- Create: `src/BBT.Workflow.Domain/Instances/TerminalOutcomeApplyResult.cs`
- Modify: `src/BBT.Workflow.Domain/Instances/InstanceCorrelation.cs`
- Modify: `src/BBT.Workflow.Domain/Instances/Instance.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Data/InstancesModelCreatingExtensions.cs`
- Create: `src/BBT.Workflow.Infrastructure/Migrations/20260716210000_InstanceCorrelationTerminalOutcome.cs`
- Create: `src/BBT.Workflow.Infrastructure/Migrations/20260716210000_InstanceCorrelationTerminalOutcome.Designer.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Migrations/WorkflowDbContextModelSnapshot.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Instances/InstanceCorrelationTests.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Instances/InstanceTests.cs`

**Interfaces:**
- Produces: `TerminalOutcomeApplyResult InstanceCorrelation.ApplyTerminalOutcome(SubItemTerminalOutcome outcome, DateTime completedAt)`.
- Produces: `InstanceCorrelation? Instance.CompleteCorrelation(Guid subInstanceId, SubItemTerminalOutcome outcome, DateTime? completedAt = null)`.
- Produces: nullable `InstanceCorrelation.TerminalOutcome` mapped as PostgreSQL integer.

- [ ] **Step 1: Failing correlation tests yaz**

`InstanceCorrelationTests.cs` icine outcome, duplicate, conflict ve revert davranislarini ekle:

```csharp
[Fact]
public void ApplyTerminalOutcome_ShouldPersistFirstOutcome_AndRejectConflict()
{
    var correlation = CreateCorrelation("S");
    var completedAt = DateTime.UtcNow;

    correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Faulted, completedAt)
        .ShouldBe(TerminalOutcomeApplyResult.Applied);
    correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Faulted, completedAt.AddSeconds(1))
        .ShouldBe(TerminalOutcomeApplyResult.Duplicate);
    correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Canceled, completedAt.AddSeconds(2))
        .ShouldBe(TerminalOutcomeApplyResult.Conflict);

    correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
    correlation.CompletedAt.ShouldBe(completedAt);
}

[Fact]
public void Revert_ShouldClearTerminalOutcome()
{
    var correlation = CreateCorrelation("S");
    correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Canceled, DateTime.UtcNow);

    correlation.Revert();

    correlation.IsCompleted.ShouldBeFalse();
    correlation.CompletedAt.ShouldBeNull();
    correlation.TerminalOutcome.ShouldBeNull();
}
```

Ayni test sinifina kullanilan helper'i ekle:

```csharp
private static InstanceCorrelation CreateCorrelation(string typeCode) =>
    InstanceCorrelation.Create(
        Guid.NewGuid(), Guid.NewGuid(), "state", Guid.NewGuid(), typeCode,
        "domain", "flow", "1.0.0");
```

- [ ] **Step 2: Testlerin beklenen nedenle fail ettigini dogrula**

Migration'i once EF ile uret, sonra uretilen iki dosyanin migration ID'sini ve adini
`20260716210000_InstanceCorrelationTerminalOutcome` olarak normalize et; `.Designer.cs` icindeki
`[Migration(...)]` attribute'u da ayni ID'yi kullanmalidir.

Run:

```bash
dotnet test test/BBT.Workflow.Domain.Tests/BBT.Workflow.Domain.Tests.csproj \
  --filter "FullyQualifiedName~InstanceCorrelationTests"
```

Expected: `SubItemTerminalOutcome`, `TerminalOutcomeApplyResult` ve `ApplyTerminalOutcome` bulunamadigi icin build FAIL.

- [ ] **Step 3: Outcome tiplerini ve first-writer-wins davranisini uygula**

```csharp
public enum SubItemTerminalOutcome
{
    Completed = 1,
    Faulted = 2,
    Canceled = 3
}

public enum TerminalOutcomeApplyResult
{
    Applied = 1,
    Duplicate = 2,
    Conflict = 3
}
```

`InstanceCorrelation` icindeki `Completed()` metodunu uyumluluk wrapper'i olarak tut ve yeni metodu ekle:

```csharp
public SubItemTerminalOutcome? TerminalOutcome { get; private set; }

public TerminalOutcomeApplyResult ApplyTerminalOutcome(
    SubItemTerminalOutcome outcome,
    DateTime completedAt)
{
    if (IsCompleted)
    {
        return TerminalOutcome == outcome
            ? TerminalOutcomeApplyResult.Duplicate
            : TerminalOutcomeApplyResult.Conflict;
    }

    IsCompleted = true;
    CompletedAt = completedAt;
    TerminalOutcome = outcome;
    return TerminalOutcomeApplyResult.Applied;
}

public void Completed() =>
    ApplyTerminalOutcome(SubItemTerminalOutcome.Completed, DateTime.UtcNow);

public void Revert()
{
    IsCompleted = false;
    CompletedAt = null;
    TerminalOutcome = null;
}
```

`CreateSnapshot()` icinde `TerminalOutcome` kopyala. `Instance.CompleteCorrelation` overload'unu outcome ve timestamp alacak sekilde genislet; mevcut iki argumansiz davranisi `Completed` sonucuna delege et.

- [ ] **Step 4: Domain testlerini calistir**

```bash
dotnet test test/BBT.Workflow.Domain.Tests/BBT.Workflow.Domain.Tests.csproj \
  --filter "FullyQualifiedName~InstanceCorrelationTests"
```

Expected: PASS; eski `Completed()` ve `Revert()` testleri de green.

- [ ] **Step 5: EF mapping ve migration uret**

`InstancesModelCreatingExtensions.cs`:

```csharp
b.Property(p => p.TerminalOutcome)
    .HasConversion<int?>()
    .HasComment("Completed=1, Faulted=2, Canceled=3; null for legacy rows");
```

Run:

```bash
dotnet ef migrations add InstanceCorrelationTerminalOutcome \
  --project src/BBT.Workflow.Infrastructure/BBT.Workflow.Infrastructure.csproj \
  --context WorkflowDbContext
```

Generated `Up` migration'in `InstancesCorrelations` tablosuna nullable `integer` `TerminalOutcome` ekledigini, `Down` metodunun yalniz bu kolonu kaldirdigini dogrula. Existing satirlar icin backfill SQL ekleme.

- [ ] **Step 6: Migration modelini ve domain testlerini dogrula**

```bash
dotnet build src/BBT.Workflow.Infrastructure/BBT.Workflow.Infrastructure.csproj
dotnet test test/BBT.Workflow.Domain.Tests/BBT.Workflow.Domain.Tests.csproj \
  --filter "FullyQualifiedName~InstanceCorrelationTests|FullyQualifiedName~InstanceTests"
```

Expected: iki komut da exit code 0.

- [ ] **Step 7: Task 1 commit'i**

```bash
git add src/BBT.Workflow.Domain/Instances/SubItemTerminalOutcome.cs \
  src/BBT.Workflow.Domain/Instances/TerminalOutcomeApplyResult.cs \
  src/BBT.Workflow.Domain/Instances/InstanceCorrelation.cs \
  src/BBT.Workflow.Domain/Instances/Instance.cs \
  src/BBT.Workflow.Infrastructure/Data/InstancesModelCreatingExtensions.cs \
  src/BBT.Workflow.Infrastructure/Migrations/20260716210000_InstanceCorrelationTerminalOutcome.cs \
  src/BBT.Workflow.Infrastructure/Migrations/20260716210000_InstanceCorrelationTerminalOutcome.Designer.cs \
  src/BBT.Workflow.Infrastructure/Migrations/WorkflowDbContextModelSnapshot.cs \
  test/BBT.Workflow.Domain.Tests/Instances/InstanceCorrelationTests.cs \
  test/BBT.Workflow.Domain.Tests/Instances/InstanceTests.cs
git commit -m "feat(instances): persist subitem terminal outcomes"
```

---

### Task 2: Termination context'i execution pipeline ve event contract'larina ekle

**Files:**
- Create: `src/BBT.Workflow.Events.Contracts/Instances/Events/TerminationOrigin.cs`
- Create: `src/BBT.Workflow.Events.Contracts/Instances/Events/TerminationContext.cs`
- Create: `src/BBT.Workflow.Events.Contracts/Instances/Events/SubItemType.cs`
- Create: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCanceledEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubFaultedEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/ChildSubflowCancelRequestedEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/ChildSubflowFaultRequestedEvent.cs`
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/WorkflowExecutionContext.cs`
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs`
- Modify: `src/BBT.Workflow.Application/Instances/DTOs/TransitionInput.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Factory/TransitionContextFactory.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Continuations/InlineContinuationStrategy.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Factory/TransitionContextFactoryTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Continuations/InlineContinuationStrategyTests.cs`

**Interfaces:**
- Produces: immutable `TerminationContext(TerminationOrigin Origin, Guid InitiatorInstanceId, Guid CascadeId)` with `Direct(Guid)` and `AsParentCascade()` helpers.
- Produces: nullable `TerminationContext` on request and transition contexts; normal API behavior remains null until terminal operation creates a direct context.
- Produces: additive fault/downward event properties; legacy fault missing `SubItemType` resolves to `SubFlow` in consumers.

- [ ] **Step 1: Context propagation testini yaz**

`TransitionContextFactoryTests` icinde bir `WorkflowExecutionContext.Termination` degerinin uretilen `TransitionExecutionContext` icinde ayni kaldigini test et. Inline continuation testinde de ayni `CascadeId` ve `InitiatorInstanceId` iletildigini assert et.

```csharp
var termination = new TerminationContext(
    TerminationOrigin.ParentCascade,
    Guid.NewGuid(),
    Guid.NewGuid());
input.Termination = termination;

var result = await sut.CreateAsync(input, CancellationToken.None);

result.Value!.Termination.ShouldBe(termination);
```

- [ ] **Step 2: Failing testleri calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~TransitionContextFactoryTests|FullyQualifiedName~InlineContinuationStrategyTests"
```

Expected: `TerminationContext` ve `Termination` property'leri bulunmadigi icin FAIL.

- [ ] **Step 3: Contract tiplerini ekle**

```csharp
public enum TerminationOrigin { Direct = 1, ParentCascade = 2 }
public enum SubItemType { SubFlow = 1, SubProcess = 2 }

public sealed record TerminationContext(
    TerminationOrigin Origin,
    Guid InitiatorInstanceId,
    Guid CascadeId)
{
    public static TerminationContext Direct(Guid instanceId) =>
        new(TerminationOrigin.Direct, instanceId, Guid.NewGuid());

    public TerminationContext AsParentCascade() => this with
    {
        Origin = TerminationOrigin.ParentCascade
    };
}
```

`InstanceSubCanceledEvent` event adini `instance.sub.canceled` yap; parent/child identity,
domain/flow/version, `CanceledState`, `CanceledAt`, `RootInstanceId`, `SubItemType`, `Sync` ve
flat `TerminationOrigin`, `InitiatorInstanceId`, `CascadeId` alanlarini ekle. Fault event'e additive
nullable `SubItemType?`, `TerminationOrigin?`, `Guid? InitiatorInstanceId`, `Guid? CascadeId`
ekle; null type legacy SubFlow demektir. Downward event'lerde typed
`TerminationContext Termination` required olsun.

- [ ] **Step 4: Context'i pipeline boyunca typed property ile tasi**

`TransitionInput`, `WorkflowExecutionContext` ve `TransitionExecutionContext`:

```csharp
public TerminationContext? Termination { get; set; }
```

`TransitionInput.ToExecutionContext`, `TransitionContextFactory.BuildExecutionContext` ve `InlineContinuationStrategy.CreateNextWorkflowContext` icinde property'yi birebir kopyala. Business karari icin header okumasi ekleme.

- [ ] **Step 5: Context testlerini calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~TransitionContextFactoryTests|FullyQualifiedName~InlineContinuationStrategyTests"
```

Expected: PASS.

- [ ] **Step 6: Task 2 commit'i**

```bash
git add src/BBT.Workflow.Events.Contracts/Instances/Events/TerminationOrigin.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/TerminationContext.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/SubItemType.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCanceledEvent.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubFaultedEvent.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/ChildSubflowCancelRequestedEvent.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/ChildSubflowFaultRequestedEvent.cs \
  src/BBT.Workflow.Domain/Execution/Transitions/Context/WorkflowExecutionContext.cs \
  src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs \
  src/BBT.Workflow.Application/Instances/DTOs/TransitionInput.cs \
  src/BBT.Workflow.Application/Execution/Transitions/Factory/TransitionContextFactory.cs \
  src/BBT.Workflow.Application/Execution/Transitions/Continuations/InlineContinuationStrategy.cs \
  test/BBT.Workflow.Application.Tests/Execution/Transitions/Factory/TransitionContextFactoryTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Transitions/Continuations/InlineContinuationStrategyTests.cs
git commit -m "feat(instances): add termination cascade context"
```

---

### Task 3: Domain fault ve cancel producer'larini no-bounce kuraliyla genislet

**Files:**
- Modify: `src/BBT.Workflow.Domain/Instances/Instance.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/HandleFinishStep.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Instances/InstanceTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/HandleFinishStepTests.cs`

**Interfaces:**
- Consumes: `TerminationContext`, `SubItemType`, `SubItemTerminalOutcome` from Tasks 1-2.
- Produces: `Instance.Fault(string domain, bool sync = false, TerminationContext? termination = null)`.
- Produces: `Instance.Cancel(string domain, bool sync = false, TerminationContext? termination = null)`.

- [ ] **Step 1: Domain producer regression testlerini yaz**

`InstanceTests` icinde su case'leri ayri testler olarak ekle:

```csharp
[Theory]
[InlineData(WorkflowType.SubFlow.Code, SubItemType.SubFlow)]
[InlineData(WorkflowType.SubProcess.Code, SubItemType.SubProcess)]
public void Fault_DirectSubItem_ShouldPublishUpwardEvent(string flowType, SubItemType expectedType)
{
    var child = CreateSubItem(flowType);
    child.Fault("child-domain");

    var message = child.GetDomainEvents().Select(x => x.Event)
        .OfType<InstanceSubFaultedEvent>().Single();
    message.SubItemType.ShouldBe(expectedType);
    message.TerminationOrigin.ShouldBe(TerminationOrigin.Direct);
}

[Fact]
public void Cancel_ParentCascadeSubItem_ShouldNotPublishUpwardEvent()
{
    var child = CreateSubItem(WorkflowType.SubFlow.Code);
    child.Cancel("child-domain", termination: new TerminationContext(
        TerminationOrigin.ParentCascade, Guid.NewGuid(), Guid.NewGuid()));

    child.GetDomainEvents().Select(x => x.Event)
        .OfType<InstanceSubCanceledEvent>().ShouldBeEmpty();
}
```

Ek olarak direct SubFlow/SubProcess cancel event'i, ParentCascade fault suppression, child correlation'larin `Canceled` outcome ile kapatilmasi ve nested downward event'lerde tek cascade ID korunmasi testlerini ekle.

Test sinifina su helper'i ekle:

```csharp
private static Instance CreateSubItem(string flowType)
{
    var instance = InstanceFactory.CreateDefault();
    instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = flowType;
    instance.ExtraProperties[DomainConsts.MetaDataKeys.Id] = Guid.NewGuid().ToString();
    instance.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = "parent-domain";
    instance.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "parent-flow";
    instance.ExtraProperties[DomainConsts.MetaDataKeys.Version] = "1.0.0";
    return instance;
}
```

- [ ] **Step 2: Producer testlerinin fail ettigini dogrula**

```bash
dotnet test test/BBT.Workflow.Domain.Tests/BBT.Workflow.Domain.Tests.csproj \
  --filter "FullyQualifiedName~InstanceTests"
```

Expected: SubProcess fault event'i ve SubItem cancel event'i eksik oldugu icin FAIL.

- [ ] **Step 3: Fault producer'ini genislet**

`Fault` basinda effective context olustur:

```csharp
var effectiveTermination = termination ?? TerminationContext.Direct(Id);
var childTermination = effectiveTermination.AsParentCascade();
```

- Active blocking SubFlow downward fault event'lerine `Termination = childTermination` yaz.
- Upward kosulunu `IsSubFlow` yerine `IsSubItem && effectiveTermination.Origin == Direct` yap.
- SubFlow icin mevcut data/incident alanlarini koru.
- SubProcess icin `InstanceData` ve incident alanlarini null birak, `SubItemType = SubProcess` yaz.

- [ ] **Step 4: Cancel producer'ini genislet**

- `sync` ve `termination` parametrelerini ekle.
- Direct SubItem icin `InstanceSubCanceledEvent` uret.
- Her active correlation'i ayni `CanceledAt` ile `Canceled` outcome'a geçir.
- Tum SubFlow ve SubProcess child cancel request'lerine `effectiveTermination.AsParentCascade()` ekle.
- ParentCascade SubItem icin upward canceled event uretme.

`HandleFinishStep.UpdateInstanceStatus` cagrisi:

```csharp
context.Instance.Cancel(
    context.Domain,
    context.CallerMode == ExecMode.Sync,
    context.Termination);
```

- [ ] **Step 5: Domain ve finish-step testlerini calistir**

```bash
dotnet test test/BBT.Workflow.Domain.Tests/BBT.Workflow.Domain.Tests.csproj \
  --filter "FullyQualifiedName~InstanceTests"
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~HandleFinishStepTests"
```

Expected: PASS; direct ve cascade producer davranislari ayrisiyor.

- [ ] **Step 6: Task 3 commit'i**

```bash
git add src/BBT.Workflow.Domain/Instances/Instance.cs \
  src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/HandleFinishStep.cs \
  test/BBT.Workflow.Domain.Tests/Instances/InstanceTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/HandleFinishStepTests.cs
git commit -m "feat(instances): propagate terminal child events without bounce"
```

---

### Task 4: Parent fault handling'i SubProcess ve terminal conflict semantigine uyarla

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/DTOs/SubFlowFaultedInput.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubFaultedEventHook.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubFaultedEventHandler.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs`

**Interfaces:**
- Consumes: event `SubItemType?`; null maps to legacy `SubFlow` only at adapter boundary.
- Produces: `SubFlowFaultedInput.SubItemType` and `Termination` properties.
- Produces: SubProcess fault handling that commits only state/timestamp/outcome.

- [ ] **Step 1: Failing parent fault testlerini yaz**

```csharp
[Fact]
public async Task FaultAsync_SubProcess_ShouldOnlyCloseCorrelation()
{
    var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubProcess);
    var input = CreateInput(parent.Id, subInstanceId, CreateJsonElement("{}")) with
    {
        SubItemType = SubItemType.SubProcess
    };

    await CreateService().FaultAsync(input);

    var correlation = parent.FindCorrelationBySubInstanceId(input.SubInstanceId)!;
    correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
    parent.Status.ShouldBe(InstanceStatus.Active);
    parent.Incidents.ShouldBeEmpty();
    _outputMapping.VerifyNoOtherCalls();
    _workflowExecution.VerifyNoOtherCalls();
}
```

Mevcut `CreateParentInstance` helper'ina opsiyonel type ekle ve correlation creation'da kullan:

```csharp
private static Instance CreateParentInstance(
    out Guid subInstanceId,
    SubFlowType? subFlowType = null)
{
    subInstanceId = Guid.NewGuid();
    var parent = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
    parent.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
    parent.SetEffectiveState("child-active");
    parent.AddCorrelation(InstanceCorrelation.Create(
        Guid.NewGuid(), parent.Id, "waiting-child", subInstanceId,
        (subFlowType ?? SubFlowType.SubFlow).Code,
        "bank", "child-flow", "1.0.0"));
    return parent;
}
```

Ayni-outcome duplicate no-op, farkli outcome conflict no-op, terminal parent no-op ve legacy null type + stored SubFlow case'lerini ekle. Conflict testinde mapping/boundary/resume cagrisi olmadigini assert et.

- [ ] **Step 2: Fault service testlerini fail durumda calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~SubflowFaultServiceTests"
```

Expected: SubProcess correlation fault outcome'u kaydedilmedigi ve servis blocking akisa girdigi icin FAIL.

- [ ] **Step 3: Common guard ve stored-type branch uygula**

`RequiresNew` UOW icinde correlation'i yükledikten sonra:

```csharp
if (correlation.IsCompleted)
{
    if (correlation.TerminalOutcome != SubItemTerminalOutcome.Faulted)
        logger.SubItemTerminalConflict(
            input.InstanceId,
            input.SubInstanceId,
            correlation.TerminalOutcome?.ToString() ?? "legacy",
            SubItemTerminalOutcome.Faulted.ToString());
    await uow.CommitAsync(cancellationToken);
    return;
}

correlation.UpdateSubFlowState(input.FaultedState, input.FaultedAt);
parentInstance.CompleteCorrelation(
    input.SubInstanceId,
    SubItemTerminalOutcome.Faulted,
    input.FaultedAt);

if (correlation.SubFlowType.Equals(SubFlowType.SubProcess))
{
    await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
    await uow.CommitAsync(cancellationToken);
    return;
}
```

Blocking SubFlow yolunda mevcut incident/boundary/mapping/fault/transition/resume davranisini koru. Parent karari icin `input.SubItemType` kullanma.

- [ ] **Step 4: Hook ve inbox mapper'larini additive contract'a uyarla**

Her iki mapper'da:

```csharp
SubItemType = eventData.SubItemType ?? SubItemType.SubFlow,
Termination = eventData.CascadeId.HasValue && eventData.InitiatorInstanceId.HasValue
    ? new TerminationContext(
        eventData.TerminationOrigin ?? TerminationOrigin.Direct,
        eventData.InitiatorInstanceId.Value,
        eventData.CascadeId.Value)
    : null
```

`SubFlowFaultedInput` icine ayrica `Guid? RootInstanceId` ekle ve iki mapper'da
`RootInstanceId = eventData.RootInstanceId` ile tasi.

Inbox retry yolunda `Sync = false` davranisini koru.

- [ ] **Step 5: Fault testlerini ve mevcut regression setini calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~SubflowFaultServiceTests"
```

Expected: tum `SubflowFaultServiceTests` PASS.

- [ ] **Step 6: Task 4 commit'i**

```bash
git add src/BBT.Workflow.Application/SubFlow/DTOs/SubFlowFaultedInput.cs \
  src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs \
  src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubFaultedEventHook.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubFaultedEventHandler.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs
git commit -m "feat(subflow): handle subprocess fault outcomes"
```

---

### Task 5: Parent cancel service'ini ve blocking SubFlow resume davranisini ekle

**Files:**
- Create: `src/BBT.Workflow.Application/SubFlow/DTOs/SubItemCanceledInput.cs`
- Create: `src/BBT.Workflow.Application/SubFlow/Contracts/ISubflowCancellationService.cs`
- Create: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCancellationService.cs`
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/WorkflowApplicationModuleServiceCollectionExtensions.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCancellationServiceTests.cs`

**Interfaces:**
- Produces: `Task CancellationAsync(SubItemCanceledInput input, CancellationToken cancellationToken = default)`.
- Consumes: `IInstanceRepository`, `IComponentCacheStore`, `IWorkflowExecutionService`, `IUnitOfWorkManager`.
- Produces: blocking cancel Phase 1 commit + Phase 2 resume + hard-failure revert.

- [ ] **Step 1: Cancel behavior testlerini yaz**

Asagidaki testleri yeni test sinifina ekle:

```csharp
[Fact]
public async Task CancellationAsync_BlockingSubFlow_ShouldCommitThenResumeWithoutMappingOrIncident()
{
    var parent = CreateParentInstance(out var subInstanceId, SubFlowType.SubFlow);
    var input = CreateCanceledInput(parent.Id, subInstanceId);

    await CreateService().CancellationAsync(input);

    parent.FindCorrelationBySubInstanceId(input.SubInstanceId)!
        .TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
    parent.Incidents.ShouldBeEmpty();
    _workflowExecution.Verify(x => x.ExecuteTransitionAsync(
        It.Is<WorkflowExecutionContext>(c =>
            c.Mode == ExecMode.Resume &&
            c.Execution!.ResumeFrom == LifecycleOrder.ClearBusyOnResumeStep &&
            c.Execution.IsSubFlowResume &&
            c.Execution.SubFlowResumeInstanceId == input.SubInstanceId),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

Yeni test sinifina parent ve input helper'larini ekle:

```csharp
private static SubItemCanceledInput CreateCanceledInput(Guid parentId, Guid childId) => new()
{
    InstanceId = parentId,
    SubInstanceId = childId,
    Domain = "bank",
    Flow = "parent-flow",
    Version = "1.0.0",
    CanceledState = "child-canceled",
    CanceledAt = DateTime.UtcNow,
    Termination = TerminationContext.Direct(childId)
};

private static Instance CreateParentInstance(
    out Guid subInstanceId,
    SubFlowType? subFlowType = null)
{
    subInstanceId = Guid.NewGuid();
    var parent = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
    parent.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
    parent.SetEffectiveState("child-active");
    parent.AddCorrelation(InstanceCorrelation.Create(
        Guid.NewGuid(), parent.Id, "waiting-child", subInstanceId,
        (subFlowType ?? SubFlowType.SubFlow).Code,
        "bank", "child-flow", "1.0.0"));
    return parent;
}
```

SubProcess yalniz correlation kapatir, duplicate no-op, conflict no-op, terminal parent no-op, `AutoTransitionConditionNotMet`/`InstanceCompleted` soft success ve hard error sonrasinda `FindWithAllCorrelationsAsync` + revert testlerini ekle.

- [ ] **Step 2: Yeni testlerin fail ettigini dogrula**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~SubflowCancellationServiceTests"
```

Expected: service/contract bulunmadigi icin build FAIL.

- [ ] **Step 3: DTO ve service common phase'ini uygula**

`SubItemCanceledInput` alanlari:

```csharp
public required Guid InstanceId { get; init; }
public required Guid SubInstanceId { get; init; }
public required string Domain { get; init; }
public required string Flow { get; init; }
public string? Version { get; init; }
public required string CanceledState { get; init; }
public required DateTime CanceledAt { get; init; }
public Guid? RootInstanceId { get; init; }
public bool Sync { get; init; }
public TerminationContext? Termination { get; init; }
```

Service common phase'inde parent terminal ise return et. Correlation bulunamazsa not-found loglayip
return et. Correlation completed ise `TerminalOutcome == Canceled` durumunu duplicate debug,
diger outcome'lari conflict warning olarak loglayip return et. Yeni outcome icin state'i update et:

```csharp
correlation.UpdateSubFlowState(input.CanceledState, input.CanceledAt);
parentInstance.CompleteCorrelation(
    input.SubInstanceId,
    SubItemTerminalOutcome.Canceled,
    input.CanceledAt);
await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
await uow.CommitAsync(cancellationToken);
```

Stored type `SubProcess` ise commit sonrasinda return et.

- [ ] **Step 4: Blocking SubFlow post-commit resume ve revert uygula**

Parent workflow'u Phase 1 icinde cache'ten yukle; mapping ve incident servisi inject etme. Commit sonrasinda su context ile resume et:

```csharp
new WorkflowExecutionContext
{
    Domain = parentWorkflow.Domain,
    WorkflowKey = parentWorkflow.Key,
    WorkflowVersion = parentWorkflow.Version,
    InstanceId = parentInstance.Id.ToString(),
    TransitionKey = string.Empty,
    TriggerType = TriggerType.Automatic,
    Mode = ExecMode.Resume,
    CallerMode = input.Sync ? ExecMode.Sync : ExecMode.Async,
    Actor = ExecutionActor.System,
    RequestedAt = DateTimeOffset.UtcNow,
    Execution = new ExecutionInfo
    {
        ExecutionChainId = Guid.NewGuid().ToString("N"),
        ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep,
        IsSubFlowResume = true,
        SubFlowResumeInstanceId = input.SubInstanceId
    }
};
```

Hard result/exception'da `RequiresNew` UOW ac, `FindWithAllCorrelationsAsync` ile reload et, `RevertCorrelation` + update + commit yap ve original failure'i tekrar firlat.

- [ ] **Step 5: DI kaydini ve testleri tamamla**

```csharp
services.AddScoped<ISubflowCancellationService, SubflowCancellationService>();
```

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~SubflowCancellationServiceTests"
```

Expected: PASS.

- [ ] **Step 6: Task 5 commit'i**

```bash
git add src/BBT.Workflow.Application/SubFlow/DTOs/SubItemCanceledInput.cs \
  src/BBT.Workflow.Application/SubFlow/Contracts/ISubflowCancellationService.cs \
  src/BBT.Workflow.Application/SubFlow/Services/SubflowCancellationService.cs \
  src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/WorkflowApplicationModuleServiceCollectionExtensions.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowCancellationServiceTests.cs
git commit -m "feat(subflow): handle parent cancellation outcomes"
```

---

### Task 6: Cancel upward routing ve nested parent-cascade tasimasini tamamla

**Files:**
- Modify: `src/BBT.Workflow.Application/Gateway/IInstanceCommandGateway.cs`
- Modify: `src/BBT.Workflow.Application/Instances/Remote/IRemoteInstanceCommandAppService.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceCommandGateway.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Gateway/RemoteInstanceCommandGateway.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceCommandGateway.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceCommandAppService.cs`
- Modify: `src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs`
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs`
- Create: `src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubCanceledEventHook.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/WorkflowInfrastructureModuleServiceCollectionExtensions.cs`
- Create: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCanceledEventHandler.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/ChildSubflowCancelRequestedEventHandler.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/ChildSubflowFaultRequestedEventHandler.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Contracts/IChildSubflowCancellationService.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/ChildSubflowCancellationService.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Contracts/IChildSubflowFaultService.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/ChildSubflowFaultService.cs`
- Create: `test/BBT.Workflow.Infrastructure.Tests/Gateway/RoutedInstanceCommandGatewayTerminalTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/SubFlow/ChildSubflowCancellationServiceTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/SubFlow/ChildSubflowFaultServiceTests.cs`

**Interfaces:**
- Produces: `IInstanceCommandGateway.CancelAsync(SubItemCanceledInput, CancellationToken)`.
- Produces: internal HTTP endpoint `POST .../instances/{instance}/sub/cancel`.
- Produces: child cancel/fault services accepting required `TerminationContext` from downward events.

- [ ] **Step 1: Routing ve child context testlerini yaz**

Routed gateway testinde local domain'in local gateway'e, remote domain'in remote gateway'e tam `SubItemCanceledInput` ile gittigini test et. Child cancel testinde gateway'e giden `TransitionInput.Termination` degerini assert et. Child fault testinde `childInstance.Fault` sonucu uretilen nested downward event'in ayni cascade ID'yi tasidigini assert et.

```csharp
await sut.CancelChildSubflowAsync(instanceId, domain, flow, version, termination, ct);

await gateway.Received(1).TransitionAsync(
    instanceId,
    WellKnownTransitionKeys.Cancel,
    Arg.Is<TransitionInput>(x => x.Termination == termination),
    ct);
```

- [ ] **Step 2: Testleri fail durumda calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~ChildSubflowCancellationServiceTests|FullyQualifiedName~ChildSubflowFaultServiceTests"
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~RoutedInstanceCommandGatewayTerminalTests"
```

Expected: yeni method imzalari eksik oldugu icin FAIL.

- [ ] **Step 3: Gateway ve HTTP cancel rotasini ekle**

Tum gateway katmanlarina su imzayi ekle:

```csharp
Task<Result> CancelAsync(
    SubItemCanceledInput input,
    CancellationToken cancellationToken = default);
```

Local gateway `ISubflowCancellationService.CancellationAsync` cagirir. Remote app service `InstanceUrlTemplates.SubFlowCancel(...)` adresine JSON body post eder. Controller ayni body'yi `ISubflowCancellationService`'e verir.

- [ ] **Step 4: Canceled hook ve inbox handler'i ekle**

Hook `IInstanceCommandGateway.CancelAsync` kullanir ve completion/fault hook'larindaki telemetry scope yapisini korur. Inbox handler domain guard sonrasi `SubItemCanceledInput` olusturur, fakat at-least-once worker yolunda `Sync = false` set eder ve `/sub/cancel` endpoint'ine forward eder.

Canceled event'in flat termination alanlarini typed application context'e map et:

```csharp
Termination = new TerminationContext(
    eventData.TerminationOrigin,
    eventData.InitiatorInstanceId,
    eventData.CascadeId),
RootInstanceId = eventData.RootInstanceId
```

DI:

```csharp
services.AddEventHook<InstanceSubCanceledEvent, InstanceSubCanceledEventHook>();
```

- [ ] **Step 5: Downward context'i child operation'a gecir**

`IChildSubflowCancellationService.CancelChildSubflowAsync` ve `IChildSubflowFaultService.FaultChildAsync` imzalarina `TerminationContext termination` ekle. Cancel service `TransitionInput.Termination = termination`; fault service `childInstance.Fault(domain, termination: termination)` kullanir. Controller downward endpoint'lerinde body DTO kullanarak context'i query-string'e koymadan typed JSON ile al.

Inbox downward handler'lari event'teki required `Termination` degerini body'ye koyar. Boylece nested child operation `ParentCascade` origin'i korur ve upward event uretmez.

- [ ] **Step 6: Routing testlerini calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~ChildSubflowCancellationServiceTests|FullyQualifiedName~ChildSubflowFaultServiceTests"
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~RoutedInstanceCommandGatewayTerminalTests"
```

Expected: PASS.

- [ ] **Step 7: Task 6 commit'i**

```bash
git add src/BBT.Workflow.Application/Gateway/IInstanceCommandGateway.cs \
  src/BBT.Workflow.Application/Instances/Remote/IRemoteInstanceCommandAppService.cs \
  src/BBT.Workflow.Application/SubFlow/Contracts/IChildSubflowCancellationService.cs \
  src/BBT.Workflow.Application/SubFlow/Contracts/IChildSubflowFaultService.cs \
  src/BBT.Workflow.Application/SubFlow/Services/ChildSubflowCancellationService.cs \
  src/BBT.Workflow.Application/SubFlow/Services/ChildSubflowFaultService.cs \
  src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceCommandGateway.cs \
  src/BBT.Workflow.Infrastructure/Gateway/RemoteInstanceCommandGateway.cs \
  src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceCommandGateway.cs \
  src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceCommandAppService.cs \
  src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubCanceledEventHook.cs \
  src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/WorkflowInfrastructureModuleServiceCollectionExtensions.cs \
  src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs \
  orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCanceledEventHandler.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/ChildSubflowCancelRequestedEventHandler.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/ChildSubflowFaultRequestedEventHandler.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/ChildSubflowCancellationServiceTests.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/ChildSubflowFaultServiceTests.cs \
  test/BBT.Workflow.Infrastructure.Tests/Gateway/RoutedInstanceCommandGatewayTerminalTests.cs
git commit -m "feat(subflow): route canceled outcomes and cascade context"
```

---

### Task 7: Terminal hook'lari durable post-commit moda al

**Files:**
- Modify: `src/BBT.Workflow.Events.Contracts/Events/Hooks/EventHookAttribute.cs`
- Create: `src/BBT.Workflow.Events.Contracts/Events/Hooks/EventHookMode.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCompletedEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubFaultedEvent.cs`
- Modify: `src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCanceledEvent.cs`
- Modify: `src/BBT.Workflow.Infrastructure/EventBus/HookedDistributedEventBus.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/EventBusHookServiceCollectionExtensions.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`
- Create: `test/BBT.Workflow.Infrastructure.Tests/EventBus/HookedDistributedEventBusTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerEventDurabilityTests.cs`

**Interfaces:**
- Produces: `EventHookMode.HandledOrFallback` default ve `EventHookMode.DurablePostCommit` opt-in.
- Consumes: `IUnitOfWorkManager.Current.OnCompleted(...)`.
- Guarantees: durable modda inner bus once-before-hook; hook basarisi outbox publish'i suppress etmez.

- [ ] **Step 1: Event bus siralama testlerini yaz**

Yeni infrastructure testinde fake inner bus, fake hook invoker ve controllable UOW ile su sirayi assert et:

```csharp
await sut.PublishAsync(terminalEvent);

calls.ShouldBe(new[] { "inner" });
await uow.CommitAsync();
calls.ShouldBe(new[] { "inner", "hook" });
```

Ayrica hook success halinde `inner` bir kez; hook failure halinde yine bir kez; UOW yokken `inner` sonra `hook`; default modda mevcut `hook success => inner yok` davranisi testlerini ekle.

- [ ] **Step 2: TransitionRunner staging failure testini yaz**

Event bus publish'i exception attiginda runner'in basarili sonuc donmedigini ve UOW commit edilmedigini assert et. Basarili staging'de publish'in commit'ten once cagrildigini assert et.

- [ ] **Step 3: Testleri fail durumda calistir**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~HookedDistributedEventBusTests"
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~TransitionRunnerEventDurabilityTests"
```

Expected: hook mode yok ve TransitionRunner exception'i swallow ettigi icin FAIL.

- [ ] **Step 4: Attribute mode'unu ekle ve terminal event'lerde opt-in yap**

```csharp
public enum EventHookMode
{
    HandledOrFallback = 1,
    DurablePostCommit = 2
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EventHookAttribute(
    EventHookMode mode = EventHookMode.HandledOrFallback) : Attribute
{
    public EventHookMode Mode { get; } = mode;
}
```

Uc terminal event'te `[EventHook(EventHookMode.DurablePostCommit)]` kullan. Diger event'lerin default davranisini degistirme.

- [ ] **Step 5: Durable publish algoritmasini uygula**

`HookedDistributedEventBus` constructor'ina `IUnitOfWorkManager` ekle. `EventHookAttributeCache`
yerine `ConcurrentDictionary<Type, EventHookMode?>` kullan. Her iki `PublishAsync` overload'unda
attribute mode'a gore branch et. Durable branch:

```csharp
await _inner.PublishAsync(payload, subject, useOutbox, cancellationToken);

var ambient = _unitOfWorkManager.Current;
if (ambient is null)
{
    await ExecutePostCommitHooksSafelyAsync(payload);
    return;
}

ambient.OnCompleted(_ => ExecutePostCommitHooksSafelyAsync(payload));
```

`ExecutePostCommitHooksSafelyAsync` hook failure/exception'ini loglar ama throw etmez; committed child state'i basarisiz gostermemelidir. Callback `CancellationToken.None` kullanir. Inner publish exception'ini catch etme; outbox staging failure transition'i fail etmelidir.

Decorator factory yeni dependency'yi acikca resolve etsin:

```csharp
var uowManager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
return new HookedDistributedEventBus(inner, serviceProvider, uowManager, logger);
```

- [ ] **Step 6: TransitionRunner'da staging exception'ini swallow etme**

`PublishDeferredEventsAsync` icindeki per-event `try/catch` bloğunu kaldir. XML comment'i gercek siraya gore duzelt: deferred events UOW commit'ten once outbox'a stage edilir; durable hooks UOW completion callback'inde calisir.

- [ ] **Step 7: Durability testlerini calistir**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~HookedDistributedEventBusTests"
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~TransitionRunnerEventDurabilityTests"
```

Expected: PASS; `inner -> commit -> hook` sirasi kanitlanir.

- [ ] **Step 8: Task 7 commit'i**

```bash
git add src/BBT.Workflow.Events.Contracts/Events/Hooks/EventHookAttribute.cs \
  src/BBT.Workflow.Events.Contracts/Events/Hooks/EventHookMode.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCompletedEvent.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubFaultedEvent.cs \
  src/BBT.Workflow.Events.Contracts/Instances/Events/InstanceSubCanceledEvent.cs \
  src/BBT.Workflow.Infrastructure/EventBus/HookedDistributedEventBus.cs \
  src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/EventBusHookServiceCollectionExtensions.cs \
  src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs \
  test/BBT.Workflow.Infrastructure.Tests/EventBus/HookedDistributedEventBusTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerEventDurabilityTests.cs
git commit -m "fix(events): persist terminal events before post-commit hooks"
```

---

### Task 8: Completion yolunu outcome-aware idempotency ile hizala

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/DTOs/FlowCompletedInput.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs`

**Interfaces:**
- Consumes: `SubItemTerminalOutcome.Completed` ve correlation conflict semantigi.
- Preserves: SubProcess completion yalniz correlation kapatir; blocking SubFlow output mapping + resume eder.

- [ ] **Step 1: Completion duplicate/conflict testlerini yaz**

```csharp
[Fact]
public async Task CompletionAsync_WhenCorrelationAlreadyCanceled_ShouldNotMapOrResume()
{
    var parent = CreateParentInstance(out var subInstanceId);
    parent.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Canceled, DateTime.UtcNow);

    await CreateService().CompletionAsync(CreateInput(parent.Id, subInstanceId));

    parent.FindCorrelationBySubInstanceId(subInstanceId)!
        .TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Canceled);
    _outputMapping.VerifyNoOtherCalls();
    _workflowExecution.VerifyNoOtherCalls();
}
```

Ayni `Completed` duplicate no-op ve legacy `IsCompleted=true/TerminalOutcome=null` no-op testlerini ekle.

- [ ] **Step 2: Completion testlerini fail durumda calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~SubflowCompletionServiceTests"
```

Expected: yeni conflict testi mevcut generic completed guard nedeniyle sonucu ayirt edemedigi/loglamadigi icin FAIL.

- [ ] **Step 3: Completion common guard'ini outcome-aware yap**

- Correlation null ise mevcut not-found sonucu koru.
- `IsCompleted && TerminalOutcome == Completed` duplicate debug + return.
- `IsCompleted && TerminalOutcome != Completed` conflict warning + return; null legacy outcome'u overwrite etme.
- Yeni completion'da `CompleteCorrelation(subId, Completed, completedInput.CompletedAt)` kullan.
- `FlowCompletedInput` icine nullable `RootInstanceId` ekle; completion hook ve inbox mapper'larinda
  event'teki degeri tasi.
- SubProcess ve blocking SubFlow davranislarini degistirme.

- [ ] **Step 4: Completion ve fault/cancel service regression testlerini calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~SubflowCompletionServiceTests|FullyQualifiedName~SubflowFaultServiceTests|FullyQualifiedName~SubflowCancellationServiceTests"
```

Expected: PASS.

- [ ] **Step 5: Task 8 commit'i**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs \
  src/BBT.Workflow.Application/SubFlow/DTOs/FlowCompletedInput.cs \
  src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubCompletedEventHook.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCompletedEventHandler.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs
git commit -m "fix(subflow): make completion terminal-outcome idempotent"
```

---

### Task 9: Telemetry, concurrency ve end-to-end regression kapisini tamamla

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowEventIds.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs`
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCancellationService.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubCompletedEventHook.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubFaultedEventHook.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubCanceledEventHook.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCompletedEventHandler.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubFaultedEventHandler.cs`
- Modify: `workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCanceledEventHandler.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCancellationServiceTests.cs`

**Interfaces:**
- Produces telemetry tags: root, parent, child, type, outcome, origin, initiator, cascade, domain, flow, version.
- Produces warning logs for conflict/not-found/revert failure; debug logs for duplicate.

- [ ] **Step 1: Parent lock ve first-writer invariant testlerini yaz**

Uc service test sinifina lock alinamadiginda parent'in hic yuklenmedigini kanitlayan testi ekle.
Fault service ornegi:

```csharp
[Fact]
public async Task FaultAsync_WhenParentLockCannotBeAcquired_ShouldFailBeforeRepositoryAccess()
{
    var input = CreateInput(Guid.NewGuid(), Guid.NewGuid(), CreateJsonElement("{}"));
    var scope = new Mock<ITransitionLockScope>();
    scope.SetupGet(x => x.IsAcquired).Returns(false);
    _lockScopeFactory
        .Setup(x => x.AcquireAsync(
            $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}",
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(scope.Object);

    await Should.ThrowAsync<SubflowCompletionException>(
        () => CreateService().FaultAsync(input));

    _instanceRepository.Verify(
        x => x.FindAsync(It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()),
        Times.Never);
}
```

Mevcut duplicate/conflict testlerini iki siralama ile tamamla: cancel-first sonra fault ve fault-first
sonra cancel. Her ikisinde ilk outcome korunmali; ikinci service mapping/boundary/resume yapmamalidir.
Completion service icin de onceden Faulted/Canceled correlation'in yeniden acilmadigini koru.

- [ ] **Step 2: Concurrency testini calistir**

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~SubflowCompletionServiceTests|FullyQualifiedName~SubflowFaultServiceTests|FullyQualifiedName~SubflowCancellationServiceTests"
```

Expected: eksik conflict/concurrency handling nedeniyle ilk calistirmada FAIL.

- [ ] **Step 3: Concurrency loser reload ve telemetry alanlarini uygula**

Uc parent terminal service'e `ITransitionLockScopeFactory` inject et. Parent'i yuklemeden once ayni
instance lock key'ini kullan:

```csharp
var lockKey = $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}";
await using var lockScope = await transitionLockScopeFactory.AcquireAsync(lockKey, cancellationToken);
if (!lockScope.IsAcquired)
{
    throw new SubflowCompletionException(
        input.Domain,
        input.Flow,
        input.InstanceId.ToString(),
        WorkflowErrorCodes.ConflictWorkflow,
        "Parent instance terminal lock could not be acquired.");
}
```

Lock yalniz parent common-phase UOW commit'ine kadar tutulur ve post-commit resume'dan once release
edilir. Resume ayni instance transition lock'unu yeniden alacagi icin parent lock'unu resume boyunca
tutma. Boylece competing consumer correlation'i authoritative olarak tekrar yukler; stored terminal
outcome duplicate/conflict guard'i mapping/boundary/resume'in ikinci kez calismasini engeller.

Her terminal scope/activity icin asagidaki alanlari ekle:

```csharp
[TelemetryConstants.TagNames.RootInstanceId] = input.RootInstanceId?.ToString() ?? "N/A",
[TelemetryConstants.TagNames.ParentInstanceId] = input.InstanceId,
[TelemetryConstants.TagNames.SubflowInstanceId] = input.SubInstanceId,
["vnext.subitem.type"] = storedType.Code,
["vnext.subitem.outcome"] = outcome.ToString(),
["vnext.termination.origin"] = input.Termination?.Origin.ToString() ?? "legacy",
["vnext.termination.initiator"] = input.Termination?.InitiatorInstanceId.ToString() ?? "N/A",
["vnext.termination.cascade_id"] = input.Termination?.CascadeId.ToString() ?? "N/A"
```

`FlowCompletedInput`, `SubFlowFaultedInput` ve `SubItemCanceledInput` nullable `RootInstanceId`
tasir; hook ve inbox mapper'larinda event degerini birebir aktar.

- [ ] **Step 4: Focused ve solution-level verification calistir**

```bash
dotnet test test/BBT.Workflow.Domain.Tests/BBT.Workflow.Domain.Tests.csproj \
  --filter "FullyQualifiedName~InstanceCorrelationTests|FullyQualifiedName~InstanceTests"
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter "FullyQualifiedName~Subflow|FullyQualifiedName~ChildSubflow|FullyQualifiedName~TransitionRunnerEventDurabilityTests"
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~HookedDistributedEventBusTests|FullyQualifiedName~RoutedInstanceCommandGatewayTerminalTests"
dotnet build vnext.sln
```

Expected: tum focused testler PASS ve solution build exit code 0. Repo kokunde plain `dotnet build` kullanma; birden fazla solution/project nedeniyle `MSB1011` verir.

- [ ] **Step 5: Migration ve dirty-worktree sinirini dogrula**

```bash
git diff --check
git status --short
git diff --name-only origin/claude/cherry-pick-commits-zmqhjw...HEAD
```

Expected: whitespace error yok; task disi mevcut csproj/config/`RemoteInvokerService.cs` degisiklikleri bu feature commit'lerinde yer almiyor; migration yalniz nullable `TerminalOutcome` kolonunu ekliyor.

- [ ] **Step 6: Task 9 commit'i**

```bash
git add src/BBT.Workflow.Domain/Logging/WorkflowEventIds.cs \
  src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs \
  src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs \
  src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs \
  src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs \
  src/BBT.Workflow.Application/SubFlow/Services/SubflowCancellationService.cs \
  src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubCompletedEventHook.cs \
  src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubFaultedEventHook.cs \
  src/BBT.Workflow.Infrastructure/Instances/Events/InstanceSubCanceledEventHook.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCompletedEventHandler.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubFaultedEventHandler.cs \
  workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCanceledEventHandler.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowCancellationServiceTests.cs
git commit -m "test(subflow): verify terminal propagation concurrency"
```

---

## Final acceptance checklist

- [ ] Direct SubFlow fault mevcut incident/data + boundary davranisini koruyor.
- [ ] Direct SubProcess fault parent correlation'i `Faulted` kapatiyor ve parent'i degistirmiyor.
- [ ] Direct SubFlow cancel correlation'i `Canceled` kapatip parent'i mapping/incident olmadan resume ediyor.
- [ ] Direct SubProcess cancel yalniz correlation'i `Canceled` kapatiyor.
- [ ] Parent cancel tum active SubFlow/SubProcess correlation'lari once kapatiyor, sonra tek cascade ID ile downward yayiliyor.
- [ ] Parent-cascade child fault/cancel yukari event uretmiyor.
- [ ] Uc seviyeli nested cascade initiator ve cascade ID'yi koruyor.
- [ ] Ayni terminal event duplicate'i parent pipeline'ini en fazla bir kez calistiriyor.
- [ ] Farkli terminal outcome ilk commit edilen sonucu overwrite etmiyor.
- [ ] Terminal event child transaction'indaki outbox'a hook'tan once stage ediliyor.
- [ ] Hook success outbox kaydini suppress etmiyor; hook failure inbox retry yolunu koruyor.
- [ ] Hard blocking-cancel resume failure correlation'i yeni UOW'da revert ediyor.
- [ ] Legacy fault event'i missing type ile blocking SubFlow olarak isleniyor.
- [ ] Legacy completed correlation (`TerminalOutcome = null`) yeniden acilmiyor.
- [ ] Focused testler ve `dotnet build vnext.sln` green.
