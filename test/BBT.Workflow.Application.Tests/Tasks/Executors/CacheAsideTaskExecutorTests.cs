using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Caching;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public sealed class CacheAsideTaskExecutorTests
{
    private const string Store = "vnext-state";
    private const string CacheKey = "customer:42:profile";

    [Fact]
    public async Task ExecuteAsync_CacheHit_ReturnsCachedValue_WithoutExecutingSource()
    {
        var harness = new Harness();
        var cached = JsonSerializer.SerializeToElement(new { name = "Ada" });
        harness.StateStore
            .GetAsync(Store, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new StateGetResult(Found: true, Value: cached, ETag: null));

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value.Metadata!["CacheHit"].ShouldBe(true);
        // Source task never resolved.
        await harness.TaskFactory.DidNotReceive()
            .CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>());
        // Nothing written back on a hit.
        await harness.StateStore.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CacheMiss_ExecutesSource_WritesBack_AndReturnsResult()
    {
        var harness = new Harness();
        harness.StateStore
            .GetAsync(Store, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new StateGetResult(Found: false, Value: default, ETag: null));
        harness.SetSourceResponse(new { id = 7 });

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value.Metadata!["CacheHit"].ShouldBe(false);
        result.Value.Metadata!["Refreshed"].ShouldBe(true);
        await harness.StateStore.Received(1).SetAsync(
            Store, Arg.Any<string>(), Arg.Any<JsonElement>(), 300,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ForceRefresh_SkipsCacheRead_AndRefreshes()
    {
        var harness = new Harness(forceRefresh: true);
        harness.SetSourceResponse(new { id = 9 });

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        // Cache read skipped.
        await harness.StateStore.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        // Entry still refreshed.
        await harness.StateStore.Received(1).SetAsync(
            Store, Arg.Any<string>(), Arg.Any<JsonElement>(), 300,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CacheReadError_WithBypass_FallsBackToSource()
    {
        var harness = new Harness(bypassOnCacheError: true);
        harness.StateStore
            .GetAsync(Store, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));
        harness.SetSourceResponse(new { id = 1 });

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        await harness.TaskFactory.Received(1)
            .CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CacheReadError_WithoutBypass_Fails()
    {
        var harness = new Harness(bypassOnCacheError: false);
        harness.StateStore
            .GetAsync(Store, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var result = await harness.ExecuteAsync();

        // Infrastructure result succeeds, but the task response is a business failure -> error boundary chain.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage.ShouldContain("CacheAside read failed");
        // Source task not executed when cache errors are not bypassed.
        await harness.TaskFactory.DidNotReceive()
            .CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public IStateStoreAccessor StateStore { get; } = Substitute.For<IStateStoreAccessor>();
        public ITaskFactory TaskFactory { get; } = Substitute.For<ITaskFactory>();
        private readonly ITaskExecutor _sourceExecutor = Substitute.For<ITaskExecutor>();
        private readonly CacheAsideTask _task;

        public Harness(bool forceRefresh = false, bool bypassOnCacheError = true)
        {
            StateStore.ResolveStoreName(Arg.Any<string?>()).Returns(Store);
            StateStore.PrefixKey(Arg.Any<string>()).Returns(ci => "custom:" + ci.Arg<string>());

            var httpSource = WorkflowTaskFactory.CreateHttpTask("get-customer-http");
            TaskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
                .Returns(Result<WorkflowTask>.Ok(httpSource));

            var registry = Substitute.For<ITaskExecutorRegistry>();
            registry.GetExecutor(Arg.Any<TaskType>()).Returns(Result<ITaskExecutor>.Ok(_sourceExecutor));

            var serviceProvider = Substitute.For<IServiceProvider>();
            serviceProvider.GetService(typeof(ITaskExecutorRegistry)).Returns(registry);

            var config = JsonSerializer.SerializeToElement(new
            {
                key = CacheKey,
                storeName = Store,
                ttlInSeconds = 300,
                consistency = "Eventual",
                sourceTask = new { key = "get-customer-http", domain = "core", flow = "sys-tasks", version = "1.0.0" },
                bypassOnCacheError,
                forceRefresh
            });
            _task = CacheAsideTask.Create(config);
            _task.SetReference(new Reference("customer-cache", "core", "sys-tasks", "1.0.0"));

            Executor = new CacheAsideTaskExecutor(
                StateStore,
                Substitute.For<IScriptEngine>(),
                TaskFactory,
                serviceProvider,
                NullLogger<CacheAsideTaskExecutor>.Instance);
        }

        public CacheAsideTaskExecutor Executor { get; }

        public void SetSourceResponse(object data) =>
            _sourceExecutor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
                .Returns(Result<StandardTaskResponse>.Ok(new StandardTaskResponse { IsSuccess = true, Data = data }));

        public Task<Result<StandardTaskResponse>> ExecuteAsync()
        {
            var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
            var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .SetInstance(instance)
                .Build();
            var onExecute = OnExecuteTask.Create(1, _task, ScriptCode.FromNative(string.Empty));
            var context = new TaskExecutorContext(_task, onExecute, scriptContext, null, TaskTrigger.OnExecute);
            return Executor.ExecuteAsync(context, CancellationToken.None);
        }
    }
}
