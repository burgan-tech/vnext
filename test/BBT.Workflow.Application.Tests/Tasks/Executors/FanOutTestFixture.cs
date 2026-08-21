using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Shared arrange scaffolding for the <see cref="FanOutTaskExecutor"/> suites: a recording task
/// execution engine, a scope factory that hands it out, a real concurrency limiter, and a
/// FanOutTask built from JSON.
/// </summary>
/// <remarks>
/// Lives in its own file, and depends on no test class, so the join-policy/timeout and mapping
/// suites can build on it without inheriting from or referencing each other.
/// </remarks>
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
        string resultKey = "fanOutResults",
        WorkflowTask? innerTemplate = null,
        FanOutOptions? fanOutOptions = null,
        Reference? itemTaskReference = null,
        FanOutTask? taskOverride = null)
    {
        Mapping = mapping;
        ResultKey = resultKey;
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
            ["resultKey"] = resultKey
        };
        if (minSuccess is not null)
        {
            join["minSuccess"] = minSuccess;
        }

        var itemTask = itemTaskReference ?? new Reference("process-document", "core", "sys-tasks", "1.0.0");
        var config = new Dictionary<string, object?>
        {
            ["mode"] = "inline",
            ["task"] = new
            {
                key = itemTask.Key,
                domain = itemTask.Domain,
                flow = itemTask.Flow,
                version = itemTask.Version
            },
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

        // taskOverride exists for the shapes JSON cannot express — notably a FanOutTask with no
        // 'task' reference, which Configure rejects but the executor still guards against.
        _task = taskOverride ?? FanOutTask.Create(JsonSerializer.SerializeToElement(config));
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

        // A REAL limiter, configurable so a test can squeeze the process-wide bulkhead below the
        // task's own maxDegreeOfParallelism and observe which of the two actually binds.
        Limiter = new FanOutConcurrencyLimiter(Options.Create(fanOutOptions ?? new FanOutOptions()));

        Executor = new FanOutTaskExecutor(
            ScriptEngine,
            TaskFactory,
            ScopeFactory,
            Limiter,
            NullLogger<FanOutTaskExecutor>.Instance);
    }

    public ITaskFactory TaskFactory { get; } = Substitute.For<ITaskFactory>();

    public IScriptEngine ScriptEngine { get; } = Substitute.For<IScriptEngine>();

    public RecordingTaskExecutionEngine Engine { get; } = new();

    public RecordingScopeFactory ScopeFactory { get; }

    public FanOutConcurrencyLimiter Limiter { get; }

    public ScriptContext ScriptContext { get; }

    public WorkflowTask Template { get; }

    public StubFanOutMapping? Mapping { get; }

    public string ResultKey { get; }

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

    /// <summary>The default packaging's per-item result array, from a completed execution.</summary>
    public JsonElement ItemResults(Result<StandardTaskResponse> response) =>
        OutputAsJson((object?)response.Value!.Data).GetProperty(ResultKey);

    /// <summary>The default packaging's batch summary, from a completed execution.</summary>
    public JsonElement Summary(Result<StandardTaskResponse> response) =>
        OutputAsJson((object?)response.Value!.Data).GetProperty($"{ResultKey}Summary");

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

    /// <summary>
    /// Builds an otherwise-valid FanOutTask whose <c>ItemTask</c> is null.
    /// </summary>
    /// <remarks>
    /// Reflection, because this shape is unreachable by design: <c>FanOutTask.Configure</c> rejects
    /// a missing <c>task</c>, and <c>CreateEmpty()</c> leaves <c>Type</c> unset so the base executor
    /// fails earlier, in <c>ValidateContext</c>. The executor's own null guard still has to hold for
    /// every other construction path (pooling <c>Reset()</c>, future deserialisers), so it is
    /// exercised here rather than left as untested defensive code.
    /// </remarks>
    public static FanOutTask CreateTaskWithoutItemTask()
    {
        var task = FanOutTask.Create(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["mode"] = "inline",
            ["itemsPath"] = "$.documents",
            ["task"] = new { key = "placeholder", domain = "core", flow = "sys-tasks", version = "1.0.0" }
        }));

        typeof(FanOutTask).GetProperty(nameof(FanOutTask.ItemTask))!.SetValue(task, null);
        return task;
    }

    /// <summary>Builds an instance-data payload of <paramref name="count"/> identified documents.</summary>
    public static object Documents(int count)
    {
        var documents = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            documents.Add(new { id = $"doc-{i}" });
        }

        return new { documents };
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

    /// <summary>Orders whose execution should throw instead of returning a result.</summary>
    public HashSet<int> ThrowOrders { get; } = [];

    /// <summary>Orders whose execution should report an infrastructure (Result-level) failure.</summary>
    public HashSet<int> InfrastructureFailureOrders { get; } = [];

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
            var order = onExecuteTask.Order;
            var call = new EngineCall(
                order,
                options.JournalTaskKey,
                options.SuppressDataApply,
                options.CaptureResponse,
                options.PreparedTask,
                context,
                taskTrigger,
                origin);
            _calls.Enqueue(call);
            OnCallStarted?.Invoke(call);

            var delay = DelayPerCall?.Invoke(order) ?? TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            if (ThrowOrders.Contains(order))
            {
                throw new InvalidOperationException($"inner task blew up on item {order}");
            }

            if (InfrastructureFailureOrders.Contains(order))
            {
                return Result<TasksExecutionResult>.Fail(
                    Error.Failure("Engine:Unavailable", $"engine could not run item {order}"));
            }

            if (FailOrders.Contains(order))
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
                        ErrorMessage = $"item {order} failed",
                        NormalizedError = new NormalizedError { Code = "Item:Failed", Message = "boom" }
                    },
                    Response = new StandardTaskResponse
                    {
                        IsSuccess = false,
                        StatusCode = 502,
                        ErrorMessage = $"item {order} failed",
                        Data = new Dictionary<string, object?> { ["order"] = order }
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
                    Data = new Dictionary<string, object?> { ["order"] = order }
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

/// <summary>
/// Scriptless <see cref="IFanOutMapping"/> stand-in for the compiled mapping, with a failure hook
/// on each of the three handlers so the mapping-failure paths are reachable from a test.
/// </summary>
internal sealed class StubFanOutMapping : IFanOutMapping
{
    private readonly Lock _lock = new();

    /// <summary>Values returned by <see cref="ItemSelector"/>; null means "use itemsPath".</summary>
    public IEnumerable<dynamic>? Items { get; init; }

    /// <summary>When set, <see cref="ItemSelector"/> throws this instead of returning.</summary>
    public Exception? ItemSelectorThrows { get; init; }

    /// <summary>When set, <see cref="ItemInputHandler"/> throws this for the given item indexes.</summary>
    public Func<FanOutItem, Exception?>? ItemInputHandlerThrows { get; init; }

    /// <summary>When set, <see cref="OutputHandler"/> throws this instead of returning.</summary>
    public Exception? OutputHandlerThrows { get; init; }

    /// <summary>When set, <see cref="OutputHandler"/> returns this data instead of the default shape.</summary>
    public object? OutputData { get; init; }

    public List<FanOutItem> BoundItems { get; } = [];

    public List<FanOutResult> OutputCalls { get; } = [];

    public Task<IEnumerable<dynamic>?> ItemSelector(ScriptContext context)
    {
        if (ItemSelectorThrows is not null)
        {
            throw ItemSelectorThrows;
        }

        return Task.FromResult(Items);
    }

    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        lock (_lock)
        {
            BoundItems.Add(item);
        }

        if (ItemInputHandlerThrows?.Invoke(item) is { } failure)
        {
            throw failure;
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

        if (OutputHandlerThrows is not null)
        {
            throw OutputHandlerThrows;
        }

        return Task.FromResult(new ScriptResponse
        {
            Data = OutputData ?? new Dictionary<string, object?>
            {
                ["total"] = result.Total,
                ["succeeded"] = result.Succeeded
            }
        });
    }
}
