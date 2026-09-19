using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Invocation.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// This namespace has <c>BBT.Workflow.Execution</c> in scope via <c>using</c> (for
/// <see cref="TaskTypes"/>) but deliberately NOT <c>BBT.Workflow.Tasks</c> as a whole-namespace
/// <c>using</c>: the orchestrator-side twins of the wire-side <c>TaskEnvelope</c> /
/// <c>TaskInvocationResult</c> / <c>TaskTraceContext</c> live there. Every reference to one of those
/// three below is fully qualified — <c>BBT.Workflow.Execution.X</c> or <c>BBT.Workflow.Tasks.X</c> —
/// rather than the shorter <c>Execution.X</c> ancestor-namespace shorthand other production code
/// under <c>BBT.Workflow.*</c> can use: this test assembly's own
/// <c>BBT.Workflow.Application.Tests.Execution.*</c> namespace (see
/// <c>test/BBT.Workflow.Application.Tests/Execution/</c>) is a CLOSER enclosing-namespace match for
/// bare <c>Execution</c> than <c>BBT.Workflow.Execution</c>, and has no <c>TaskEnvelope</c> of its
/// own, so the short form fails to compile here. Same fully-qualified convention as
/// <c>HttpTaskExecutorRoutingTests</c> / <c>TaskInvocationDispatcherTests</c>.
/// </summary>
public sealed class LocalCacheAsideTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_CacheHit_DoesNotRunTheSourceTask()
    {
        var store = new FakeStateStoreClient("statestore");
        store.Seed("cfg:1", JsonSerializer.SerializeToElement(new { limit = 5 }));
        var sourceCalls = 0;
        var invoker = CreateInvoker(store, _ => { sourceCalls++; return SourceSuccess(); });

        var result = await invoker.InvokeAsync("cfg", Binding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["CacheHit"].ShouldBe(true);
        sourceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task InvokeAsync_CacheMiss_RunsTheSourceTaskAndWritesItBack()
    {
        var store = new FakeStateStoreClient("statestore");
        var invoker = CreateInvoker(store, _ => SourceSuccess());

        var result = await invoker.InvokeAsync("cfg", Binding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["CacheHit"].ShouldBe(false);
        store.Contains("cfg:1").ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_SourceTypeHasNoLocalInvoker_FallsBackToTheRemotePath()
    {
        var store = new FakeStateStoreClient("statestore");
        var remoteCalls = 0;
        var invoker = CreateInvokerWithRemoteFallback(store, onRemote: () => remoteCalls++);

        await invoker.InvokeAsync("cfg", BindingWithPythonSource("cfg:1"), traceContext: null);

        remoteCalls.ShouldBe(1);
    }

    /// <summary>
    /// A cache read failure under the (default-true) <c>bypassOnCacheError</c> flag must not fail
    /// the task — it falls through to the source task, exactly as before the extraction — and the
    /// swallow must still be OBSERVABLE: the shared core reports it through the
    /// <c>onBypassedCacheError</c> callback rather than logging it itself, and this host turns that
    /// into its own <c>LocalCacheAsideBypassedCacheError</c> warning (fix round 1, issue #1007).
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ReadErrorWithBypass_StillReturnsSourceResult_AndLogsTheBypassOnce()
    {
        var store = new FakeStateStoreClient("statestore") { ThrowOnGet = new InvalidOperationException("redis down") };
        var logger = new RecordingLogger<LocalCacheAsideTaskInvoker>();
        var invoker = new LocalCacheAsideTaskInvoker(
            store,
            new StubRegistry(TaskTypes.Http, _ => SourceSuccess()),
            Substitute.For<IRemoteInvokerService>(),
            logger);

        var result = await invoker.InvokeAsync("cfg", Binding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        store.Contains("cfg:1").ShouldBeTrue();
        logger.Entries.Count(e => e.EventId.Id == 10164 && e.Level == LogLevel.Warning).ShouldBe(1);
    }

    private static LocalCacheAsideTaskInvoker CreateInvoker(
        FakeStateStoreClient store,
        Func<BBT.Workflow.Execution.TaskEnvelope, BBT.Workflow.Execution.TaskInvocationResult> onSource) =>
        new(store,
            new StubRegistry(TaskTypes.Http, onSource),
            Substitute.For<IRemoteInvokerService>(),
            NullLogger<LocalCacheAsideTaskInvoker>.Instance);

    /// <summary>
    /// An empty local registry (no invoker for any type) plus a remote invoker whose call is
    /// counted — exercises the miss path for a source type with no in-process invoker (python,
    /// conversation, triggers), where the cache read/write still runs locally.
    /// </summary>
    private static LocalCacheAsideTaskInvoker CreateInvokerWithRemoteFallback(
        FakeStateStoreClient store,
        Action onRemote)
    {
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.InvokeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
                Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                onRemote();
                return Result<BBT.Workflow.Tasks.TaskInvocationResult>.Ok(
                    BBT.Workflow.Tasks.TaskInvocationResult.Success(
                        data: JsonSerializer.SerializeToElement(new { scored = true })));
            });

        return new LocalCacheAsideTaskInvoker(
            store,
            new StubRegistry(),
            remoteInvoker,
            NullLogger<LocalCacheAsideTaskInvoker>.Instance);
    }

    private static BBT.Workflow.Execution.TaskInvocationResult SourceSuccess() =>
        BBT.Workflow.Execution.TaskInvocationResult.Success(
            data: JsonSerializer.SerializeToElement(new { limit = 5 }),
            taskType: TaskTypes.Http);

    private static JsonElement Binding(string key) => JsonSerializer.SerializeToElement(new CacheAsideBinding
    {
        Key = key,
        StoreName = "statestore",
        TtlInSeconds = 300,
        BypassOnCacheError = true,
        ForceRefresh = false,
        SourceTask = new BBT.Workflow.Execution.TaskEnvelope
        {
            TaskType = TaskTypes.Http,
            TaskKey = "get-customer-http",
            Binding = JsonSerializer.SerializeToElement(new HttpTaskBinding
            {
                Url = "https://partner.local/customer", Method = "GET", TimeoutSeconds = 30, ValidateSSL = true
            })
        }
    });

    /// <summary>
    /// A source type with no in-process invoker must still work: the miss path falls back to the
    /// Execution service for that one call while the cache read/write stays local. Built as a fresh
    /// binding (rather than deserializing <see cref="Binding"/> and mutating <c>SourceTask</c>) since
    /// that property is init-only.
    /// </summary>
    private static JsonElement BindingWithPythonSource(string key) => JsonSerializer.SerializeToElement(new CacheAsideBinding
    {
        Key = key,
        StoreName = "statestore",
        TtlInSeconds = 300,
        BypassOnCacheError = true,
        ForceRefresh = false,
        SourceTask = new BBT.Workflow.Execution.TaskEnvelope
        {
            TaskType = TaskTypes.Python,
            TaskKey = "score-customer",
            Binding = JsonSerializer.SerializeToElement(new { script = "pass" })
        }
    });

    private sealed class StubRegistry : ILocalTaskInvokerRegistry
    {
        private readonly Dictionary<string, ILocalTaskInvoker> _invokers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Empty registry: no in-process invoker for any task type.</summary>
        public StubRegistry()
        {
        }

        public StubRegistry(string taskType, Func<BBT.Workflow.Execution.TaskEnvelope, BBT.Workflow.Execution.TaskInvocationResult> onSource)
        {
            _invokers[taskType] = new StubInvoker(taskType, onSource);
        }

        public ILocalTaskInvoker? Get(string taskType) => _invokers.GetValueOrDefault(taskType);

        public bool Has(string taskType) => _invokers.ContainsKey(taskType);
    }

    /// <summary>
    /// A minimal <see cref="ILocalTaskInvoker"/> standing in for the source task's own local
    /// invoker: reconstructs the envelope it was given (the interface takes taskKey/binding
    /// separately, not a pre-built envelope) and hands it to the test's callback.
    /// </summary>
    private sealed class StubInvoker(
        string taskType, Func<BBT.Workflow.Execution.TaskEnvelope, BBT.Workflow.Execution.TaskInvocationResult> onInvoke) : ILocalTaskInvoker
    {
        public string TaskType => taskType;

        public Task<BBT.Workflow.Tasks.TaskInvocationResult> InvokeAsync(
            string? taskKey,
            JsonElement binding,
            BBT.Workflow.Tasks.TaskTraceContext? traceContext,
            CancellationToken cancellationToken = default)
        {
            var envelope = new BBT.Workflow.Execution.TaskEnvelope
            {
                TaskType = taskType,
                TaskKey = taskKey ?? string.Empty,
                Binding = binding
            };
            var wireResult = onInvoke(envelope);
            return Task.FromResult(LocalInvocationResultMapper.ToOrchestratorResult(wireResult));
        }
    }

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> recorder — cheaper for this one assertion (did the
    /// generated <c>LocalCacheAsideBypassedCacheError</c> call fire, exactly once, at Warning) than
    /// matching NSubstitute against the source-generated <c>Log&lt;TState&gt;</c> call shape.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
