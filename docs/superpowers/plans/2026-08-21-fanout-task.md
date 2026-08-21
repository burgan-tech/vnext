# FanOutTask (Type 21) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Runtime'da belli olan bir koleksiyon üzerinden herhangi bir inner task'ı N kez paralel çalıştıran, tek InstanceData patch üreten yeni `FanOutTask` (TaskType 21) — inline scatter-gather modu.

**Architecture:** FanOut, orchestration-local bir `TaskExecutorBase<FanOutTask>` executor'ı olarak mevcut task altyapısına eklenir. FanOut task'ın kendisi `TaskExecutionEngine`'den normal geçer (retry/boundary/journal/tek output application bedava); item'lar aynı engine'i yeni `TaskEngineExecutionOptions` (SuppressDataApply, JournalTaskKey, PreparedTask, CaptureResponse) ile "collect-only" modda, item başına DI scope + `CreateParallelBranch()` izolasyonuyla çağırır. Join policy sonucu tek `OutputHandler` çağrısıyla task çıktısına dönüşür.

**Tech Stack:** .NET 10, xUnit + NSubstitute + Shouldly, Aether Result pattern, Roslyn script engine (`IScriptEngine.CompileToInstanceAsync<T>`).

**Spec:** `docs/superpowers/specs/2026-08-21-fanout-task-design.md`
**Branch:** `feature/fanout-task-design` (bu branch üzerinde devam)

## Spec Amendments (plan aşamasında netleşen sapmalar)

1. **`ItemInputHandler` imzası:** `IMapping.InputHandler(task, context)` konvansiyonunda input binding **task mutasyonudur** (ScriptResponse audit içindir). Bu yüzden imza `ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)` — klonlanmış inner task parametre olarak verilir, script onu mutasyonlar.
2. **`FanOutItemResult.Attempts` kaldırıldı:** Engine retry sayısını `TasksExecutionResult` üzerinden dışarı vermiyor; attempt görünürlüğü item journal kaydı ve retry span event'lerinde zaten var.
3. **Validasyon bölünmesi:** FanOut config workflow dokümanında değil **task component'inde** yaşar; `WorkflowValidator` dokunulmaz. Yapısal kurallar `FanOutTask.Configure()` içinde (fail-fast `ArgumentException`), cross-component kurallar (nested fan-out, kaynak belirsizliği) executor preflight'ında runtime'da uygulanır.
4. **`TaskExecutorContext`'e `Origin` eklenir:** Item'lar parent'ın `TaskExecutionOrigin`'ini miras almalı; context bugün bunu taşımıyor.
5. **Mapping yokken default input binding:** item değeri branch context'e `SetBody(item.Value)` ile verilir. Task config mutasyonu gereken inner türlerde (Http URL/body şablonu vb.) `ItemInputHandler` yazılması gerekir — "sıfır-script" garantisi yalnız context.Body okuyan inner task'lar (ör. Script) için geçerlidir; dokümante edilir.

## Önemli bağlam (mühendis için)

- **Test baseline'ı kirli:** master'da ~191 pre-existing test failure var (AmbientServiceProvider paralel-koleksiyon sızması). Her zaman `--filter` ile kendi testlerini çalıştır; suite genelini asla başarı kriteri sayma.
- Task journal: `InstanceTask` kaydını engine üretir (`TaskExecutionEngine.ExecuteCoreAsync`, `new InstanceTask(guid, transitionId, task.Key)`); `InstanceAction` diye bir kayıt task yolunda **yoktur**.
- Aynı `Order`'daki task'lar bugün zaten paralel koşar (`TaskCoordinator.ExecuteTaskGroupInParallelAsync`) — scope-per-branch deseninin kaynağı orası.
- `MergeParallelBranch` aynı key'e farklı değer gelirse exception atar — FanOut item branch'leri **merge edilmez, atılır**.
- Loglama: asla raw `logger.LogX` kullanma; `WorkflowLogs.cs` LoggerMessage extension'ları (Task 11).

---

### Task 1: Domain — `TaskType.FanOut` + `FanOutTask` sınıfı

**Files:**
- Modify: `src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs`
- Modify: `src/BBT.Workflow.Domain/Definitions/Tasks/WorkflowTask.cs` (JsonDerivedType)
- Create: `src/BBT.Workflow.Domain/Definitions/Tasks/FanOutTask.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Definitions/Tasks/FanOutTaskTests.cs`

- [ ] **Step 1: Failing test'i yaz**

```csharp
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Tasks;

public class FanOutTaskTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private const string ValidConfig = """
    {
      "mode": "inline",
      "itemsPath": "$.documents",
      "itemAlias": "document",
      "task": { "key": "process-doc", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
      "execution": { "maxDegreeOfParallelism": 5, "itemTimeoutSeconds": 30, "batchTimeoutSeconds": 120 },
      "join": { "policy": "allSettled", "resultKey": "documentResults", "ordered": true }
    }
    """;

    [Fact]
    public void Configure_Should_Parse_Valid_Config()
    {
        var task = FanOutTask.Create(Parse(ValidConfig));

        task.GetTaskType().ShouldBe(TaskType.FanOut);
        task.ItemsPath.ShouldBe("$.documents");
        task.ItemAlias.ShouldBe("document");
        task.ItemTask.ShouldNotBeNull();
        task.ItemTask.Key.ShouldBe("process-doc");
        task.ItemTask.Domain.ShouldBe("core");
        task.MaxDegreeOfParallelism.ShouldBe(5);
        task.ItemTimeoutSeconds.ShouldBe(30);
        task.BatchTimeoutSeconds.ShouldBe(120);
        task.JoinPolicy.ShouldBe(FanOutJoinPolicy.AllSettled);
        task.ResultKey.ShouldBe("documentResults");
        task.Ordered.ShouldBeTrue();
    }

    [Fact]
    public void Configure_Should_Apply_Defaults()
    {
        var task = FanOutTask.Create(Parse("""
        { "itemsPath": "$.items",
          "task": { "key": "t", "domain": "d", "flow": "f", "version": "1.0.0" } }
        """));

        task.Mode.ShouldBe("inline");
        task.MaxDegreeOfParallelism.ShouldBe(4);
        task.ItemTimeoutSeconds.ShouldBe(30);
        task.BatchTimeoutSeconds.ShouldBe(120);
        task.JoinPolicy.ShouldBe(FanOutJoinPolicy.AllSettled);
        task.Ordered.ShouldBeTrue();
        task.ResultKey.ShouldBe("fanOutResults");
    }

    [Theory]
    [InlineData("""{ "mode": "durable", "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" } }""")]  // durable Faz 1'de yok
    [InlineData("""{ "itemsPath": "$.x" }""")]                                                              // task referansı zorunlu
    [InlineData("""{ "itemsPath": "documents", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" } }""")] // itemsPath $. ile başlamalı
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "join": { "policy": "quorum" } }""")] // quorum → minSuccess zorunlu
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "execution": { "maxDegreeOfParallelism": 0 } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "execution": { "itemTimeoutSeconds": 300, "batchTimeoutSeconds": 120 } }""")] // item > batch
    public void Configure_Should_Reject_Invalid_Config(string json)
    {
        Should.Throw<ArgumentException>(() => FanOutTask.Create(Parse(json)));
    }

    [Fact]
    public void Clone_Should_Copy_All_Properties_And_Reset_Should_Clear()
    {
        var task = FanOutTask.Create(Parse(ValidConfig));

        var clone = (FanOutTask)task.Clone();
        clone.ItemsPath.ShouldBe(task.ItemsPath);
        clone.ItemTask!.Key.ShouldBe("process-doc");
        clone.JoinPolicy.ShouldBe(task.JoinPolicy);

        clone.Reset();
        clone.ItemsPath.ShouldBeNull();
        clone.ItemTask.ShouldBeNull();
        clone.MaxDegreeOfParallelism.ShouldBe(4);
    }

    [Fact]
    public void Should_Deserialize_Via_Polymorphic_Discriminator_21()
    {
        var json = $$"""{ "type": "21", "config": {{ValidConfig}} }""";
        var task = JsonSerializer.Deserialize<WorkflowTask>(json);
        task.ShouldBeOfType<FanOutTask>();
    }
}
```

Not: `Should_Deserialize_Via_Polymorphic_Discriminator_21` mevcut polymorphic deserialization'ın kullandığı `JsonSerializerOptions` ile uyumlu olmalı — `WorkflowTask`'ın başka bir derived type'ının benzer testi varsa (`Domain.Tests` içinde ara: `grep -rn "typeDiscriminator\|Deserialize<WorkflowTask>" test/BBT.Workflow.Domain.Tests`) aynı options'ı kullan.

- [ ] **Step 2: Testin FAIL ettiğini gör**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~FanOutTaskTests"`
Expected: derleme hatası (`FanOutTask` yok) — bu da fail sayılır.

- [ ] **Step 3: Enum + JsonDerivedType + FanOutTask'ı yaz**

`TaskEnums.cs` — `DaprConversation = 20`'nin altına:

```csharp
    DaprConversation = 20,
    FanOut = 21
```

