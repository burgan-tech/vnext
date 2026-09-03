# Schedule-After-Auto Pipeline Reorder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-transition değerlendirmesi Schedule'dan ÖNCE çalışsın; auto bir kazanan seçtiyse scheduled transition'lar hiç enqueue edilmesin (gereksiz arm + bir sonraki hop'ta cancel churn'ü ortadan kalksın).

**Architecture:** `LifecycleOrder` sabitlerinde Auto (90→80) ve Schedule (80→90) yer değiştirir — executor adımları `Order`'a göre sıraladığı ve tüm profil/replan mantığı isimli sabitleri kullandığı için tek otorite sabitlerdir. `ScheduleTransitionsStep`'e bir guard eklenir: `context.Directives.NextTransition` doluysa (auto kazanan seçti, instance bu state'ten hemen ayrılacak) hiçbir timer arm edilmez.

**Tech Stack:** .NET 10, xUnit + NSubstitute + Shouldly, Aether Result pattern, `WorkflowLogs.cs` source-generated logging.

**Spec:** Inline — aşağıdaki "Background & Desired Behavior" bölümü bu planın spec'idir (ayrı spec dokümanı yok; gereksinim kullanıcı tarafından doğrudan tarif edildi).

## Background & Desired Behavior (spec)

Bugünkü akış: `ScheduleTransitionsStep (80)` hedef state'in scheduled transition'larını Dapr job + `InstanceJob` satırı olarak arm eder; ardından `RunAutomaticTransitionsStep (90)` auto koşullarını değerlendirir ve kazanan varsa `Directives.RequestNextTransition` ile zincirlenir. Zincirlenen hop kendi pipeline'ında `CancelScheduledJobsStep (39)` ile az önce arm edilen timer'ları siler. Yani auto'nun kazandığı her hop'ta **boşuna bir enqueue + persist + bir sonraki hop'ta cancel** yapılır.

İstenen davranış:
1. Auto değerlendirmesi önce çalışır.
2. Auto kazanan seçtiyse (`NextTransition` directive dolu) → Schedule adımı **hiç arm etmez** (no-op, log'lanır).
3. Auto kazanan seçmediyse (koşul sağlanmadı veya state'te auto yok) → Schedule bugünkü gibi arm eder.

### Doğrulanmış kod gerçekleri (implementasyonun dayandığı)

- `TransitionExecutor` ctor'u adımları `steps.OrderBy(s => s.Order)` ile sıralar ([TransitionExecutor.cs:34](src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs)) — kayıt sırası önemsiz, sabitler tek otorite.
- Epilogue-skip ve replan mantığı `LifecycleOrder.Schedule` / `LifecycleOrder.Auto` isimli sabitlerini kullanır ([TransitionExecutor.cs:253](src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs:253), :296) — değer değişince otomatik takip eder.
- `ClearBusyOnResumeStep = Schedule - 1` ([LifecycleOrder.cs:95](src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs:95)). Subflow/long-poll resume bu sabitten başlar (`ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep`, 4 çağrı noktası). Resume epilogue'un TAMAMINDAN önce başlamalı → yeni tanım `Auto - 1` olmalı (sayısal değer 79'da kalır).
- `ResumeFrom` değerleri hiçbir yerde persist edilmez — subflow completion/fault/cancel ve long-poll ack servisleri değeri çalışma anında sabitten okur. Sabit değer değişikliği deploy-güvenlidir.
- `AllowAutoChain` profil alanı kodda **hiç tüketilmiyor** (yalnız `PipelineExecutionProfile.cs` içinde tanım/kopya). Yani `RequestNextTransition` set edilen her senaryoda continuation gerçekten dispatch edilir (Inline veya Enqueue) — "directive set edildi ama zincir koşmadı, timer da arm edilmedi" açığı yok.
- Tek `NextTransition` drop yolu updateData continuation handoff'udur ([TransitionPipeline.cs:301](src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs:301)); updateData profili (`+Self`) `Schedule`'ı zaten isimle exclude ettiğinden bu drop Schedule guard'ıyla etkileşmez.
- `SelfTargetExcluded` seti `LifecycleOrder.Schedule`'ı isimle içerir ([PipelineExecutionProfile.cs:74](src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/PipelineExecutionProfile.cs:74)) — değer değişikliğini otomatik takip eder.
- `State.ScheduledTransitions` türetilmiş property'dir: `Transitions.Where(t => t.TriggerType == TriggerType.Scheduled)` ([State.cs:263](src/BBT.Workflow.Domain/Definitions/States/State.cs:263)).

### Kabul edilen davranış değişiklikleri (bilinçli)

1. **Kazanan seçildiğinde timer'lar hiç arm edilmez.** Eski davranışta arm edilip zincirin bir sonraki hop'unda cancel ediliyordu. Zincirlenen hop fault ederse eski dünyada timer'lar armed kalırdı (faulted instance'ta işe yaramaz — scheduled execution System-actor gate + aktiflik kontrolünden döner); yeni dünyada hiç olmaz. Net iyileşme.
2. **Auto adımı fail ederse (ör. `UnhandledNonBlockingTaskFailures`) timer'lar arm edilmemiş olur.** Eskiden Auto'dan önce arm edilmişlerdi. Instance zaten fault'a gittiği için kabul.
3. Timer script'leri artık auto koşul script'lerinden SONRA çalışır. Auto değerlendirmesi instance datasını mutate etmez; timer girdileri değişmez.

## Global Constraints

- Loglama: asla raw `logger.Log*`; `WorkflowLogs.cs`'e `[LoggerMessage]` partial ekle (transition kategorisi = 10xxx EventId aralığı).
- Result pattern: adımlar `Result<StepOutcome>` döner; exception ile kontrol akışı yok.
- Breaking change YASAK (bkz. memory `no-breaking-change-policy`): bu değişiklik davranışsal optimizasyondur, public API/şema değişmez — dolayısıyla uyumlu.
- Test baseline: master'da ~191 pre-existing failure var (çoğu AmbientServiceProvider paralel-koleksiyon sızması). Başarı ölçütü: **bu değişiklikle İLGİLİ projelerde yeni kırmızı yok**; tam suite koşusunda mevcut baseline'a göre kıyasla.
- Branch: `feature/schedule-after-auto` üzerinde çalış (master'a doğrudan commit yok).

---

### Task 1: LifecycleOrder — Auto ve Schedule yer değişimi

**Files:**
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Execution/Transitions/Pipeline/LifecycleOrderTests.cs` (yeni dosya)

**Interfaces:**
- Consumes: —
- Produces: `LifecycleOrder.Auto = 80`, `LifecycleOrder.Schedule = 90`, `LifecycleOrder.ClearBusyOnResumeStep = LifecycleOrder.Auto - 1` (= 79). Task 2 ve 3 bu yeni sırayı varsayar.

- [ ] **Step 1: Failing test yaz**

Yeni dosya `test/BBT.Workflow.Domain.Tests/Execution/Transitions/Pipeline/LifecycleOrderTests.cs`:

```csharp
using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Pins the epilogue ordering contract: automatic transitions are evaluated BEFORE
/// scheduled transitions are armed, so a satisfied auto winner can suppress pointless
/// timer arming (which the next hop's CancelScheduledJobs would immediately tear down).
/// </summary>
public class LifecycleOrderTests
{
    [Fact]
    public void Auto_ShouldRunBeforeSchedule()
    {
        LifecycleOrder.Auto.ShouldBeLessThan(LifecycleOrder.Schedule);
    }

    [Fact]
    public void ClearBusyOnResume_ShouldRunBeforeEntireEpilogue()
    {
        // Subflow/long-poll resumes start from this step and must still walk BOTH
        // epilogue steps (Auto first, then Schedule).
        LifecycleOrder.ClearBusyOnResumeStep.ShouldBeLessThan(LifecycleOrder.Auto);
        LifecycleOrder.ClearBusyOnResumeStep.ShouldBeLessThan(LifecycleOrder.Schedule);
    }

    [Fact]
    public void LongPollTermination_ShouldRunBeforeEpilogue()
    {
        LifecycleOrder.LongPollTermination.ShouldBeLessThan(LifecycleOrder.ClearBusyOnResumeStep);
    }

    [Fact]
    public void Epilogue_ShouldRunBeforeFinishAndFinalize()
    {
        LifecycleOrder.Schedule.ShouldBeLessThan(LifecycleOrder.Finish);
        LifecycleOrder.Finish.ShouldBeLessThan(LifecycleOrder.Finalize);
    }
}
```

- [ ] **Step 2: Testin fail ettiğini doğrula**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~LifecycleOrderTests" -v minimal`
Expected: `Auto_ShouldRunBeforeSchedule` FAIL (90 < 80 değil); diğerleri PASS.

- [ ] **Step 3: LifecycleOrder.cs'i düzenle**

`LifecycleOrder.cs`'te üç sabiti ve XML yorumlarını değiştir. Mevcut:

```csharp
    public const int ClearBusyOnResumeStep = Schedule - 1;

    /// <summary>
    /// Order for scheduling future transitions.
    /// Enqueues scheduled transitions based on timers.
    /// </summary>
    public const int Schedule = 80;

    /// <summary>
    /// Order for executing automatic transitions.
    /// Evaluates and triggers automatic transitions based on conditions.
    /// </summary>
    public const int Auto = 90;
```

Yeni (Auto tanımı Schedule'dan ÖNCE gelsin ki `ClearBusyOnResumeStep = Auto - 1` ileri referans olmasın — C# const'larda ileri referans derlenir ama okunabilirlik için sıralı tut):

```csharp
    public const int ClearBusyOnResumeStep = Auto - 1;

    /// <summary>
    /// Order for executing automatic transitions.
    /// Evaluates auto-transition conditions and, on a winner, requests the next transition.
    /// Runs BEFORE Schedule so a satisfied winner suppresses pointless timer arming
    /// (the chained hop's CancelScheduledJobs would immediately tear those jobs down).
    /// </summary>
    public const int Auto = 80;

    /// <summary>
    /// Order for scheduling future transitions.
    /// Enqueues scheduled transitions based on timers — only when the Auto step did not
    /// select a winner (see ScheduleTransitionsStep's NextTransition guard).
    /// </summary>
    public const int Schedule = 90;
```

Ayrıca aynı dosyada `LongPollTermination` (75) XML yorumundaki "epilogue (Schedule/Auto)" ifadesini "(Auto/Schedule)" yap.

- [ ] **Step 4: Testlerin geçtiğini doğrula**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~LifecycleOrderTests" -v minimal`
Expected: 4/4 PASS.

- [ ] **Step 5: Sıralamaya dokunan mevcut testleri koş**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~PipelineExecutionProfileTests" -v minimal && dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionPipelineTests|FullyQualifiedName~PipelineProfileResolverTests" -v minimal`
Expected: PASS (hepsi isimli sabit kullanıyor; sayısal literal yok). Kırmızı çıkarsa testin sabit mi literal mi beklediğine bak — literal bekleyen test varsa sabite çevir, davranış beklentisini DEĞİŞTİRME.

- [ ] **Step 6: Commit**

```bash
git add src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs test/BBT.Workflow.Domain.Tests/Execution/Transitions/Pipeline/LifecycleOrderTests.cs
git commit -m "feat(pipeline): evaluate auto transitions before scheduling timers

Swap LifecycleOrder.Auto (90->80) and Schedule (80->90) so the auto step
runs first. ClearBusyOnResumeStep is redefined as Auto - 1, keeping its
numeric value 79 and the resume point ahead of the whole epilogue.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: ScheduleTransitionsStep — NextTransition guard + log

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ScheduleTransitionsStep.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/ScheduleTransitionsStepTests.cs` (yeni dosya)

**Interfaces:**
- Consumes: Task 1'in sırası (Auto=80 < Schedule=90) ve mevcut `PipelineDirectives.NextTransition` (`NextTransitionRequest?`, `RequestNextTransition` ile set edilir — `RunAutomaticTransitionsStep` kazananda çağırıyor).
- Produces: `ScheduleTransitionsStep` davranış sözleşmesi: `Directives.NextTransition != null` ⇒ hiçbir yan etki yok, `StepOutcome.ContinueNoWork()` döner. Yeni log extension: `ScheduledTransitionsSkippedForChainedNext(this ILogger logger, Guid instanceId, string stateKey, string nextTransitionKey)`.

- [ ] **Step 1: Failing testleri yaz**

Yeni dosya `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/ScheduleTransitionsStepTests.cs`. Context kurulum kalıbı `ClearBusyOnResumeStepTests.cs`'ten birebir alınmıştır (JSON'dan workflow deserialize + object-initializer'lı `TransitionExecutionContext`); tek fark: state'lerden birine `triggerType: "Scheduled"` + `timer`'lı bir transition eklenir ki `Target.ScheduledTransitions` boş olmasın. `timer` alanının workflow JSON'daki tam şekli için önce `src/BBT.Workflow.Domain/Definitions/Transitions/Transition.cs` içindeki `Timer` property tipine ve onun JSON sözleşmesine bak; deserialize hata verirse şekli oradan düzelt.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="ScheduleTransitionsStep"/>. The step runs AFTER
/// RunAutomaticTransitionsStep; when that step selected a winner (Directives.NextTransition
/// is set) the instance is about to leave the state, so arming its timers would be pure
/// waste — the chained hop's CancelScheduledJobsStep would tear them down immediately.
/// </summary>
public class ScheduleTransitionsStepTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";

    private readonly IBackgroundJobService _backgroundJobService = Substitute.For<IBackgroundJobService>();
    private readonly ITaskTimerService _taskTimerService = Substitute.For<ITaskTimerService>();
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly IInstanceJobRepository _jobRepository = Substitute.For<IInstanceJobRepository>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

    private ScheduleTransitionsStep CreateStep() => new(
        _backgroundJobService,
        _taskTimerService,
        _scriptContextFactory,
        _jobRepository,
        _instanceRepository,
        NullLogger<ScheduleTransitionsStep>.Instance,
        _runtimeInfoProvider);

    [Fact]
    public void Order_ShouldBeSchedule()
    {
        CreateStep().Order.ShouldBe(LifecycleOrder.Schedule);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNextTransitionAlreadySelected_ShouldSkipArmingEntirely()
    {
        // Arrange: target state HAS scheduled transitions, but the Auto step (order 80,
        // runs before this step at 90) already selected a winner.
        var context = CreateContextWithScheduledTarget();
        context.Directives.RequestNextTransition(new NextTransitionRequest("auto-next", "auto"));

        // Act
        var result = await CreateStep().ExecuteAsync(context, CancellationToken.None);

        // Assert: no-op outcome, and NOTHING was armed or persisted.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
        _scriptContextFactory.ReceivedCalls().ShouldBeEmpty();
        _taskTimerService.ReceivedCalls().ShouldBeEmpty();
        _backgroundJobService.ReceivedCalls().ShouldBeEmpty();
        _jobRepository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoNextTransition_ShouldProceedToArm()
    {
        // Arrange: same state, but no winner was selected — arming must proceed.
        // We assert the step ENTERS the arming path (script context build is the first
        // side effect); the full arm/persist chain is covered by integration tests.
        var context = CreateContextWithScheduledTarget();
        context.Directives.NextTransition.ShouldBeNull();

        // Act — the substituted builder chain returns null ScriptContext and the chain
        // will fail afterwards; that is fine, the guard question is answered by the
        // factory having been invoked at all.
        await CreateStep().ExecuteAsync(context, CancellationToken.None);

        // Assert
        _scriptContextFactory.Received(1).NewBuilder(_instanceRepository);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoScheduledTransitions_ShouldContinueNoWork()
    {
        var context = CreateContextWithPlainTarget();

        var result = await CreateStep().ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _scriptContextFactory.ReceivedCalls().ShouldBeEmpty();
        _backgroundJobService.ReceivedCalls().ShouldBeEmpty();
    }

    private TransitionExecutionContext CreateContextWithScheduledTarget()
        => CreateContext(CreateWorkflow(withScheduled: true));

    private TransitionExecutionContext CreateContextWithPlainTarget()
        => CreateContext(CreateWorkflow(withScheduled: false));

    private static TransitionExecutionContext CreateContext(Definitions.Workflow workflow)
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, WorkflowKey, "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);

        var context = new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = "go",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
        // ChangeStateStep normally sets Target; the epilogue reads it.
        context.Target = workflow.GetState("state2").Value!;
        return context;
    }

    private static Definitions.Workflow CreateWorkflow(bool withScheduled)
    {
        // state2 is the entered target. In the scheduled variant it carries one
        // Scheduled-trigger transition with a duration timer.
        var scheduledTransitionJson = withScheduled
            ? """
              {
                  "key": "timeout-check",
                  "target": "state1",
                  "triggerType": "Scheduled",
                  "versionStrategy": "Patch",
                  "labels": [],
                  "onExecutionTasks": [],
                  "view": null,
                  "timer": { "type": "duration", "duration": "PT5M" }
              }
              """
            : null;

        var json = $$"""
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           { "key": "state1", "stateType": "Intermediate", "transitions": [] },
                           { "key": "state2", "stateType": "Intermediate", "transitions": [{{scheduledTransitionJson ?? ""}}] }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
```

Notlar (derleme uyumu için kontrol noktaları — davranış değil):
- `context.Target` settable değilse (`init`/internal), `ClearBusyOnResumeStepTests`/`ChangeStateStepTests`'te Target nasıl set ediliyorsa aynı yolu kullan.
- `timer` JSON şekli (`{ "type": "duration", "duration": "PT5M" }`) `Transition.Timer` sözleşmesiyle uyuşmazsa gerçek sözleşmeye göre düzelt (`Definitions/Timer/` klasörü). Testin amacı `Target.ScheduledTransitions`'ın boş OLMAMASI; timer'ın içeriği ikinci testte script factory substitute'una gelmeden okunmuyor.
- İkinci testte adım, substitute builder'ın `BuildAsync`'i null döndürdüğü için guard SONRASI patlayabilir — assertion yalnızca `NewBuilder`'ın çağrılmış olması; `await` sonucu bilerek assert edilmiyor.

- [ ] **Step 2: Testlerin fail ettiğini doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScheduleTransitionsStepTests" -v minimal`
Expected: `ExecuteAsync_WhenNextTransitionAlreadySelected_ShouldSkipArmingEntirely` FAIL (guard yok, factory çağrılıyor); diğerleri PASS (derleme sorunlarını burada çöz).

- [ ] **Step 3: WorkflowLogs'a log extension ekle**

`src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` içinde 10xxx (transition) bandındaki mevcut en büyük EventId'yi bul (`grep -o 'EventId = 10[0-9]*' src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs | sort -t= -k2 -n | tail -1`) ve bir sonrakini kullan:

```csharp
[LoggerMessage(
    EventId = 10xxx, // <- bulduğun bir sonraki boş 10xxx id
    Level = LogLevel.Debug,
    Message = "Scheduled transitions for state {StateKey} not armed on instance {InstanceId}: auto step already selected next transition {NextTransitionKey}")]
public static partial void ScheduledTransitionsSkippedForChainedNext(
    this ILogger logger, string stateKey, Guid instanceId, string nextTransitionKey);
```

Dosyadaki mevcut partial'ların bulunduğu sınıf/bölge düzenine uy (transition bölgesine yerleştir).

- [ ] **Step 4: Guard'ı implemente et**

`ScheduleTransitionsStep.ExecuteAsync` başındaki mevcut skip'in hemen ÜSTÜNE (scheduled-transition kontrolünden de önce — kazanan varken `ScheduledTransitions` LINQ'ini bile boşuna yürütme):

```csharp
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // The Auto step (LifecycleOrder.Auto, runs just before this step) may have selected
        // a winner: the instance is leaving this state immediately, so arming its timers
        // would be pure churn — the chained hop's CancelScheduledJobsStep would tear them
        // down right away. Skip arming entirely. (updateData never reaches here: its +Self
        // profile excludes Schedule by name.)
        if (context.Directives.NextTransition is { } chainedNext)
        {
            logger.ScheduledTransitionsSkippedForChainedNext(
                context.Target?.Key ?? context.Current.Key, context.InstanceId, chainedNext.TransitionKey);
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Skip if no scheduled transitions
        if (!HasScheduledTransitions(context))
        ...
```

Sınıfın `<summary>` XML yorumuna bir cümle ekle: "Runs after RunAutomaticTransitionsStep; when an auto winner was selected (Directives.NextTransition set) it arms nothing."

- [ ] **Step 5: Testlerin geçtiğini doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScheduleTransitionsStepTests" -v minimal`
Expected: 4/4 PASS.

- [ ] **Step 6: İlgili test kümelerini koş**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~Pipeline" -v minimal && dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~Pipeline" -v minimal`
Expected: Yeni kırmızı yok (baseline'daki pre-existing failure'lar Pipeline filtresine düşmüyor; düşen olursa master'da da kırmızı mı diye `git stash` ile doğrula).

- [ ] **Step 7: Commit**

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ScheduleTransitionsStep.cs src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/ScheduleTransitionsStepTests.cs
git commit -m "feat(pipeline): skip timer arming when auto step selected a winner

ScheduleTransitionsStep now no-ops when Directives.NextTransition is set:
the instance is leaving the state immediately, and the chained hop's
CancelScheduledJobsStep used to tear the fresh jobs down right away.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Dokümantasyon senkronu

**Files:**
- Modify: `CLAUDE.md` (kök — pipeline tablosu ve profil paragrafı)
- Modify: `.claude/rules/vnext-workflow-developer.md` (pipeline tablosu)
- Modify: `docs/architecture/workflow-execution-pipeline.md` (sıra/diyagram geçen yerler)

**Interfaces:**
- Consumes: Task 1–2'nin kesinleşmiş davranışı.
- Produces: — (yalnız doküman).

- [ ] **Step 1: Kök CLAUDE.md pipeline tablosunu güncelle**

"Transition Pipeline" tablosunda iki satırın order/sırasını değiştir ve Schedule'ın koşulunu yaz:

```
| 80  | RunAutomaticTransitionsStep | Evaluate auto-transition conditions; set NextTransition |
| 90  | ScheduleTransitionsStep     | Schedule future transitions — skipped when auto selected a winner |
```

`ClearBusyOnResumeStep` satırı 79'da kalır (değer değişmedi). Aynı dosyada LifecycleOrder'a atıf yapan başka sıra anlatısı varsa (ör. "ScheduleTransitionsStep | 80") tara: `grep -n "Schedule\|Auto" CLAUDE.md`.

- [ ] **Step 2: .claude/rules/vnext-workflow-developer.md tablosunu aynı şekilde güncelle**

Aynı iki satır + `StepOutcome`/profil bölümlerinde sıraya atıf varsa düzelt. Ek olarak "Locking" ya da pipeline bölümünün sonuna tek maddelik davranış notu ekle:

```
- **Epilogue sırası Auto → Schedule'dır.** Auto kazanan seçtiyse (`Directives.NextTransition` dolu)
  `ScheduleTransitionsStep` hiçbir timer arm etmez — eski "arm et, zincirin bir sonraki hop'unda
  CancelScheduledJobs ile sil" churn'ü bilinçli olarak kaldırıldı. Kazananla zincirlenen hop fault
  ederse timer'lar da arm edilmemiştir (faulted instance'ta zaten işe yaramazlardı).
