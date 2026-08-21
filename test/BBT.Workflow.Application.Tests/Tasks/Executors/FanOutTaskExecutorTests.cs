using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Executors.FanOut;
using BBT.Workflow.Tasks.Factory;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Behavioural tests for <see cref="FanOutTaskExecutor"/>: one inner-task execution per item,
/// bounded parallelism, and exactly one joined output. The <see cref="FanOutHarness"/> below is
/// deliberately reusable — the deeper join-policy / timeout / mapping suites build on it.
/// </summary>
public sealed class FanOutTaskExecutorTests
{
    [Fact]
    public async Task Execute_RunsInnerTaskOncePerItem_AndProducesOneJoinedOutput()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new
            {
                documents = new[]
                {
                    new { id = "doc-a" }, new { id = "doc-b" }, new { id = "doc-c" }
                }
            });

        var response = await harness.ExecuteAsync();

        response.IsSuccess.ShouldBeTrue();
        response.Value!.IsSuccess.ShouldBeTrue();

        // One inner execution per item, each on its own DI scope.
        harness.Engine.Calls.Count.ShouldBe(3);
        harness.ScopeFactory.ScopesCreated.ShouldBe(3);

        // Every item runs collect-only with a distinct journal identity and a pre-bound task —
        // this trio is what makes "N executions, one write" true.
        harness.Engine.Calls.Select(c => c.JournalTaskKey)
            .ShouldBe(["fan-out-docs#0", "fan-out-docs#1", "fan-out-docs#2"], ignoreOrder: true);
        harness.Engine.Calls.ShouldAllBe(c => c.SuppressDataApply);
        harness.Engine.Calls.ShouldAllBe(c => c.CaptureResponse);
        harness.Engine.Calls.ShouldAllBe(c => c.PreparedTask != null);

        // The single output: item results under the result key plus the batch summary.
        var output = harness.OutputAsJson((object?)response.Value!.Data);
        var results = output.GetProperty("fanOutResults");
        results.GetArrayLength().ShouldBe(3);
        results.EnumerateArray().Select(r => r.GetProperty("itemKey").GetString())
            .ShouldBe(["doc-a", "doc-b", "doc-c"]);
        results.EnumerateArray().ShouldAllBe(r => r.GetProperty("isSuccess").GetBoolean());

        var summary = output.GetProperty("fanOutResultsSummary");
        summary.GetProperty("total").GetInt32().ShouldBe(3);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(3);
        summary.GetProperty("failed").GetInt32().ShouldBe(0);
        summary.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_AlwaysReturnsResultsInItemIndexOrder_EvenWhenItemsSettleOutOfOrder()
    {
        // Item 0 is slowest, item 2 fastest — completion order is the reverse of index order.
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new
            {
                documents = new[] { new { id = "slow" }, new { id = "mid" }, new { id = "fast" } }
            });
        harness.Engine.DelayPerCall = order => TimeSpan.FromMilliseconds(60 - (order * 25));

        var response = await harness.ExecuteAsync();

        var results = harness.OutputAsJson((object?)response.Value!.Data).GetProperty("fanOutResults");
        results.EnumerateArray().Select(r => r.GetProperty("itemKey").GetString())
            .ShouldBe(["slow", "mid", "fast"]);
        results.EnumerateArray().Select(r => r.GetProperty("index").GetInt32()).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task Execute_EmptyCollection_ExecutesNothing_AndSucceedsUnderAllSettled()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new { documents = Array.Empty<object>() });

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.ShouldBeEmpty();

        var output = harness.OutputAsJson((object?)response.Value!.Data);
        output.GetProperty("fanOutResults").GetArrayLength().ShouldBe(0);
        output.GetProperty("fanOutResultsSummary").GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Execute_EmptyCollection_StillFailsTheJoin_WhenPolicyNeedsASuccess()
    {
        // An empty batch is NOT unconditionally successful: it runs through the same join
        // evaluation as a non-empty one, and firstSuccess cannot be met by zero items.
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new { documents = Array.Empty<object>() },
            joinPolicy: FanOutJoinPolicy.FirstSuccess);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        harness.Engine.Calls.ShouldBeEmpty();
        // The output still lands so the flow can branch on it.
        harness.OutputAsJson((object?)response.Value!.Data).GetProperty("fanOutResultsSummary")
            .GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Execute_FailedJoin_StillCarriesTheResultSet()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new { documents = new[] { new { id = "ok" }, new { id = "bad" } } },
            joinPolicy: FanOutJoinPolicy.AllSettled);
        harness.Engine.FailOrders.Add(1);

        var response = await harness.ExecuteAsync();

        var output = harness.OutputAsJson((object?)response.Value!.Data);
        var summary = output.GetProperty("fanOutResultsSummary");
        summary.GetProperty("succeeded").GetInt32().ShouldBe(1);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        output.GetProperty("fanOutResults").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Execute_WhenCallerCancelsMidBatch_PropagatesCancellation_InsteadOfABusinessFailure()
    {
        // A torn-down transition must not come back as a 500 business failure that the error
        // boundary could then retry — the cancellation has to escape the executor.
        using var cts = new CancellationTokenSource();
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new
            {
                documents = new[] { new { id = "a" }, new { id = "b" }, new { id = "c" } }
            },
            maxDop: 1);
        harness.Engine.DelayPerCall = _ => TimeSpan.FromMilliseconds(50);
        harness.Engine.OnCallStarted = call =>
        {
            if (call.Order == 0)
            {
                cts.Cancel();
            }
        };

        await Should.ThrowAsync<OperationCanceledException>(() => harness.ExecuteAsync(cts.Token));
    }

    [Fact]
    public async Task Execute_FailedItem_KeepsItsResponsePayload()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new { documents = new[] { new { id = "ok" }, new { id = "bad" } } });
        harness.Engine.FailOrders.Add(1);

        var response = await harness.ExecuteAsync();

        // Under allSettled the author inspects the failed item's body to decide what to do —
        // the payload must survive the failure, not just the message.
        var failed = harness.OutputAsJson((object?)response.Value!.Data)
            .GetProperty("fanOutResults")
            .EnumerateArray()
            .Single(r => !r.GetProperty("isSuccess").GetBoolean());

        failed.GetProperty("data").GetProperty("order").GetInt32().ShouldBe(1);
        failed.GetProperty("errorMessage").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Execute_WhenInnerTaskIsItselfFanOut_Fails_WithoutRunningAnyItem()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new { documents = new[] { new { id = "doc-a" } } },
            innerTemplate: FanOutHarness.CreateFanOutTask("inner-fan-out"));

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage.ShouldNotBeNull();
        response.Value!.ErrorMessage!.ShouldContain("nest", Case.Insensitive);
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WithNoItemsPathAndNoMapping_Fails_MentioningTheItemSource()
    {
        var harness = new FanOutHarness(itemsPath: null);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("item source", Case.Insensitive);
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WithBothItemsPathAndItemSelector_Fails_AsAmbiguous()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new { documents = new[] { new { id = "doc-a" } } },
            mapping: new StubFanOutMapping { Items = [new { id = "from-selector" }] });

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("ambiguous", Case.Insensitive);
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WithItemSelector_FansOutOverTheSelectedValues()
    {
        var mapping = new StubFanOutMapping
        {
            Items = [new { id = "s-1" }, new { id = "s-2" }]
        };
        var harness = new FanOutHarness(itemsPath: null, mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.Count.ShouldBe(2);
        // The mapping binds every item, and the batch produces exactly ONE output-handler call.
        mapping.BoundItems.Count.ShouldBe(2);
        mapping.BoundItems.Select(i => i.ItemKey).ShouldBe(["s-1", "s-2"], ignoreOrder: true);
        mapping.OutputCalls.Count.ShouldBe(1);
        mapping.OutputCalls[0].Total.ShouldBe(2);
        mapping.OutputCalls[0].Succeeded.ShouldBe(2);
    }

    [Fact]
    public async Task Execute_NeverExceedsMaxDegreeOfParallelism()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new
            {
                documents = Enumerable.Range(0, 6).Select(i => new { id = $"doc-{i}" }).ToArray()
            },
            maxDop: 2);
        harness.Engine.DelayPerCall = _ => TimeSpan.FromMilliseconds(40);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.Count.ShouldBe(6);
        harness.Engine.PeakConcurrency.ShouldBeLessThanOrEqualTo(2);
        // Guard against a false pass from accidental serialisation: with 6 items and maxDop 2 the
        // batch must genuinely overlap.
        harness.Engine.PeakConcurrency.ShouldBe(2);
    }

    [Fact]
    public async Task Execute_DiscardsItemBranchContexts_LeavingTheBatchContextUntouched()
    {
        var harness = new FanOutHarness(
            itemsPath: "$.documents",
            instanceData: new { documents = new[] { new { id = "doc-a" }, new { id = "doc-b" } } });

        await harness.ExecuteAsync();

        // Each item ran on its own branch, and none of them was merged back: N items would
        // otherwise collide on the same inner task key in the batch context.
        harness.Engine.Calls.Select(c => c.Context).Distinct().Count().ShouldBe(2);
        harness.Engine.Calls.ShouldAllBe(c => !ReferenceEquals(c.Context, harness.ScriptContext));
        harness.ScriptContext.TaskResponse.ShouldBeEmpty();
    }
}