`WorkflowTask.cs` — attribute listesine (satır ~29, DaprConversation'ın altına):

```csharp
[JsonDerivedType(typeof(FanOutTask), typeDiscriminator: "21")]
```

`FanOutTask.cs` (yeni dosya — desen birebir `SubProcessTask.cs`):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Join policy for a fan-out batch: how per-item outcomes combine into the task outcome.
/// </summary>
public enum FanOutJoinPolicy
{
    All = 1,
    AllSettled = 2,
    Quorum = 3,
    FirstSuccess = 4
}

/// <summary>
/// FanOut Task Definition — executes a referenced inner task once per item of a
/// runtime-resolved collection, in parallel, and joins the results into a single output.
/// Inline mode only (mode "durable" is reserved for a later phase).
/// </summary>
public sealed class FanOutTask : WorkflowTask
{
    public const string InlineMode = "inline";
    public const int DefaultMaxDegreeOfParallelism = 4;
    public const int DefaultItemTimeoutSeconds = 30;
    public const int DefaultBatchTimeoutSeconds = 120;
    public const string DefaultResultKey = "fanOutResults";

    private FanOutTask()
    {
    }

    [JsonConstructor]
    private FanOutTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.FanOut).ToString();
    }

    /// <summary>Execution mode. Phase 1 supports only "inline".</summary>
    public string Mode { get; private set; } = InlineMode;

    /// <summary>JSONPath (dot-path subset, "$." rooted) into instance data selecting the item collection. Mutually exclusive with a mapping ItemSelector.</summary>
    public string? ItemsPath { get; private set; }

    /// <summary>Optional readable alias for a single item (used in default input binding and logs).</summary>
    public string? ItemAlias { get; private set; }

    /// <summary>Reference to the inner task executed once per item.</summary>
    public Reference? ItemTask { get; private set; }

    public int MaxDegreeOfParallelism { get; private set; } = DefaultMaxDegreeOfParallelism;
    public int ItemTimeoutSeconds { get; private set; } = DefaultItemTimeoutSeconds;
    public int BatchTimeoutSeconds { get; private set; } = DefaultBatchTimeoutSeconds;

    public FanOutJoinPolicy JoinPolicy { get; private set; } = FanOutJoinPolicy.AllSettled;

    /// <summary>Minimum successful items for Quorum policy. Required when JoinPolicy is Quorum.</summary>
    public int? MinSuccess { get; private set; }

    /// <summary>Instance data key the default output writes the item results under.</summary>
    public string ResultKey { get; private set; } = DefaultResultKey;

    /// <summary>When true (default) the result list preserves item index order.</summary>
    public bool Ordered { get; private set; } = true;

    /// <summary>Per-item error boundary (retry/fallback applied independently to every item).</summary>
    public ErrorBoundary? ItemErrorBoundary { get; private set; }

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("mode", out var modeEl))
        {
            var mode = modeEl.GetString();
            if (!string.Equals(mode, InlineMode, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"FanOutTask mode '{mode}' is not supported yet. Only '{InlineMode}' is available (Key={Key}).",
                    nameof(config));
            Mode = InlineMode;
        }

        if (config.TryGetProperty("itemsPath", out var itemsPathEl))
        {
            var itemsPath = itemsPathEl.GetString();
            if (string.IsNullOrWhiteSpace(itemsPath) || !itemsPath.StartsWith("$.", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"FanOutTask itemsPath must start with '$.' (Key={Key}).", nameof(config));
            ItemsPath = itemsPath;
        }

        if (config.TryGetProperty("itemAlias", out var aliasEl))
            ItemAlias = aliasEl.GetString();

        if (!config.TryGetProperty("task", out var taskEl))
            throw new ArgumentException($"Property 'task' is required for FanOutTask (Key={Key}).", nameof(config));

        string RequiredTaskProp(string name) =>
            taskEl.TryGetProperty(name, out var el) && el.GetString() is { Length: > 0 } v
                ? v
                : throw new ArgumentException(
                    $"Property 'task.{name}' is required for FanOutTask (Key={Key}).", nameof(config));

        ItemTask = new Reference(
            RequiredTaskProp("key"),
            RequiredTaskProp("domain"),
            RequiredTaskProp("flow"),
            RequiredTaskProp("version"));

        if (config.TryGetProperty("execution", out var execEl))
        {
            if (execEl.TryGetProperty("maxDegreeOfParallelism", out var dopEl))
                MaxDegreeOfParallelism = dopEl.GetInt32();
            if (execEl.TryGetProperty("itemTimeoutSeconds", out var itemToEl))
                ItemTimeoutSeconds = itemToEl.GetInt32();
            if (execEl.TryGetProperty("batchTimeoutSeconds", out var batchToEl))
                BatchTimeoutSeconds = batchToEl.GetInt32();
        }

        if (MaxDegreeOfParallelism < 1)
            throw new ArgumentException($"FanOutTask maxDegreeOfParallelism must be >= 1 (Key={Key}).", nameof(config));
        if (ItemTimeoutSeconds < 1 || BatchTimeoutSeconds < 1)
            throw new ArgumentException($"FanOutTask timeouts must be positive (Key={Key}).", nameof(config));
        if (ItemTimeoutSeconds > BatchTimeoutSeconds)
            throw new ArgumentException(
                $"FanOutTask itemTimeoutSeconds ({ItemTimeoutSeconds}) cannot exceed batchTimeoutSeconds ({BatchTimeoutSeconds}) (Key={Key}).",
                nameof(config));

        if (config.TryGetProperty("join", out var joinEl))
        {
            if (joinEl.TryGetProperty("policy", out var policyEl))
            {
                var policyStr = policyEl.GetString();
                if (!Enum.TryParse<FanOutJoinPolicy>(policyStr, ignoreCase: true, out var policy))
                    throw new ArgumentException(
                        $"FanOutTask join.policy '{policyStr}' is invalid. Expected one of: all, allSettled, quorum, firstSuccess (Key={Key}).",
                        nameof(config));
                JoinPolicy = policy;
            }

            if (joinEl.TryGetProperty("minSuccess", out var minEl))
                MinSuccess = minEl.GetInt32();
            if (joinEl.TryGetProperty("resultKey", out var rkEl) && rkEl.GetString() is { Length: > 0 } rk)
                ResultKey = rk;
            if (joinEl.TryGetProperty("ordered", out var ordEl))
                Ordered = ordEl.GetBoolean();
        }

        if (JoinPolicy == FanOutJoinPolicy.Quorum && MinSuccess is null or < 1)
            throw new ArgumentException(
                $"FanOutTask join.policy 'quorum' requires join.minSuccess >= 1 (Key={Key}).", nameof(config));

        if (config.TryGetProperty("errorBoundary", out var ebEl) && ebEl.ValueKind == JsonValueKind.Object)
            ItemErrorBoundary = ebEl.Deserialize<ErrorBoundary>(JsonSerializerConstants.JsonOptions);
    }

    public static FanOutTask Create(JsonElement config) => new(config);

    public override WorkflowTask Clone() => CloneTyped();

    public FanOutTask CloneTyped()
    {
        var cloned = new FanOutTask();
        CopyBaseTo(cloned);
        cloned.Mode = Mode;
        cloned.ItemsPath = ItemsPath;
        cloned.ItemAlias = ItemAlias;
        cloned.ItemTask = ItemTask;
        cloned.MaxDegreeOfParallelism = MaxDegreeOfParallelism;
        cloned.ItemTimeoutSeconds = ItemTimeoutSeconds;
        cloned.BatchTimeoutSeconds = BatchTimeoutSeconds;
        cloned.JoinPolicy = JoinPolicy;
        cloned.MinSuccess = MinSuccess;
        cloned.ResultKey = ResultKey;
        cloned.Ordered = Ordered;
        cloned.ItemErrorBoundary = ItemErrorBoundary;
        return cloned;
    }

    public override void Reset()
    {
        base.Reset();
        Mode = InlineMode;
        ItemsPath = null;
        ItemAlias = null;
        ItemTask = null;
        MaxDegreeOfParallelism = DefaultMaxDegreeOfParallelism;
        ItemTimeoutSeconds = DefaultItemTimeoutSeconds;
        BatchTimeoutSeconds = DefaultBatchTimeoutSeconds;
        JoinPolicy = FanOutJoinPolicy.AllSettled;
        MinSuccess = null;
        ResultKey = DefaultResultKey;
        Ordered = true;
        ItemErrorBoundary = null;
    }

    public static FanOutTask CreateEmpty() => new();
}
```

Notlar:
- `JsonSerializerConstants.JsonOptions` Domain'de erişilebilir olmalı (`BBT.Workflow` namespace'inde kullanılıyor); değilse `ErrorBoundary`'nin workflow JSON'ından nasıl deserialize edildiğine bak (`grep -rn "Deserialize<ErrorBoundary>" src/`) ve aynı yolu kullan.
- `ErrorBoundary` tipinin tam adı/namespace'i için `grep -rn "class ErrorBoundary" src/BBT.Workflow.Domain`.
- `ITaskFactory.CreateFromCached` → `Clone()`'u kullanır; `TaskFactory`'de task type'a göre switch/pooling varsa (`grep -rn "SubProcessTask" src/BBT.Workflow.Application/Tasks/Factory/`) FanOut için de karşılık ekle (pooling yoksa `Clone()` yeterli).

- [ ] **Step 4: Testlerin PASS ettiğini gör**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~FanOutTaskTests"`
Expected: tüm testler PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Definitions/Tasks/ test/BBT.Workflow.Domain.Tests/Definitions/Tasks/FanOutTaskTests.cs
git commit -m "feat(domain): add FanOutTask (type 21) definition with config parsing"
```

---

### Task 2: Domain — `IFanOutMapping` kontratı + record'lar

**Files:**
- Create: `src/BBT.Workflow.Domain/Scripting/Contracts/IFanOutMapping.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/FanOutMappingContractTests.cs`

- [ ] **Step 1: Failing test**

```csharp
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Scripting;

public class FanOutMappingContractTests
{
    private sealed class MinimalMapping : IFanOutMapping
    {
        public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
            => Task.FromResult(new ScriptResponse());

        public Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result)
            => Task.FromResult(new ScriptResponse());
    }

    [Fact]
    public async Task Default_ItemSelector_Should_Return_Null()
    {
        IFanOutMapping mapping = new MinimalMapping();
        var items = await mapping.ItemSelector(null!);
        items.ShouldBeNull();
    }

    [Fact]
    public void FanOutResult_Should_Carry_Counts_And_Items()
    {
        var items = new List<FanOutItemResult>
        {
            new(0, "a", true, null, null, null, TimeSpan.FromMilliseconds(10)),
            new(1, "b", false, null, "Task:500", "boom", TimeSpan.FromMilliseconds(20))
        };
        var result = new FanOutResult(2, 1, 1, false, items);
        result.Succeeded.ShouldBe(1);
        result.Items[1].ErrorCode.ShouldBe("Task:500");
    }
}
```

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~FanOutMappingContractTests"`
Expected: derleme hatası.