```

- [ ] **Step 3: docs/architecture/workflow-execution-pipeline.md'yi güncelle**

`grep -n "Schedule\|Automatic\|80\|90" docs/architecture/workflow-execution-pipeline.md` ile sıra anlatan tüm yerleri bul; tabloyu/diyagramı yeni sıraya çevir ve "Schedule is skipped when the auto step selected a winner" cümlesini adım açıklamasına ekle. (İngilizce doküman — İngilizce yaz.)

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md .claude/rules/vnext-workflow-developer.md docs/architecture/workflow-execution-pipeline.md
git commit -m "docs(pipeline): document auto-before-schedule epilogue ordering

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Tam derleme + regresyon süpürmesi

**Files:** — (yalnız doğrulama)

**Interfaces:**
- Consumes: Task 1–3.
- Produces: yeşil derleme + baseline'a göre yeni kırmızı olmadığının kanıtı.

- [ ] **Step 1: Tam solution build**

Run: `dotnet build`
Expected: 0 error. (İlk kurulumsa önce `./scripts/setup-netstandard-ref.sh`.)

- [ ] **Step 2: Etkilenen üç test projesini tam koş**

Run: `dotnet test test/BBT.Workflow.Domain.Tests -v minimal; dotnet test test/BBT.Workflow.Application.Tests -v minimal`
Expected: Master baseline'ında ~191 pre-existing failure var (çoğu AmbientServiceProvider paralel-koleksiyon sızması — bkz. memory `vnext-test-baseline`). Karşılaştırma için failure LİSTESİNİ al; şüphede kalırsan aynı filtreyle `git stash && dotnet test ... && git stash pop` yaparak master'la birebir kıyasla. Başarı ölçütü: bu branch'e ÖZGÜ yeni kırmızı yok.

- [ ] **Step 3: Kanıtı raporla**

Yeni-kırmızı-yok iddiasını failure sayıları ve (varsa) fark listesiyle birlikte kullanıcıya yaz — çıplak "testler geçti" deme.

---

### Task 5: Integration test — vnext-example (KULLANICI ONAYI GEREKMEZ; politika gereği zorunlu, ama altyapıyı AYAĞA KALDIRMADAN ÖNCE mevcut durumu kontrol et)

CLAUDE.local.md politikası: temel pipeline davranışı değişiyor → integration test **yaz ve çalıştır**. Bu task ağır altyapı ister (docker infra + DbMigrator + 4 app, hepsi `--launch-profile http`); koşmadan önce ayakta olan servisi yeniden başlatma.

**Files (vnext-example reposunda — `/Users/U0B006/Documents/repos/burgan-tech/vnext-example`):**
- Create/Modify: `tests/Core.IntegrationTests/Tests/ScheduleAfterAuto/` altında yeni test sınıfı + senaryo `README.md`
- Modify: `TEST-SCENARIOS.md` (aynı commit'te satır ekle — zorunlu konvansiyon)
- Modify (geçici, commit'leme): `tests/Core.IntegrationTests/test.runsettings` → `<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>` satırını aç (image değil lokal runtime'a bağlanmak İÇİN ŞART)

**Interfaces:**
- Consumes: Task 1–2'nin runtime davranışı; VNext.Testing.Sdk (davranışı `/Users/U0B006/Documents/repos/burgan-tech/vnext-integration` reposundan OKU, tahmin etme).
- Produces: iki assertion'lı senaryo (aşağıda).

- [ ] **Step 1: Mevcut senaryo envanterini kontrol et**

`TEST-SCENARIOS.md` ve `tests/Core.IntegrationTests/Tests/` altında scheduled-transition kullanan mevcut bir akış var mı bak (`grep -rn "Scheduled\|timer" /Users/U0B006/Documents/repos/burgan-tech/vnext-example/tests /Users/U0B006/Documents/repos/burgan-tech/vnext-example/*/README.md`). Uygun akış varsa yeni workflow üretme, ona state ekle/uyarlа.

- [ ] **Step 2: Senaryo akışını hazırla**

İhtiyaç: bir state'te HEM koşullu auto transition HEM scheduled transition. İki instance ile iki yol:
1. **Auto kazanır** (koşulu sağlayan data ile başlat) → instance zincirlenip ilerler; o state için **hiç `InstanceJob` (JobType.ScheduledTransition) satırı oluşmamış** olmalı. Doğrulama: state function'ın `transitions` dizisinde `kind: "scheduled"` girdisi görünmemesi (SDK üzerinden) — DB'ye inmeden en temiz gözlem noktası; SDK'da job sorgusu varsa onu da kullan.
2. **Auto kazanmaz** (koşulu sağlamayan data) → instance state'te bekler; state function `kind: "scheduled"` girdisini `executeAtUtc` ile göstermeli, timer süresi dolunca scheduled transition fire etmeli.

- [ ] **Step 3: Runtime'ı lokalden ayağa kaldır (ayakta değilse)**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext/etc/docker && ./run-docker.sh
```

Migration gerekiyorsa bir kez: `dotnet run --project workers/BBT.Workflow.DbMigrator --launch-profile DbMigrator`. Sonra 4 app'i AYRI terminallerde `--launch-profile http` ile (orchestration 4201, execution 4202, Inbox, Outbox — profil bayrağını asla atlama).

- [ ] **Step 4: Testi koş**

Run (vnext-example içinde): `dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~ScheduleAfterAuto" -v minimal`
Expected: iki yol da yeşil.

- [ ] **Step 5: Senaryo README'si + TEST-SCENARIOS.md satırı yaz ve commit'le (vnext-example)**

README zorunlu bölümleri: Neyi denetliyor (auto-kazananın timer arm'ını bastırması), Neden var (bu değişiklik — vnext plan `docs/superpowers/plans/2026-09-02-schedule-after-auto.md`, 2026-09-02), akış şeması, nasıl çalıştırılır (yukarıdaki komutlar + lokal runtime şartı), başarı kriteri. `TEST-SCENARIOS.md` tablosuna aynı commit'te satır: feature seti = "pipeline epilogue ordering (Auto→Schedule), ScheduleTransitionsStep NextTransition guard, state function scheduled entries". `test.runsettings` değişikliğini commit'e KOYMA.

```bash
git add tests/Core.IntegrationTests/Tests/ScheduleAfterAuto TEST-SCENARIOS.md
git commit -m "test: schedule-after-auto pipeline ordering scenario

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-Review (yapıldı)

- **Spec coverage:** (1) Auto önce → Task 1. (2) Kazanan varsa enqueue yok → Task 2. (3) Kazanan yoksa eski davranış → Task 2 pozitif test + Task 5 yol 2. (4) Gereksiz cancel churn'ünün kalkması → sıralamanın doğal sonucu; Task 5 yol 1 gözlemliyor.
- **Placeholder taraması:** WorkflowLogs EventId "10xxx" bilinçli — dosyadaki bir sonraki boş id'nin bulunma komutu verildi. Timer JSON şekli için doğrulama talimatı verildi (test derlemesi kontrol noktası). Başka TBD yok.
- **Tip tutarlılığı:** `NextTransitionRequest("auto-next", "auto")` imzası `PipelineDirectives.cs`'teki record ile uyumlu; `StepOutcome.ContinueNoWork()` mevcut API (ScheduleTransitionsStep:42'de kullanılıyor); `ScheduledTransitionsSkippedForChainedNext(stateKey, instanceId, nextTransitionKey)` imzası Task 2 Step 3 ve 4'te aynı.