/// <summary>
/// Reusable arrange scaffolding for <see cref="FanOutTaskExecutor"/> tests: a recording task
/// execution engine, a scope factory that hands it out, a real concurrency limiter, and a
/// FanOutTask built from JSON.
/// </summary>
internal sealed class FanOutHarness
{
    private readonly FanOutTask _task;
    private readonly ScriptCode _mappingCode;

    public FanOutHarness(
        string? itemsPath = "$.documents",
        object? instanceData = null,
        StubFanOutMapping? mapping = null,
        FanOutJoinPolicy joinPolicy = FanOutJoinPolicy.AllSettled,
        int? minSuccess = null,
        int maxDop = 4,
        int itemTimeoutSeconds = 5,
        int batchTimeoutSeconds = 30,
        WorkflowTask? innerTemplate = null)
    {
        Template = innerTemplate ?? WorkflowTaskFactory.CreateHttpTask("process-document");
        TaskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(Template));

        _mappingCode = mapping is null
            ? ScriptCode.FromNative(string.Empty)
            : ScriptCode.FromNative("// fan-out mapping");

        if (mapping is not null)
        {
            ScriptEngine.CompileToInstanceAsync<IFanOutMapping>(
                    Arg.Any<ScriptCode>(),
                    Arg.Any<ScriptSettings?>(),
                    Arg.Any<IEnumerable<MetadataReference>?>(),
                    Arg.Any<IEnumerable<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(mapping);
        }

        var join = new Dictionary<string, object?>
        {
            ["policy"] = joinPolicy switch
            {
                FanOutJoinPolicy.All => "all",
                FanOutJoinPolicy.AllSettled => "allSettled",
                FanOutJoinPolicy.Quorum => "quorum",
                _ => "firstSuccess"
            },
            ["resultKey"] = "fanOutResults"
        };
        if (minSuccess is not null)
        {
            join["minSuccess"] = minSuccess;
        }

        var config = new Dictionary<string, object?>
        {
            ["mode"] = "inline",
            ["task"] = new { key = "process-document", domain = "core", flow = "sys-tasks", version = "1.0.0" },
            ["execution"] = new
            {
                maxDegreeOfParallelism = maxDop,
                itemTimeoutSeconds,
                batchTimeoutSeconds
            },
            ["join"] = join
        };
        if (itemsPath is not null)
        {
            config["itemsPath"] = itemsPath;
        }

        _task = FanOutTask.Create(JsonSerializer.SerializeToElement(config));
        _task.SetReference(new Reference("fan-out-docs", "core", "sys-tasks", "1.0.0"));

        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        if (instanceData is not null)
        {
            instance.SeedData(Guid.NewGuid(), new JsonData(JsonSerializer.SerializeToElement(instanceData)));
        }

        ScriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .Build();

        ScopeFactory = new RecordingScopeFactory(Engine);

        Executor = new FanOutTaskExecutor(
            ScriptEngine,
            TaskFactory,
            ScopeFactory,
            new FanOutConcurrencyLimiter(Options.Create(new FanOutOptions())),
            NullLogger<FanOutTaskExecutor>.Instance);
    }