- [ ] **Step 3: Kontratı yaz**

```csharp
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Mapping contract for FanOutTask. Mirrors <see cref="IMapping"/> conventions:
/// input binding mutates the (cloned) inner task, the returned ScriptResponse is audit data,
/// and the OutputHandler result is merged into instance data — exactly once per batch.
/// </summary>
/// <remarks>
/// ItemInputHandler MUST be a pure function with respect to instance data: it runs on an
/// isolated parallel-branch ScriptContext that is discarded after the item completes.
/// The single write point for the whole batch is <see cref="OutputHandler"/>.
/// </remarks>
public interface IFanOutMapping
{
    /// <summary>
    /// Produces the fan-out item collection when the task defines no itemsPath.
    /// Default implementation returns null, meaning "use itemsPath".
    /// Returning non-null while itemsPath is also configured is an execution error (ambiguous source).
    /// </summary>
    Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context)
        => Task.FromResult<IEnumerable<dynamic>?>(null);

    /// <summary>
    /// Binds one item's input by mutating the cloned inner task (endpoint, body, headers…).
    /// Called once per item, on that item's isolated branch context.
    /// </summary>
    Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item);

    /// <summary>
    /// Called exactly once after every item settled. The returned ScriptResponse.Data becomes
    /// the FanOutTask's output and is merged into instance data as a single patch.
    /// </summary>
    Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result);
}

/// <summary>A single fan-out item: its position, value and stable key.</summary>
public sealed record FanOutItem(int Index, dynamic? Value, string ItemKey);

/// <summary>Aggregate outcome of a fan-out batch, handed to the OutputHandler.</summary>
public sealed record FanOutResult(
    int Total,
    int Succeeded,
    int Failed,
    bool TimedOut,
    IReadOnlyList<FanOutItemResult> Items);

/// <summary>Outcome of a single item execution.</summary>
public sealed record FanOutItemResult(
    int Index,
    string ItemKey,
    bool IsSuccess,
    dynamic? Data,
    string? ErrorCode,
    string? ErrorMessage,
    TimeSpan Duration);
```

- [ ] **Step 4: PASS doğrula**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~FanOutMappingContractTests"`

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Scripting/Contracts/IFanOutMapping.cs test/BBT.Workflow.Domain.Tests/Scripting/FanOutMappingContractTests.cs
git commit -m "feat(domain): add IFanOutMapping contract with FanOutItem/FanOutResult records"
```

---

### Task 3: Application — Engine'e collect-only destek (`TaskEngineExecutionOptions`) + `TaskExecutorContext.Origin`

**Files:**
- Create: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskEngineExecutionOptions.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/ITaskExecutionEngine.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionEngine.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TasksExecutionResult.cs` (Response prop)
- Modify: `src/BBT.Workflow.Domain/Tasks/Executors/Core/TaskExecutorContext.cs` (Origin)
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskExecutionEngineTests.cs` (mevcut dosyaya ekle)

- [ ] **Step 1: Mevcut `TaskExecutionEngineTests.cs`'i oku, arrange desenini (mock'lanan bağımlılıklar, ScriptContext kurulumu) aynen kullanarak şu üç testi ekle**

```csharp
[Fact]
public async Task ExecuteAsync_WithSuppressDataApply_Should_Not_Write_Instance_Data()
{
    // Arrange: mevcut testlerdeki gibi başarılı bir executor + IInstanceDataWriteService mock'u
    var options = new TaskEngineExecutionOptions { SuppressDataApply = true };

    // Act
    var result = await _engine.ExecuteAsync(_onExecuteTask, _transitionId, TaskTrigger.OnEntry,
        TaskExecutionOrigin.Flow, _context, options, CancellationToken.None);

    // Assert
    result.IsSuccess.ShouldBeTrue();
    await _instanceDataWriteService.DidNotReceiveWithAnyArgs()
        .AppendAsync(default!, default!, default, default);
}

[Fact]
public async Task ExecuteAsync_WithPreparedTask_Should_Bypass_Factory_And_Use_JournalTaskKey()
{
    var prepared = /* mevcut testlerdeki task fixture'ı, ör. ScriptTask */;
    var options = new TaskEngineExecutionOptions
    {
        PreparedTask = prepared,
        JournalTaskKey = "fan-out-docs#3",
        SuppressDataApply = true
    };

    var result = await _engine.ExecuteAsync(_onExecuteTask, _transitionId, TaskTrigger.OnEntry,
        TaskExecutionOrigin.Flow, _context, options, CancellationToken.None);

    result.IsSuccess.ShouldBeTrue();
    await _taskFactory.DidNotReceiveWithAnyArgs().CreateExecutionTaskAsync(default!, default);
    // Persistence strategy'ye giden InstanceTask'ın TaskId'si journal key olmalı:
    await _persistenceStrategy.Received().HandleCreationAsync(
        Arg.Is<InstanceTask>(t => t.TaskId == "fan-out-docs#3"), Arg.Any<CancellationToken>());
}

[Fact]
public async Task ExecuteAsync_WithCaptureResponse_Should_Return_StandardTaskResponse()
{
    var options = new TaskEngineExecutionOptions { CaptureResponse = true, SuppressDataApply = true };

    var result = await _engine.ExecuteAsync(_onExecuteTask, _transitionId, TaskTrigger.OnEntry,
        TaskExecutionOrigin.Flow, _context, options, CancellationToken.None);

    result.Value!.Response.ShouldNotBeNull();
}
```

Not: `InstanceTask.TaskId` property adını doğrula (`grep -n "TaskId\|public.*Key" src/BBT.Workflow.Domain/Instances/InstanceTask.cs`) — ctor üçüncü parametresi task key'idir; property adı farklıysa asserti uyarlayıp bu planı düzelt.

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskExecutionEngineTests"`
Expected: derleme hatası (options overload yok).

- [ ] **Step 3: Implementasyon**

`TaskEngineExecutionOptions.cs` (yeni):

```csharp
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Per-call execution options for <see cref="ITaskExecutionEngine"/>. Introduced for FanOutTask:
/// items run through the full engine lifecycle (retry, boundary, journal, metrics) but must not
/// each write instance data, and need distinct journal identities.
/// </summary>
public sealed record TaskEngineExecutionOptions
{
    public static readonly TaskEngineExecutionOptions Default = new();

    /// <summary>When true, the task output is NOT appended to instance data (collect-only execution).</summary>
    public bool SuppressDataApply { get; init; }

    /// <summary>Overrides the InstanceTask journal key (e.g. "fan-out-docs#3"). Null = task key.</summary>
    public string? JournalTaskKey { get; init; }

    /// <summary>Pre-built task instance to execute; bypasses the task factory load. Used when the caller already cloned and bound the task.</summary>
    public WorkflowTask? PreparedTask { get; init; }

    /// <summary>When true, the final StandardTaskResponse is exposed on TasksExecutionResult.Response.</summary>
    public bool CaptureResponse { get; init; }
}
```

`ITaskExecutionEngine.cs` — mevcut metodun altına ikinci imza:

```csharp
    /// <summary>
    /// Executes a single task with per-call execution options (collect-only mode, journal key
    /// override, prepared task). The parameterless-options overload forwards here with defaults.
    /// </summary>
    Task<Result<TasksExecutionResult>> ExecuteAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        ScriptContext context,
        TaskEngineExecutionOptions options,
        CancellationToken cancellationToken);
```

`TaskExecutionEngine.cs` değişiklikleri:
1. Mevcut `ExecuteAsync` gövdesini options'lı overload'a taşı; eski imza `=> ExecuteAsync(..., TaskEngineExecutionOptions.Default, ct)` olarak forward etsin.
2. `options`'ı `ExecuteWithErrorAwareRetryAsync` ve `ExecuteCoreAsync`'e parametre olarak geçir.
3. `ExecuteCoreAsync` içinde üç nokta:

```csharp
// 1. Load task from factory — PreparedTask varsa factory atlanır
WorkflowTask task;
if (options.PreparedTask is not null)
{
    task = options.PreparedTask;
}
else
{
    var taskResult = await _taskFactory.CreateExecutionTaskAsync(onExecuteTask.Task, cancellationToken);
    // ... mevcut hata bloğu aynen ...
    task = taskResult.Value!;
}
```

```csharp
// 2. Create instance task for tracking — journal key override
var instanceTask = new InstanceTask(
    _guidGenerator.Create(),
    instanceTransitionId ?? Guid.Empty,
    options.JournalTaskKey ?? task.Key);
```

```csharp
// ALWAYS apply output to context ... — suppress bayrağı
if (!options.SuppressDataApply)
{
    await ApplyOutputToContextAsync(response, taskTrigger, origin, context, cancellationToken);
}
```

4. `TasksExecutionResult.cs`'e property ekle:

```csharp
    /// <summary>
    /// The final StandardTaskResponse of the executed task. Populated only when the caller
    /// requested capture via TaskEngineExecutionOptions.CaptureResponse (FanOut item collection).
    /// </summary>
    public StandardTaskResponse? Response { get; init; }
```

5. `ExecuteCoreAsync`'te başarı ve business-failure dönüşlerinde (adım 10 sonrası üç dönüş noktası) capture uygula — her `Result<TasksExecutionResult>.Ok(x)` yerine:

```csharp
var executionResult = TasksExecutionResult.Success([summary], stopwatch.ElapsedMilliseconds);
if (options.CaptureResponse)
    executionResult = executionResult with { Response = response };
return Result<TasksExecutionResult>.Ok(executionResult);
```

(aynı `with { Response = response }` desenini no-boundary business failure ve boundary'li business failure dönüşlerine de uygula; `response` scope'ta mevcut).

6. `TaskExecutorContext.cs` — record'a `Origin` ekle:

```csharp
public sealed record TaskExecutorContext(
    WorkflowTask Task,
    OnExecuteTask OnExecuteTask,
    ScriptContext ScriptContext,
    Guid? InstanceTransitionId,
    TaskTrigger TaskTrigger,
    TaskExecutionOrigin Origin = TaskExecutionOrigin.Flow)
```

ve `TaskExecutionEngine.ExecuteCoreAsync` adım 7'de: `new TaskExecutorContext(task, onExecuteTask, context, instanceTransitionId, taskTrigger, origin)`. Diğer `new TaskExecutorContext(` çağrılarını bul (`grep -rn "new TaskExecutorContext(" src/ test/`) — default parametre sayesinde çoğu derlenmeye devam eder; origin'i bilen çağrı yerlerinde açıkça geçir.

- [ ] **Step 4: Yeni testler + mevcut engine/koordinatör testleri PASS**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskExecutionEngineTests|FullyQualifiedName~TaskCoordinatorTests"`
Expected: hepsi PASS (davranış değişikliği yok — default options).

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Tasks/Coordinator/ src/BBT.Workflow.Domain/Tasks/Executors/Core/TaskExecutorContext.cs test/BBT.Workflow.Application.Tests/Tasks/Coordinator/
git commit -m "feat(engine): add TaskEngineExecutionOptions for collect-only execution (suppress data apply, journal key override, prepared task, response capture)"
```

---

### Task 4: Application — `FanOutOptions` + global bulkhead (`FanOutConcurrencyLimiter`)

**Files:**
- Create: `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutOptions.cs`
- Create: `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutConcurrencyLimiter.cs`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutConcurrencyLimiterTests.cs`

- [ ] **Step 1: Failing test**

```csharp
using BBT.Workflow.Tasks.Executors.FanOut;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public class FanOutConcurrencyLimiterTests
{
    [Fact]
    public async Task Limiter_Should_Cap_Concurrent_Holders_At_MaxConcurrentItems()
    {
        var limiter = new FanOutConcurrencyLimiter(
            Options.Create(new FanOutOptions { MaxConcurrentItems = 2 }));

        var running = 0;
        var peak = 0;
        var gate = new object();

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await limiter.WaitAsync(CancellationToken.None);
            try
            {
                lock (gate) { running++; peak = Math.Max(peak, running); }
                await Task.Delay(20);
            }
            finally
            {
                lock (gate) { running--; }
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks);
        peak.ShouldBeLessThanOrEqualTo(2);
        limiter.ActiveCount.ShouldBe(0);
    }
}
```

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutConcurrencyLimiterTests"`

- [ ] **Step 3: Implementasyon**

`FanOutOptions.cs`:

```csharp
namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>
/// Process-level fan-out settings. Bound from configuration section "Workflow:FanOut".
/// </summary>
public sealed class FanOutOptions
{
    public const string SectionName = "Workflow:FanOut";

    /// <summary>
    /// Global bulkhead: maximum fan-out items executing concurrently across ALL batches
    /// in this process. Effective per-batch concurrency = min(task maxDegreeOfParallelism,
    /// available global slots).
    /// </summary>
    public int MaxConcurrentItems { get; set; } = 64;
}
```

`FanOutConcurrencyLimiter.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>
/// Process-wide bulkhead for fan-out item execution. Singleton: every fan-out batch in the
/// process draws item slots from this single semaphore, so N concurrent instances cannot
/// multiply into N × maxDop downstream calls beyond the configured ceiling.
/// </summary>
public sealed class FanOutConcurrencyLimiter(IOptions<FanOutOptions> options)
{
    private readonly SemaphoreSlim _semaphore = new(
        options.Value.MaxConcurrentItems,
        options.Value.MaxConcurrentItems);

    private readonly int _capacity = options.Value.MaxConcurrentItems;

    /// <summary>Currently held item slots (observability gauge source).</summary>
    public int ActiveCount => _capacity - _semaphore.CurrentCount;

    public Task WaitAsync(CancellationToken cancellationToken)
        => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();
}
```

- [ ] **Step 4: PASS doğrula, sonra DI kaydı**

`TaskServiceCollectionExtensions.cs` → `AddTaskExecutors` metodunun başına:

```csharp
        // FanOut global bulkhead (process-level ceiling across all fan-out batches)
        services.AddOptions<FanOutOptions>()
            .BindConfiguration(FanOutOptions.SectionName);
        services.TryAddSingleton<FanOutConcurrencyLimiter>();
```

(`BindConfiguration` için `Microsoft.Extensions.Options.ConfigurationExtensions` gerek — dosyada mevcut options kayıtlarının nasıl yapıldığına bak, ör. `WorkflowExecutionOptions` kaydı; aynı deseni kullan.)

Run: `dotnet build && dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutConcurrencyLimiterTests"`

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Tasks/Executors/FanOut/ src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/TaskServiceCollectionExtensions.cs test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutConcurrencyLimiterTests.cs
git commit -m "feat(fanout): add FanOutOptions and process-level concurrency bulkhead"
```

---

### Task 5: Application — `FanOutItemsResolver` (itemsPath çözümü + ItemKey üretimi)

**Files:**
- Create: `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutItemsResolver.cs`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutItemsResolverTests.cs`

- [ ] **Step 1: Önce mevcut dynamic dönüşümünü keşfet**

`ScriptContext.SetBody(object?)` (`src/BBT.Workflow.Domain/Scripting/Models.cs:540`) gövdesini oku — instance data / body dynamic'e hangi mekanizmayla çevriliyor (muhtemelen bir Json→dynamic converter helper'ı). Resolver item'ları **aynı mekanizmayla** dynamic'e çevirmeli ki script'lerdeki `item.Value.id` erişimi instance data erişimiyle aynı davransın. Helper'ın adını not et ve Step 3'teki `ToDynamic` çağrısını ona bağla.

- [ ] **Step 2: Failing test**

```csharp
using System.Text.Json;
using BBT.Workflow.Tasks.Executors.FanOut;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public class FanOutItemsResolverTests
{
    private static readonly JsonElement Data = JsonDocument.Parse("""
    {
      "customer": { "id": "c-1" },
      "documents": [
        { "id": "doc-1", "url": "u1" },
        { "id": "doc-2", "url": "u2" }
      ],
      "batch": { "inner": { "items": [1, 2, 3] } },
      "notAnArray": { "id": "x" }
    }
    """).RootElement;

    [Fact]
    public void Resolve_Should_Return_Items_With_Id_As_ItemKey()
    {
        var items = FanOutItemsResolver.Resolve(Data, "$.documents");
        items.Count.ShouldBe(2);
        items[0].Index.ShouldBe(0);
        items[0].ItemKey.ShouldBe("doc-1");
        items[1].ItemKey.ShouldBe("doc-2");
    }

    [Fact]
    public void Resolve_Should_Walk_Nested_Path()
    {
        var items = FanOutItemsResolver.Resolve(Data, "$.batch.inner.items");
        items.Count.ShouldBe(3);
        items[2].ItemKey.ShouldBe("2"); // primitif item → index string
    }

    [Fact]
    public void Resolve_Should_Return_Empty_For_Missing_Path()
    {
        FanOutItemsResolver.Resolve(Data, "$.nope").ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_Should_Throw_When_Path_Targets_Non_Array()
    {
        Should.Throw<InvalidOperationException>(() =>
            FanOutItemsResolver.Resolve(Data, "$.notAnArray"));
    }
}
```

- [ ] **Step 3: Implementasyon**

```csharp
using System.Text.Json;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>
/// Resolves the fan-out item collection from instance data using a dot-path subset of JSONPath
/// ("$.a.b.c" — property navigation only; no filters, wildcards or slices). A missing path
/// yields an empty batch (successful no-op by design); a path resolving to a non-array is an error.
/// ItemKey: the item's "id" or "key" string property when present, otherwise the index.
/// </summary>
public static class FanOutItemsResolver
{
    public static IReadOnlyList<FanOutItem> Resolve(object? instanceData, string itemsPath)
    {
        if (instanceData is null)
            return [];

        // Normalize whatever dynamic representation instance data has into JsonElement for walking.
        var root = instanceData is JsonElement el
            ? el
            : JsonSerializer.SerializeToElement(instanceData, JsonSerializerConstants.JsonOptions);

        var segments = itemsPath[2..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return [];
            }
        }

        if (current.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"FanOut itemsPath '{itemsPath}' resolved to {current.ValueKind}, expected an array.");

        var items = new List<FanOutItem>(current.GetArrayLength());
        var index = 0;
        foreach (var itemEl in current.EnumerateArray())
        {
            items.Add(new FanOutItem(index, ToDynamic(itemEl), ExtractItemKey(itemEl, index)));
            index++;
        }

        return items;
    }

    private static string ExtractItemKey(JsonElement item, int index)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString()!;
            if (item.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
                return key.GetString()!;
        }

        return index.ToString();
    }

    private static dynamic? ToDynamic(JsonElement element)
    {
        // Step 1'de keşfedilen, ScriptContext.SetBody'nin kullandığı Json→dynamic dönüşümünü çağır.
        // Örn. repo'da bir DynamicJsonConverter/JsonDynamicHelper varsa onu kullan; script'lerin
        // gördüğü dynamic temsille birebir aynı olmak ZORUNDA.
        throw new NotImplementedException("bind to the ScriptContext.SetBody conversion helper");
    }
}
```

`ToDynamic` gövdesini Step 1'de bulduğun helper'a bağla; test assertion'ları helper'ın döndürdüğü temsile göre gerekirse uyarlanır (ör. `items[0].Value.id`).

- [ ] **Step 4: PASS doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutItemsResolverTests"`

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutItemsResolver.cs test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutItemsResolverTests.cs
git commit -m "feat(fanout): add itemsPath resolver with dot-path subset and item key extraction"
```

---

### Task 6: Application — `FanOutJoinEvaluator` (policy matrisi)

**Files:**
- Create: `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutJoinEvaluator.cs`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutJoinEvaluatorTests.cs`

- [ ] **Step 1: Failing test — policy × sonuç matrisi**

```csharp
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors.FanOut;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public class FanOutJoinEvaluatorTests
{
    private static FanOutItemResult Ok(int i) => new(i, i.ToString(), true, null, null, null, TimeSpan.Zero);
    private static FanOutItemResult Fail(int i) => new(i, i.ToString(), false, null, "Task:500", "boom", TimeSpan.Zero);

    [Theory]
    // policy, results (1=ok 0=fail), minSuccess, timedOut, expectedSuccess
    [InlineData(FanOutJoinPolicy.All,          "111", null, false, true)]
    [InlineData(FanOutJoinPolicy.All,          "101", null, false, false)]
    [InlineData(FanOutJoinPolicy.All,          "111", null, true,  false)] // timeout → all fails
    [InlineData(FanOutJoinPolicy.AllSettled,   "000", null, false, true)]  // allSettled her zaman success
    [InlineData(FanOutJoinPolicy.AllSettled,   "101", null, true,  true)]  // timeout'ta bile akış devam eder
    [InlineData(FanOutJoinPolicy.Quorum,       "110", 2,    false, true)]
    [InlineData(FanOutJoinPolicy.Quorum,       "100", 2,    false, false)]
    [InlineData(FanOutJoinPolicy.FirstSuccess, "010", null, false, true)]
    [InlineData(FanOutJoinPolicy.FirstSuccess, "000", null, false, false)]
    public void Evaluate_Should_Apply_Policy(
        FanOutJoinPolicy policy, string pattern, int? minSuccess, bool timedOut, bool expected)
    {
        var items = pattern.Select((c, i) => c == '1' ? Ok(i) : Fail(i)).ToList();

        var outcome = FanOutJoinEvaluator.Evaluate(policy, minSuccess, items, timedOut);

        outcome.IsSuccess.ShouldBe(expected);
        if (!expected) outcome.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Evaluate_Empty_Batch_Should_Succeed_For_All_Policies_Except_FirstSuccess()
    {
        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.All, null, [], false).IsSuccess.ShouldBeTrue();
        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.AllSettled, null, [], false).IsSuccess.ShouldBeTrue();
        FanOutJoinEvaluator.Evaluate(FanOutJoinPolicy.FirstSuccess, null, [], false).IsSuccess.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutJoinEvaluatorTests"`

- [ ] **Step 3: Implementasyon**

```csharp
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>Join outcome of a fan-out batch.</summary>
public sealed record FanOutJoinOutcome(bool IsSuccess, string? ErrorMessage);

/// <summary>
/// Pure policy evaluation over settled item results.
/// all: every item must succeed and the batch must not time out.
/// allSettled: always succeeds — partial failure is data, branching is the flow designer's job.
/// quorum: succeeded >= minSuccess.
/// firstSuccess: at least one success (executor cancels the rest on the first success).
/// An empty batch succeeds (no-op) except for firstSuccess, which by definition needs one success.
/// </summary>
public static class FanOutJoinEvaluator
{
    public static FanOutJoinOutcome Evaluate(
        FanOutJoinPolicy policy,
        int? minSuccess,
        IReadOnlyList<FanOutItemResult> items,
        bool timedOut)
    {
        var succeeded = items.Count(i => i.IsSuccess);
        var failed = items.Count - succeeded;

        return policy switch
        {
            FanOutJoinPolicy.AllSettled => new FanOutJoinOutcome(true, null),

            FanOutJoinPolicy.All when timedOut => new FanOutJoinOutcome(false,
                $"FanOut batch timed out with join policy 'all' ({succeeded}/{items.Count} succeeded)."),
            FanOutJoinPolicy.All when failed > 0 => new FanOutJoinOutcome(false,
                $"FanOut join policy 'all' failed: {failed}/{items.Count} item(s) failed."),
            FanOutJoinPolicy.All => new FanOutJoinOutcome(true, null),

            FanOutJoinPolicy.Quorum when succeeded >= (minSuccess ?? 1) => new FanOutJoinOutcome(true, null),
            FanOutJoinPolicy.Quorum => new FanOutJoinOutcome(false,
                $"FanOut quorum not met: {succeeded}/{items.Count} succeeded, minSuccess={minSuccess}."),

            FanOutJoinPolicy.FirstSuccess when succeeded >= 1 => new FanOutJoinOutcome(true, null),
            FanOutJoinPolicy.FirstSuccess => new FanOutJoinOutcome(false,
                $"FanOut join policy 'firstSuccess' failed: no item succeeded ({items.Count} attempted)."),

            _ => new FanOutJoinOutcome(false, $"Unknown join policy '{policy}'.")
        };
    }
}
```

- [ ] **Step 4: PASS doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutJoinEvaluatorTests"`

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutJoinEvaluator.cs test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutJoinEvaluatorTests.cs
git commit -m "feat(fanout): add join policy evaluator (all/allSettled/quorum/firstSuccess)"
```

---

### Task 7: Application — `FanOutTaskExecutor` (çekirdek: preflight + item döngüsü + default output)

**Files:**
- Create: `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutTaskExecutor.cs`
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/TaskServiceCollectionExtensions.cs`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutTaskExecutorTests.cs`

Bu görev planın en büyüğü. Önce **`CacheAsideTaskExecutorTests.cs`'i oku** — o executor da inner "source task"ı lokal orkestre ediyor; ScriptContext/scope-factory mock kurulumunu oradan uyarla.

- [ ] **Step 1: Failing testler — happy path (allSettled), boş koleksiyon, nested guard, kaynak-XOR**

```csharp
// test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutTaskExecutorTests.cs
// Arrange altyapısı: CacheAsideTaskExecutorTests deseninden uyarla.
// Anahtar mock'lar:
//   ITaskFactory        → CreateExecutionTaskAsync(itemTaskRef) = inner ScriptTask fixture
//   IServiceScopeFactory→ scope.ServiceProvider.GetRequiredService<ITaskExecutionEngine>() = mock engine
//   ITaskExecutionEngine→ options'lı ExecuteAsync overload'ı: başarı + CaptureResponse ile
//                         Response = new StandardTaskResponse { IsSuccess = true, Data = <item'a göre> }
//   IScriptEngine       → mapping'siz senaryoda hiç çağrılmamalı
//   FanOutConcurrencyLimiter → gerçek instance (Options.Create(new FanOutOptions()))

public class FanOutTaskExecutorTests
{
    [Fact]
    public async Task Invoke_Should_Execute_Inner_Task_Per_Item_And_Package_Default_Output()
    {
        // instance data: { "documents": [ {id:"d1"}, {id:"d2"}, {id:"d3"} ] }
        // FanOutTask config: itemsPath=$.documents, join.policy=allSettled, resultKey="documentResults"
        // engine mock: her çağrıda success döner

        var result = await _executor.ExecuteAsync(_context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var response = result.Value!;
        response.IsSuccess.ShouldBeTrue();

        // Engine item başına 1 kez, SuppressDataApply=true + CaptureResponse=true + JournalTaskKey="{fanKey}#{idx}" ile çağrıldı
        await _engine.Received(3).ExecuteAsync(
            Arg.Any<OnExecuteTask>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(),
            Arg.Any<TaskExecutionOrigin>(), Arg.Any<ScriptContext>(),
            Arg.Is<TaskEngineExecutionOptions>(o =>
                o.SuppressDataApply && o.CaptureResponse && o.PreparedTask != null &&
                o.JournalTaskKey!.StartsWith("fan-out-docs#")),
            Arg.Any<CancellationToken>());

        // Default output: resultKey altında 3 sonuç + summary
        var data = (IDictionary<string, object?>)response.Data!;
        var items = (IReadOnlyList<object?>)data["documentResults"]!;
        items.Count.ShouldBe(3);
        var summary = (IDictionary<string, object?>)data["documentResultsSummary"]!;
        summary["total"].ShouldBe(3);
        summary["succeeded"].ShouldBe(3);
    }

    [Fact]
    public async Task Invoke_Should_Succeed_With_Empty_Result_For_Empty_Collection()
    {
        // instance data: { "documents": [] }
        var result = await _executor.ExecuteAsync(_context, CancellationToken.None);
        result.Value!.IsSuccess.ShouldBeTrue();
        await _engine.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!, default, default, default, default!, default!, default);
    }

    [Fact]
    public async Task Invoke_Should_Fail_When_Inner_Task_Is_FanOut() // nested guard
    {
        // factory mock inner task olarak FanOutTask döner
        var result = await _executor.ExecuteAsync(_context, CancellationToken.None);
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage.ShouldContain("nested");
    }

    [Fact]
    public async Task Invoke_Should_Fail_When_No_Item_Source() // itemsPath yok + mapping yok
    {
        var result = await _executor.ExecuteAsync(_context, CancellationToken.None);
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage.ShouldContain("item source");
    }

    [Fact]
    public async Task Invoke_Should_Respect_MaxDegreeOfParallelism()
    {
        // maxDop=2, 6 item; engine mock'u aktif çağrı sayısını sayar (Interlocked), peak <= 2
    }
}
```

Not: default output'un somut temsili (`IDictionary` vs anonymous) Step 3'teki implementasyona göre kesinleşir — assertion'ları oradaki `BuildDefaultOutput` dönüş tipine göre yaz.

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutTaskExecutorTests"`

- [ ] **Step 3: Executor'ı yaz**