    public ITaskFactory TaskFactory { get; } = Substitute.For<ITaskFactory>();

    public IScriptEngine ScriptEngine { get; } = Substitute.For<IScriptEngine>();

    public RecordingTaskExecutionEngine Engine { get; } = new();

    public RecordingScopeFactory ScopeFactory { get; }

    public ScriptContext ScriptContext { get; }

    public WorkflowTask Template { get; }

    public FanOutTaskExecutor Executor { get; }

    public Task<Result<StandardTaskResponse>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var onExecute = OnExecuteTask.Create(1, _task, _mappingCode);
        var context = new TaskExecutorContext(
            _task, onExecute, ScriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
        return Executor.ExecuteAsync(context, cancellationToken);
    }

    /// <summary>Round-trips the executor's output data through JSON for structural assertions.</summary>
    public JsonElement OutputAsJson(object? data) =>
        JsonSerializer.SerializeToElement(data, JsonSerializerConstants.JsonOptions);

    /// <summary>Builds a nested FanOutTask, used to prove the depth-1 guard.</summary>
    public static FanOutTask CreateFanOutTask(string key)
    {
        var task = FanOutTask.Create(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["mode"] = "inline",
            ["itemsPath"] = "$.inner",
            ["task"] = new { key = "leaf", domain = "core", flow = "sys-tasks", version = "1.0.0" }
        }));
        task.SetReference(new Reference(key, "core", "sys-tasks", "1.0.0"));
        return task;
    }
}

/// <summary>
/// Hand-written <see cref="ITaskExecutionEngine"/> fake. Hand-written rather than mocked because
/// fan-out calls it from many threads at once and the tests assert on observed concurrency.
/// </summary>
internal sealed class RecordingTaskExecutionEngine : ITaskExecutionEngine
{
    private readonly ConcurrentQueue<EngineCall> _calls = new();
    private int _active;
    private int _peak;