```csharp
using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>
/// Executes a FanOutTask: resolves the item collection at runtime, runs the referenced inner
/// task once per item in parallel (bounded by task maxDop AND the process-level bulkhead),
/// joins the outcomes per the configured policy, and produces a SINGLE output that the engine
/// merges into instance data once.
///
/// Orchestration-local by design: remote inner task types still travel to the Execution
/// service through their own executors. Item executions are collect-only (no per-item
/// instance data writes) and journaled as "{fanOutKey}#{index}" InstanceTask rows.
/// Item branch contexts are discarded, never merged — the single write point is the
/// OutputHandler (or the default packaging under join.resultKey).
/// </summary>
public sealed class FanOutTaskExecutor : TaskExecutorBase<FanOutTask>
{
    private readonly ITaskFactory _taskFactory;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IScriptEngine _scriptEngine;
    private readonly FanOutConcurrencyLimiter _bulkhead;

    public FanOutTaskExecutor(
        ITaskFactory taskFactory,
        IServiceScopeFactory serviceScopeFactory,
        IScriptEngine scriptEngine,
        FanOutConcurrencyLimiter bulkhead,
        ILogger<FanOutTaskExecutor> logger)
        : base(logger)
    {
        _taskFactory = taskFactory;
        _serviceScopeFactory = serviceScopeFactory;
        _scriptEngine = scriptEngine;
        _bulkhead = bulkhead;
    }

    public override TaskType TaskType => TaskType.FanOut;

    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        FanOutTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // ---- 1. Mapping'i derle (varsa) ----
        IFanOutMapping? mapping = null;
        var mappingCode = context.OnExecuteTask.Mapping;
        if (mappingCode is not null && mappingCode.HasMappingCode)
        {
            mapping = await _scriptEngine.CompileToInstanceAsync<IFanOutMapping>(
                mappingCode,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: cancellationToken);
        }

        // ---- 2. Item kaynağını çöz (itemsPath XOR ItemSelector) ----
        IReadOnlyList<FanOutItem> items;
        if (task.ItemsPath is not null)
        {
            var selected = mapping is null ? null : await mapping.ItemSelector(context.ScriptContext);
            if (selected is not null)
                return FanOutFailure(
                    "FanOut item source is ambiguous: both itemsPath and a mapping ItemSelector are defined. Use exactly one.",
                    stopwatch);

            items = FanOutItemsResolver.Resolve(
                context.ScriptContext.Instance?.Data, task.ItemsPath);
        }
        else
        {
            var selected = mapping is null ? null : await mapping.ItemSelector(context.ScriptContext);
            if (selected is null)
                return FanOutFailure(
                    "FanOut has no item source: define itemsPath or implement ItemSelector in the mapping.",
                    stopwatch);

            items = selected
                .Select((value, index) => new FanOutItem(index, value, ResolveItemKey(value, index)))
                .ToList();
        }

        // ---- 3. Inner task tanımını bir kez yükle + nested guard ----
        var innerResult = await _taskFactory.CreateExecutionTaskAsync(task.ItemTask!, cancellationToken);
        if (!innerResult.IsSuccess)
            return FanOutFailure(
                $"FanOut inner task '{task.ItemTask}' could not be resolved: {innerResult.Error.Message}",
                stopwatch);

        var innerTemplate = innerResult.Value!;
        if (innerTemplate.GetTaskType() == TaskType.FanOut)
            return FanOutFailure(
                $"FanOut inner task '{innerTemplate.Key}' is itself a FanOutTask; nested fan-out is not allowed (depth 1).",
                stopwatch);

        if (items.Count == 0)
        {
            var emptyResult = new FanOutResult(0, 0, 0, false, []);
            var emptyData = await ProduceOutputAsync(task, mapping, context.ScriptContext, emptyResult, cancellationToken);
            stopwatch.Stop();
            return SuccessInvocation(emptyData, stopwatch);
        }

        // ---- 4. Bounded parallel yürütme ----
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        batchCts.CancelAfter(TimeSpan.FromSeconds(task.BatchTimeoutSeconds));
        using var localLimiter = new SemaphoreSlim(task.MaxDegreeOfParallelism, task.MaxDegreeOfParallelism);

        var settled = new FanOutItemResult?[items.Count];
        var earlyStopLock = new object();
        var earlyStopped = false;

        var itemTasks = items.Select(item => RunItemAsync(item)).ToList();
        await Task.WhenAll(itemTasks);

        async Task RunItemAsync(FanOutItem item)
        {
            var itemStopwatch = Stopwatch.StartNew();
            try
            {
                await localLimiter.WaitAsync(batchCts.Token);
                try
                {
                    await _bulkhead.WaitAsync(batchCts.Token);
                    try
                    {
                        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(batchCts.Token);
                        itemCts.CancelAfter(TimeSpan.FromSeconds(task.ItemTimeoutSeconds));

                        settled[item.Index] = await ExecuteSingleItemAsync(
                            task, innerTemplate, mapping, item, context, itemStopwatch, itemCts.Token);
                    }
                    finally
                    {
                        _bulkhead.Release();
                    }
                }
                finally
                {
                    localLimiter.Release();
                }
            }
            catch (OperationCanceledException)
            {
                itemStopwatch.Stop();
                settled[item.Index] = new FanOutItemResult(
                    item.Index, item.ItemKey, false, null,
                    "FanOut:ItemCancelled", "Item was cancelled (batch timeout, early-stop policy or caller cancellation).",
                    itemStopwatch.Elapsed);
                return;
            }

            // ---- Early-stop politikaları ----
            var outcome = settled[item.Index]!;
            if (task.JoinPolicy == FanOutJoinPolicy.FirstSuccess && outcome.IsSuccess)
                TryEarlyStop();
            else if (task.JoinPolicy == FanOutJoinPolicy.All && !outcome.IsSuccess)
                TryEarlyStop();
        }

        void TryEarlyStop()
        {
            lock (earlyStopLock)
            {
                if (earlyStopped) return;
                earlyStopped = true;
                batchCts.Cancel();
            }
        }

        // ---- 5. Sonuçları topla + join ----
        var timedOut = batchCts.IsCancellationRequested && !earlyStopped && !cancellationToken.IsCancellationRequested;
        var results = settled
            .Select((r, i) => r ?? new FanOutItemResult(
                i, items[i].ItemKey, false, null,
                "FanOut:ItemCancelled", "Item never started (batch closed first).", TimeSpan.Zero))
            .ToList();

        if (!task.Ordered)
            results = results.OrderByDescending(r => r.IsSuccess).ToList();

        var fanOutResult = new FanOutResult(
            results.Count,
            results.Count(r => r.IsSuccess),
            results.Count(r => !r.IsSuccess),
            timedOut,
            results);

        var join = FanOutJoinEvaluator.Evaluate(task.JoinPolicy, task.MinSuccess, results, timedOut);

        // ---- 6. Tek output ----
        var outputData = await ProduceOutputAsync(task, mapping, context.ScriptContext, fanOutResult, cancellationToken);
        stopwatch.Stop();

        return join.IsSuccess
            ? SuccessInvocation(outputData, stopwatch)
            : Result<TaskInvocationResult>.Ok(new TaskInvocationResult
            {
                IsSuccess = false,
                Data = outputData,
                StatusCode = 500,
                ErrorMessage = join.ErrorMessage,
                ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
                TaskType = TaskType.ToString()
            });
    }

    private async Task<FanOutItemResult> ExecuteSingleItemAsync(
        FanOutTask task,
        WorkflowTask innerTemplate,
        IFanOutMapping? mapping,
        FanOutItem item,
        TaskExecutorContext context,
        Stopwatch itemStopwatch,
        CancellationToken cancellationToken)
    {
        // Item izolasyonu: kendi branch context'i + kendi DI scope'u (EF DbContext izolasyonu —
        // TaskCoordinator.ExecuteTaskGroupInParallelAsync ile aynı desen). Branch ASLA merge edilmez.
        var branch = context.ScriptContext.CreateParallelBranch();
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ITaskExecutionEngine>();

        var clone = innerTemplate.Clone();

        if (mapping is not null)
        {
            await mapping.ItemInputHandler(clone, branch, item);
        }
        else
        {
            // Default binding: item değeri branch context Body'si olur. Task config mutasyonu
            // gerektiren inner türler (Http body/url şablonu vb.) ItemInputHandler yazmalıdır.
            branch.SetBody(item.Value);
        }

        var syntheticDef = OnExecuteTask.Create(
            order: item.Index,
            task: task.ItemTask!,
            mapping: null!,                     // item input binding'i yukarıda yapıldı
            errorBoundary: task.ItemErrorBoundary);

        var options = new TaskEngineExecutionOptions
        {
            PreparedTask = clone,
            SuppressDataApply = true,
            JournalTaskKey = $"{task.Key}#{item.Index}",
            CaptureResponse = true
        };

        var result = await engine.ExecuteAsync(
            syntheticDef, context.InstanceTransitionId, context.TaskTrigger,
            context.Origin, branch, options, cancellationToken);

        itemStopwatch.Stop();

        if (!result.IsSuccess)
        {
            return new FanOutItemResult(item.Index, item.ItemKey, false, null,
                result.Error.Code, result.Error.Message, itemStopwatch.Elapsed);
        }

        var execution = result.Value!;
        var response = execution.Response;
        var isSuccess = execution.IsSuccess && !execution.HasFailedTasks;

        return new FanOutItemResult(
            item.Index, item.ItemKey, isSuccess,
            response?.Data,
            isSuccess ? null : execution.TaskError?.NormalizedError.Code ?? "Task:Failed",
            isSuccess ? null : execution.TaskError?.ErrorMessage ?? response?.ErrorMessage,
            itemStopwatch.Elapsed);
    }

    private async Task<object?> ProduceOutputAsync(
        FanOutTask task,
        IFanOutMapping? mapping,
        ScriptContext scriptContext,
        FanOutResult result,
        CancellationToken cancellationToken)
    {
        if (mapping is not null)
        {
            var response = await mapping.OutputHandler(scriptContext, result);
            return response.Data;
        }

        return BuildDefaultOutput(task, result);
    }

    /// <summary>Mapping yoksa: item sonuç dizisi resultKey altına, özet "{resultKey}Summary" altına.</summary>
    private static Dictionary<string, object?> BuildDefaultOutput(FanOutTask task, FanOutResult result)
    {
        return new Dictionary<string, object?>
        {
            [task.ResultKey] = result.Items.Select(i => new Dictionary<string, object?>
            {
                ["index"] = i.Index,
                ["itemKey"] = i.ItemKey,
                ["isSuccess"] = i.IsSuccess,
                ["data"] = (object?)i.Data,
                ["errorCode"] = i.ErrorCode,
                ["errorMessage"] = i.ErrorMessage,
                ["durationMs"] = (long)i.Duration.TotalMilliseconds
            }).ToList(),
            [$"{task.ResultKey}Summary"] = new Dictionary<string, object?>
            {
                ["total"] = result.Total,
                ["succeeded"] = result.Succeeded,
                ["failed"] = result.Failed,
                ["timedOut"] = result.TimedOut
            }
        };
    }

    private static string ResolveItemKey(dynamic? value, int index)
    {
        try
        {
            if (value?.id is string id && id.Length > 0) return id;
            if (value?.key is string key && key.Length > 0) return key;
        }
        catch
        {
            // dynamic binder miss — index'e düş
        }
        return index.ToString();
    }

    private static Result<TaskInvocationResult> SuccessInvocation(object? data, Stopwatch stopwatch)
        => Result<TaskInvocationResult>.Ok(new TaskInvocationResult
        {
            IsSuccess = true,
            Data = data,
            StatusCode = 200,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
            TaskType = TaskType.FanOut.ToString()
        });

    private static Result<TaskInvocationResult> FanOutFailure(string message, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return Result<TaskInvocationResult>.Ok(new TaskInvocationResult
        {
            IsSuccess = false,
            StatusCode = 500,
            ErrorMessage = message,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
            TaskType = TaskType.FanOut.ToString()
        });
    }
}
```