    /// <summary>Orders (item indexes) whose execution should report a business failure.</summary>
    public HashSet<int> FailOrders { get; } = [];

    /// <summary>Optional per-call delay, keyed by the item's order, to shape completion timing.</summary>
    public Func<int, TimeSpan>? DelayPerCall { get; set; }

    /// <summary>Invoked as a call begins, before its delay — lets a test act mid-batch.</summary>
    public Action<EngineCall>? OnCallStarted { get; set; }

    public IReadOnlyList<EngineCall> Calls => _calls.ToArray();

    public int PeakConcurrency => Volatile.Read(ref _peak);

    public Task<Result<TasksExecutionResult>> ExecuteAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        ScriptContext context,
        CancellationToken cancellationToken)
        => ExecuteAsync(onExecuteTask, instanceTransitionId, taskTrigger, origin, context,
            TaskEngineExecutionOptions.Default, cancellationToken);

    public async Task<Result<TasksExecutionResult>> ExecuteAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        TaskExecutionOrigin origin,
        ScriptContext context,
        TaskEngineExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        UpdatePeak(active);
        try
        {
            var call = new EngineCall(
                onExecuteTask.Order,
                options.JournalTaskKey,
                options.SuppressDataApply,
                options.CaptureResponse,
                options.PreparedTask,
                context,
                taskTrigger,
                origin);
            _calls.Enqueue(call);
            OnCallStarted?.Invoke(call);

            var delay = DelayPerCall?.Invoke(onExecuteTask.Order) ?? TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            if (FailOrders.Contains(onExecuteTask.Order))
            {
                return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
                {
                    IsSuccess = false,
                    HasFailedTasks = true,
                    FailedTask = onExecuteTask,
                    TaskError = new ExecutionError
                    {
                        TaskKey = onExecuteTask.Task.Key,
                        TaskType = "Http",
                        ErrorMessage = $"item {onExecuteTask.Order} failed",
                        NormalizedError = new NormalizedError { Code = "Item:Failed", Message = "boom" }
                    },
                    Response = new StandardTaskResponse
                    {
                        IsSuccess = false,
                        StatusCode = 502,
                        ErrorMessage = $"item {onExecuteTask.Order} failed",
                        Data = new Dictionary<string, object?> { ["order"] = onExecuteTask.Order }
                    }
                });
            }

            return Result<TasksExecutionResult>.Ok(new TasksExecutionResult
            {
                IsSuccess = true,
                Response = new StandardTaskResponse
                {
                    IsSuccess = true,
                    StatusCode = 200,
                    Data = new Dictionary<string, object?> { ["order"] = onExecuteTask.Order }
                }
            });
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private void UpdatePeak(int observed)
    {
        var current = Volatile.Read(ref _peak);
        while (observed > current)
        {
            var previous = Interlocked.CompareExchange(ref _peak, observed, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}

/// <summary>A single recorded inner-task execution.</summary>
internal sealed record EngineCall(
    int Order,
    string? JournalTaskKey,
    bool SuppressDataApply,
    bool CaptureResponse,
    WorkflowTask? PreparedTask,
    ScriptContext Context,
    TaskTrigger TaskTrigger,
    TaskExecutionOrigin Origin);

/// <summary>Scope factory that counts scopes and resolves the recording engine from every one.</summary>
internal sealed class RecordingScopeFactory(ITaskExecutionEngine engine)
    : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    private int _scopesCreated;

    public int ScopesCreated => Volatile.Read(ref _scopesCreated);

    public IServiceScope CreateScope()
    {
        Interlocked.Increment(ref _scopesCreated);
        return this;
    }

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType) =>
        serviceType == typeof(ITaskExecutionEngine) ? engine : null;

    public void Dispose()
    {
    }
}

/// <summary>Scriptless <see cref="IFanOutMapping"/> stand-in for the compiled mapping.</summary>
internal sealed class StubFanOutMapping : IFanOutMapping
{
    private readonly object _lock = new();

    /// <summary>Values returned by <see cref="ItemSelector"/>; null means "use itemsPath".</summary>
    public IEnumerable<dynamic>? Items { get; init; }

    public List<FanOutItem> BoundItems { get; } = [];

    public List<FanOutResult> OutputCalls { get; } = [];

    public Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context)
        => Task.FromResult(Items);

    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        lock (_lock)
        {
            BoundItems.Add(item);
        }

        context.SetBody(item.Value);
        return Task.FromResult(new ScriptResponse { Data = item.Value });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result)
    {
        lock (_lock)
        {
            OutputCalls.Add(result);
        }

        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object?>
            {
                ["total"] = result.Total,
                ["succeeded"] = result.Succeeded
            }
        });
    }
}