Uygulama notları:
- **`TaskInvocationResult` init'lerini doğrula:** `src/BBT.Workflow.Tasks.Abstractions/Core/TaskInvocationResult.cs`'i oku; property adları/settable'lık farklıysa (`ExecutionDurationMs` vs `DurationMs` gibi) initializer'ları uyarla.
- **`ExecutionError.NormalizedError.Code`:** `ExecutionError`'ın gerçek üyelerini (`src/BBT.Workflow.Application/Execution/ErrorHandling/`) doğrula; kod erişimi farklıysa uyarla.
- **`CompileToInstanceAsync<IFanOutMapping>`:** `IScriptEngine` imzasını doğrula (HttpTaskExecutor:50 kullanımıyla aynı).
- Business-failure yolunda (`IsSuccess=false` + `Data=outputData`) engine, `AcceptedStatusCodes` uygulamaz (FanOut'ta yok) ve output'u instance data'ya YİNE yazar (`ApplyOutputToContextAsync` `response.Data != null` koşuluyla) — bu bilinçli: `all`/`quorum` fail olsa bile sonuç seti data'ya girer, error boundary/auto-transition dallanması bu veriyle çalışır.
- DI kaydı — `AddTaskExecutors` içine:

```csharp
        // FanOut executor (orchestration-local dynamic parallel execution)
        services.AddTaskExecutor<FanOutTaskExecutor>();
```

- [ ] **Step 4: PASS doğrula**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutTaskExecutorTests"`

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Tasks/Executors/FanOut/ src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/TaskServiceCollectionExtensions.cs test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutTaskExecutorTests.cs
git commit -m "feat(fanout): add FanOutTaskExecutor with bounded parallel item execution and single-output join"
```

---

### Task 8: Policy davranış testleri — all/quorum/firstSuccess erken durdurma + timeout

**Files:**
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutTaskExecutorPolicyTests.cs`

- [ ] **Step 1: Testleri yaz** (arrange altyapısını Task 7 test sınıfından paylaş — ortak fixture/builder'a çıkar)

```csharp
public class FanOutTaskExecutorPolicyTests
{
    [Fact]
    public async Task All_Policy_Should_Cancel_Remaining_On_First_Failure()
    {
        // 4 item; engine mock: index 1 fail (hızlı), diğerleri 200ms gecikmeli success.
        // Beklenti: sonuç fail; en az bir item "FanOut:ItemCancelled".
    }

    [Fact]
    public async Task FirstSuccess_Policy_Should_Cancel_Remaining_On_First_Success()
    {
        // 4 item; engine mock: index 0 success (hızlı), diğerleri 500ms gecikmeli.
        // Beklenti: sonuç success; kalan item'lardan en az biri cancelled;
        // toplam süre << 4×500ms.
    }

    [Fact]
    public async Task Quorum_Policy_Should_Succeed_When_MinSuccess_Met()
    {
        // 3 item, minSuccess=2; engine mock: 2 success 1 fail → task success, summary.failed=1
    }

    [Fact]
    public async Task Batch_Timeout_Should_Force_Settle_With_TimedOut_Flag()
    {
        // batchTimeoutSeconds=1; engine mock: tüm item'lar 5sn bekler (Task.Delay(5000, ct)).
        // allSettled → task success, tüm item'lar FanOut:ItemCancelled, summary.timedOut=true.
        // İkinci varyant: policy=all → task fail, hata mesajı "timed out" içerir.
    }

    [Fact]
    public async Task Item_Failure_With_AllSettled_Should_Not_Fail_Task()
    {
        // 3 item, 1 fail → task success; result seti errorCode taşır.
    }
}
```

Her testte engine mock'unun `Task.Delay`'leri verilen `CancellationToken`'ı **kullanmalı** ki iptal gerçekçi test edilsin.

- [ ] **Step 2: FAIL/PASS döngüsü** — davranış Task 7'de yazıldıysa testler direkt geçebilir; geçmeyen her test bir davranış eksiğidir, executor'da düzelt (test değiştirme yasak — test doğru davranışı tanımlar).

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutTaskExecutorPolicyTests"`

- [ ] **Step 3: Commit**

```bash
git add test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutTaskExecutorPolicyTests.cs src/BBT.Workflow.Application/Tasks/Executors/FanOut/
git commit -m "test(fanout): pin join policy early-stop, timeout and partial-failure behavior"
```

---

### Task 9: Mapping yolu — `ItemInputHandler` + `OutputHandler` script entegrasyonu

**Files:**
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutTaskExecutorMappingTests.cs`

- [ ] **Step 1: Testler**

```csharp
public class FanOutTaskExecutorMappingTests
{
    // IScriptEngine mock: CompileToInstanceAsync<IFanOutMapping> → elle yazılmış fake mapping döner.

    [Fact]
    public async Task ItemInputHandler_Should_Receive_Cloned_Task_And_Branch_Context()
    {
        // Fake mapping ItemInputHandler'da: gördüğü task referanslarını listeye toplar.
        // Beklenti: 3 item → 3 FARKLI task instance'ı (klon), hiçbiri template'in kendisi değil;
        // her çağrının context'i parent ScriptContext'ten farklı (branch).
    }

    [Fact]
    public async Task OutputHandler_Should_Be_Called_Exactly_Once_With_Ordered_Results()
    {
        // Fake mapping OutputHandler çağrılarını sayar; FanOutResult.Items index sıralı gelir.
        // Dönen ScriptResponse.Data task çıktısı olur (TaskInvocationResult.Data).
    }

    [Fact]
    public async Task ItemSelector_Should_Provide_Items_When_No_ItemsPath()
    {
        // Config'de itemsPath yok; fake ItemSelector 2 item döner → 2 inner çağrı.
    }

    [Fact]
    public async Task Ambiguous_Source_Should_Fail() // itemsPath VAR + ItemSelector non-null
    {
        // Beklenti: IsSuccess=false, mesaj "ambiguous" içerir, hiç inner çağrı yok.
    }
}
```

- [ ] **Step 2: FAIL/PASS döngüsü, executor'da eksik davranışı tamamla**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOutTaskExecutorMappingTests"`

- [ ] **Step 3: Commit**

```bash
git add test/BBT.Workflow.Application.Tests/Tasks/Executors/FanOutTaskExecutorMappingTests.cs src/BBT.Workflow.Application/Tasks/Executors/FanOut/
git commit -m "test(fanout): pin IFanOutMapping integration (item input binding, single output, selector XOR)"
```

---

### Task 10: Observability — WorkflowLogs + span'ler + metrikler

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Executors/FanOut/FanOutTaskExecutor.cs`
- Modify: `IWorkflowMetrics` + implementasyonu (yerini bul: `grep -rn "interface IWorkflowMetrics" src/`)

- [ ] **Step 1: WorkflowLogs'a LoggerMessage blokları ekle** (10xxx task serisi; son kullanılan id'yi doğrula: `grep -n "EventId = 101" src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs | tail -3` — çakışmayan bir blok seç, aşağıda 10110+ varsayıldı)

```csharp
    [LoggerMessage(EventId = 10110, Level = LogLevel.Information,
        Message = "FanOut batch started. Task: {TaskKey}, Items: {ItemCount}, MaxDop: {MaxDop}, Policy: {JoinPolicy}, Instance: {InstanceId}")]
    public static partial void FanOutBatchStarted(this ILogger logger, string taskKey, int itemCount, int maxDop, string joinPolicy, Guid instanceId);

    [LoggerMessage(EventId = 10111, Level = LogLevel.Warning,
        Message = "FanOut item failed. Task: {TaskKey}, ItemKey: {ItemKey}, Index: {ItemIndex}, ErrorCode: {ErrorCode}, Instance: {InstanceId}")]
    public static partial void FanOutItemFailed(this ILogger logger, string taskKey, string itemKey, int itemIndex, string? errorCode, Guid instanceId);

    [LoggerMessage(EventId = 10112, Level = LogLevel.Information,
        Message = "FanOut batch completed. Task: {TaskKey}, Total: {Total}, Succeeded: {Succeeded}, Failed: {Failed}, DurationMs: {DurationMs}, Instance: {InstanceId}")]
    public static partial void FanOutBatchCompleted(this ILogger logger, string taskKey, int total, int succeeded, int failed, long durationMs, Guid instanceId);

    [LoggerMessage(EventId = 10113, Level = LogLevel.Warning,
        Message = "FanOut batch timed out. Task: {TaskKey}, Settled: {Settled}/{Total}, BatchTimeoutSeconds: {TimeoutSeconds}, Instance: {InstanceId}")]
    public static partial void FanOutBatchTimedOut(this ILogger logger, string taskKey, int settled, int total, int timeoutSeconds, Guid instanceId);

    [LoggerMessage(EventId = 10114, Level = LogLevel.Warning,
        Message = "FanOut global bulkhead saturated; item waiting for a slot. Task: {TaskKey}, ActiveItems: {ActiveItems}, MaxConcurrentItems: {MaxConcurrentItems}")]
    public static partial void FanOutBulkheadSaturated(this ILogger logger, string taskKey, int activeItems, int maxConcurrentItems);
```

(Dosyadaki mevcut partial sınıf/using düzenine uy; extension imzaları dosyadaki desenle aynı olsun — mevcutlar `this ILogger logger` almıyorsa oradaki formu kopyala.)

- [ ] **Step 2: Executor'a log + span'leri işle**

- Batch başında: `Logger.FanOutBatchStarted(task.Key, items.Count, task.MaxDegreeOfParallelism, task.JoinPolicy.ToString(), instanceId)`.
- `RunItemAsync` içinde bulkhead beklemeden önce `if (_bulkhead.ActiveCount >= kapasite)` → `FanOutBulkheadSaturated` (kapasiteyi `FanOutOptions`'tan al — limiter'a `Capacity` property'si ekle).
- Item fail → `FanOutItemFailed`; batch sonunda `FanOutBatchCompleted` / timeout'ta `FanOutBatchTimedOut`.
- Item span'i: `ExecuteSingleItemAsync` başına `using (TaskExecutionActivityHelper.StartActivity($"FanOutItem[{item.Index}]", task.Key, TaskType.ToString()))` — helper imzasını `TaskExecutorBase` kullanımından doğrula; `item_key` tag'i ekleyebiliyorsa ekle.

- [ ] **Step 3: Metrikler**

`IWorkflowMetrics`'e ekle (implementasyonu ve test fake'lerini bul: `grep -rln "IWorkflowMetrics" src/ test/`):

```csharp
    /// <summary>Records a completed fan-out batch (size, duration, success/failure split).</summary>
    void RecordFanOutBatch(string taskKey, string workflowKey, int total, int succeeded, int failed, double durationSeconds);
```

Implementasyonda: `fanout.batch.size` histogram, `fanout.batch.duration` histogram, `fanout.item.failures` counter (`error_code` etiketi bu seviyede yoksa `failed` sayısıyla). `fanout.concurrency.active` gauge'ı için implementasyon sınıfında `ObservableGauge` kaydet ve `FanOutConcurrencyLimiter.ActiveCount`'u okut (limiter'ı metrics sınıfına ctor'dan ver; DI'da ikisi de singleton olmalı — metrics singleton değilse gauge'ı atla ve plana not düş). Item süre histogramı zaten engine'in task metriklerinden geliyor (her item bir task).

- [ ] **Step 4: Derle + ilgili testleri koştur**

Run: `dotnet build && dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FanOut"`

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs src/BBT.Workflow.Application/Tasks/Executors/FanOut/ src/BBT.Workflow.Domain/Monitoring/
git commit -m "feat(fanout): add structured logs, item spans and batch metrics"
```

---

### Task 11: Konfig + meta + dokümantasyon

**Files:**
- Modify: Orchestration host `appsettings.json` (`orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`)
- Modify: `vnext-meta/features.json`, `vnext-meta/component-registry.json`
- Create: `docs/domain/fan-out-task.md`
- Modify: `docs/README.md` (navigasyon)

- [ ] **Step 1: appsettings** — Orchestration host'un `appsettings.json`'ına (mevcut `Workflow` bölümünün altına):

```json
"Workflow": {
  "FanOut": {
    "MaxConcurrentItems": 64
  }
}
```

(Mevcut `Workflow` node'u varsa içine merge et; Execution host'a GEREKMEZ — FanOut orchestration-local.)

- [ ] **Step 2: vnext-meta** — `component-registry.json`'a task tipi 21 kaydı (mevcut task entry'lerinin şemasını birebir takip et: key/type/availability alanları), `features.json`'a `fan-out-task` feature'ı (inline mode, 4 join policy, bulkhead limitleri). Sonra `/vnext-meta-validator` skill'i ile doğrula.

- [ ] **Step 3: Doküman** — `docs/domain/fan-out-task.md`: spec §3-§9'un developer-facing özeti — config şeması, IFanOutMapping örneği (spec'teki DocumentFanOutMapping örneğini yeni imzalara uyarla), join policy tablosu, default binding sınırı (amendment #5), Human/Timer inner uyarısı, `{taskKey}#{index}` journal modeli, bulkhead config. `docs/README.md` navigasyonuna ekle.

- [ ] **Step 4: Commit**

```bash
git add orchestration/ vnext-meta/ docs/
git commit -m "docs(fanout): add developer guide, meta registry entry and default bulkhead config"
```

---

### Task 12: Regresyon süpürmesi + Helm hatırlatması

- [ ] **Step 1: Dokunulan alanların tam test süitini koştur**

```bash
dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~FanOut|FullyQualifiedName~WorkflowTask|FullyQualifiedName~TaskEnums"
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskExecutionEngine|FullyQualifiedName~TaskCoordinator|FullyQualifiedName~FanOut|FullyQualifiedName~Executor"
```

Expected: yeni testler + dokunulan mevcut testler PASS. (Suite genelindeki ~191 pre-existing failure baseline'dır — yalnızca BU filtrelerdeki sonuçlar kriter.)

- [ ] **Step 2: Kullanıcıya Helm hatırlatması** — `Workflow__FanOut__MaxConcurrentItems` env değişkeninin vnext-helm-charts'ta ayarlanabilir olması gerektiğini final raporunda belirt (default'u olduğu için bloklamaz; CLAUDE.local.md kuralı).

- [ ] **Step 3: Commit (kalan değişiklik varsa) ve branch'i toparla**

---

### Task 13: Integration test (vnext-example) — AYRI REPO, kod tamamlandıktan sonra

CLAUDE.local.md politikası: temel sürece yeni primitif ekleyen major geliştirme → integration test **zorunlu**. Bu görev `/Users/U0B006/Documents/repos/burgan-tech/vnext-example` reposunda çalışır ve lokal runtime gerektirir.

- [ ] **Step 1: Senaryo bileşenleri** — yeni akış `fan-out-documents`: instance data'ya N doküman listesi alan bir başlangıç, OnEntry'de FanOutTask (inner: MockLab'a giden HttpTask, `allSettled`, maxDop 3), sonrasında `documentResultsSummary.failed > 0` koşuluyla `partial-failure` state'ine, aksi halde `completed`'a giden auto-transition'lar. Bileşen üretiminde `vnext-ai-toolkit` skill'leri kullanılabilir; **plugin şeması type 21'i bilmez** — FanOut task JSON'ını bu plandaki config şemasına göre elle yaz (CLAUDE.local.md §3 uyarısı).
- [ ] **Step 2: Integration test** — `tests/Core.IntegrationTests/Tests/FanOut/`: (a) 5 dokümanlık batch → completed, instance data'da 5 sonuç + summary, **tek** yeni data versiyonu (patch sayısı asserti); (b) MockLab'da 2 doküman fail → partial-failure state; (c) journal: `fan-out-documents#N` formatında 5 item InstanceTask kaydı. `test.runsettings`'te `VNEXT_BASE_URL=http://localhost:4201` (lokal runtime, image DEĞİL).
- [ ] **Step 3: Yük testi** — `api-tests/fan-out-documents/fanout-load.py`: M eşzamanlı instance × N item; ölçüm: downstream'e giden eşzamanlı istek `MaxConcurrentItems`'ı aşmıyor (MockLab tarafında eşzamanlılık sayacı), straggler oranı raporu. README'ye çalıştırma komutu + eşikler.
- [ ] **Step 4: Dokümantasyon** — senaryo `README.md` (neyi denetliyor/neden var/akış/çalıştırma/başarı kriteri) + `TEST-SCENARIOS.md` tablosuna satır — **aynı commit'te**.
- [ ] **Step 5: Çalıştırma** — vNext tarafında altyapı (`cd etc/docker && ./run-docker.sh` — zaten ayaktaysa atlama kuralı) + 4 app `--launch-profile http` ile; testleri koştur, sonuçları raporla.

---

## Self-Review Kaydı

- **Spec kapsaması:** §3 tanım→Task 1; §5 mapping→Task 2; §4 collect-only/engine→Task 3; §7 bulkhead→Task 4; §4.1 items çözümü→Task 5; §6 join→Task 6, 8; §4 akış→Task 7; §5 mapping entegrasyonu→Task 9; §9 observability→Task 10; §3 global config + §11 meta/şema/doc→Task 11; §13 test stratejisi→Task 8, 12, 13. §10 validation → Configure (Task 1) + executor preflight (Task 7), amendment #3 gereği WorkflowValidator'a görev yok. vnext-schema (ayrı repo) task şemasına type 21 eklenmesi bu planın dışında — final raporda kullanıcıya hatırlatılacak.
- **Tip tutarlılığı:** `FanOutItem/FanOutResult/FanOutItemResult` Task 2'de tanımlı, Task 5/6/7 aynı imzaları kullanıyor; `TaskEngineExecutionOptions` Task 3'te tanımlı, Task 7 aynı property'leri kullanıyor; `Attempts` hiçbir yerde yok (amendment #2).
- **Bilinen doğrulama noktaları (mühendise bırakılan):** `TaskInvocationResult` initializer'ları, `ExecutionError` üye adları, `IScriptEngine.CompileToInstanceAsync` imzası, `InstanceTask.TaskId` adı, Json→dynamic helper — her biri ilgili görevde açık "doğrula" adımı olarak işaretli.
