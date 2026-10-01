using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// The CacheAside executor runs the read-through itself: cache get/set through
/// <see cref="IStateStoreCacheGateway"/> (which follows the <c>statestore</c> routing mode — pinned in
/// <c>StateStoreCacheGatewayRoutingTests</c>), the source as a TASK through its own
/// <see cref="ITaskExecutor"/> with <c>sourceMapping</c> as its mapping, and the task-level mapping's
/// OutputHandler on the result like any other task. The gateway and the source executor are
/// substituted; the executor under test is real.
/// </summary>
public sealed class CacheAsideTaskExecutorTests
{
    private const string StaticKey = "customer:42:profile";
    private const string SourceMappingCode = "public class SourceMapping {}";

    [Fact]
    public async Task Miss_RunsSourceExecutorWithSourceMapping_AndCachesItsOutput()
    {
        var harness = new Harness();
        object? cachedValue = null;
        harness.Gateway.SetWithResultAsync(
                Arg.Any<string>(), Arg.Do<object?>(v => cachedValue = v), Arg.Any<int?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new CacheSetResult(true));

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();

        var sourceContext = harness.SourceContexts.ShouldHaveSingleItem();
        sourceContext.OnExecuteTask.Mapping.ShouldBe(harness.Task.SourceMapping);
        sourceContext.Task.ShouldBeSameAs(harness.SourceTask);
        sourceContext.ScriptContext.ShouldNotBeSameAs(harness.OuterScriptContext);

        JsonSerializer.Serialize(cachedValue, JsonSerializerConstants.JsonOptions).ShouldBe("""{"name":"shaped"}""");
        await harness.Gateway.Received(1).SetWithResultAsync(
            StaticKey, Arg.Any<object?>(), 300, "vnext-state", "Eventual",
            Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>(), "cacheaside");

        ((bool)result.Value.Metadata!["CacheHit"]).ShouldBeFalse();
        ((bool)result.Value.Metadata!["Refreshed"]).ShouldBeTrue();
    }

    [Fact]
    public async Task Hit_DoesNotRunSourceExecutor()
    {
        var harness = new Harness();
        harness.Gateway.GetAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new CacheGetResult(true, true, JsonSerializer.SerializeToElement(new { name = "cached" }),
                Metadata: new Dictionary<string, object> { ["Key"] = "custom:" + StaticKey, ["StoreName"] = "vnext-state" }));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeTrue();
        ((bool)result.Value.Metadata!["CacheHit"]).ShouldBeTrue();
        ((bool)result.Value.Metadata!["Refreshed"]).ShouldBeFalse();
        result.Value.Metadata!["Key"].ShouldBe("custom:" + StaticKey);
        JsonSerializer.Serialize((object?)result.Value.Data).ShouldContain("cached");
        harness.Registry.ReceivedCalls().ShouldBeEmpty();
        await harness.Gateway.DidNotReceiveWithAnyArgs().SetWithResultAsync(
            default!, default, default, default, default, default!, default, default);
    }

    [Fact]
    public async Task SourceBusinessFailure_IsNotCached_AndPropagatesStatus()
    {
        var harness = new Harness();
        harness.SourceResponse = new StandardTaskResponse { IsSuccess = false, StatusCode = 404, ErrorMessage = "not found" };

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.StatusCode.ShouldBe(404);
        await harness.Gateway.DidNotReceiveWithAnyArgs().SetWithResultAsync(
            default!, default, default, default, default, default!, default, default);
    }

    [Fact]
    public async Task NoSourceMapping_CachesRawSourceData()
    {
        var harness = new Harness(withSourceMapping: false);

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeTrue();
        var sourceMapping = harness.SourceContexts.ShouldHaveSingleItem().OnExecuteTask.Mapping;
        sourceMapping.HasMappingCode.ShouldBeFalse();
        sourceMapping.Code.ShouldBe(string.Empty);
        await harness.Gateway.Received(1).SetWithResultAsync(
            StaticKey, Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>(), "cacheaside");
    }

    [Fact]
    public async Task ReadError_Bypass_RunsSource()
    {
        var harness = new Harness(bypassOnCacheError: true);
        harness.SetReadResult(new CacheGetResult(false, false, default, Error: "redis down"));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeTrue();
        harness.SourceContexts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReadError_NoBypass_Fails()
    {
        var harness = new Harness(bypassOnCacheError: false);
        harness.SetReadResult(new CacheGetResult(false, false, default, Error: "redis down"));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage.ShouldNotBeNull();
        result.Value.ErrorMessage!.ShouldContain("redis down");
        harness.SourceContexts.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteError_Bypass_ReturnsSourceResult()
    {
        var harness = new Harness(bypassOnCacheError: true);
        harness.SetWriteResult(new CacheSetResult(false, Error: "write refused"));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeTrue();
        JsonSerializer.Serialize((object?)result.Value.Data).ShouldContain("shaped");
    }

    [Fact]
    public async Task WriteError_NoBypass_Fails()
    {
        var harness = new Harness(bypassOnCacheError: false);
        harness.SetWriteResult(new CacheSetResult(false, Error: "write refused"));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage!.ShouldContain("write refused");
    }

    [Fact]
    public async Task ForceRefresh_SkipsRead()
    {
        var harness = new Harness(forceRefresh: true);

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeTrue();
        await harness.Gateway.DidNotReceiveWithAnyArgs().GetAsync(
            default!, default, default, default!, default, default);
        harness.SourceContexts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TaskLevelMappingOutputHandler_RunsOnResult()
    {
        var harness = new Harness();
        var mapping = harness.UseOuterMapping();
        mapping.OutputHandler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse { Data = "projected" }));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeTrue();
        ((object?)result.Value.Data).ShouldBe("projected");
    }

    [Fact]
    public async Task TaskLevelMappingInputHandler_SetCacheKey_IsUsed()
    {
        var harness = new Harness();
        var mapping = harness.UseOuterMapping();
        mapping.InputHandler(Arg.Any<WorkflowTask>(), Arg.Any<ScriptContext>())
            .Returns(ci =>
            {
                ((CacheAsideTask)ci[0]).SetCacheKey("from-input");
                return Task.FromResult(new ScriptResponse());
            });

        await harness.ExecuteAsync();

        await harness.Gateway.Received(1).GetAsync(
            "from-input", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(),
            Arg.Any<CancellationToken>(), "cacheaside");
    }

    [Fact]
    public async Task KeyScript_OverridesKey()
    {
        var harness = new Harness(keyScriptCode: "public class Key {}");
        harness.KeyEvaluator.EvaluateAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Ok("customer:99:profile"));

        await harness.ExecuteAsync();

        await harness.Gateway.Received(1).GetAsync(
            "customer:99:profile", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(),
            Arg.Any<CancellationToken>(), "cacheaside");
    }

    [Fact]
    public async Task EmptyKeyScriptResult_KeepsStaticKey()
    {
        var harness = new Harness(keyScriptCode: "public class Key {}");
        // The object form of 'key' carries no static key, so seed one the way an InputHandler would.
        harness.Task.SetCacheKey(StaticKey);
        harness.KeyEvaluator.EvaluateAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Ok("  "));

        await harness.ExecuteAsync();

        await harness.Gateway.Received(1).GetAsync(
            StaticKey, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(),
            Arg.Any<CancellationToken>(), "cacheaside");
    }

    [Fact]
    public async Task KeyScriptFailure_FailsInputStage()
    {
        var harness = new Harness(keyScriptCode: "public class Key {}");
        harness.KeyEvaluator.EvaluateAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Fail(Error.Failure("key.failed", "key script blew up")));

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldBe("key script blew up");
        await harness.Gateway.DidNotReceiveWithAnyArgs().GetAsync(
            default!, default, default, default!, default, default);
    }

    [Fact]
    public async Task EmptyKey_Fails()
    {
        var harness = new Harness(staticKey: string.Empty);

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage!.ShouldContain("non-empty 'key'");
        harness.SourceContexts.ShouldBeEmpty();
    }

    [Fact]
    public async Task SourceOfTypeCacheAside_IsRejected()
    {
        var harness = new Harness();
        var nestedCacheAside = CacheAsideTask.Create(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["key"] = "nested",
            ["sourceTask"] = new { key = "x", domain = "core", flow = "sys-tasks", version = "1.0.0" }
        }));
        nestedCacheAside.SetReference(new Reference("nested-cache", "core", "sys-tasks", "1.0.0"));
        harness.TaskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(nestedCacheAside));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage!.ShouldContain("cannot be a CacheAside task");
        harness.Registry.ReceivedCalls().ShouldBeEmpty();
        await harness.Gateway.DidNotReceiveWithAnyArgs().GetAsync(
            default!, default, default, default!, default, default);
    }

    [Fact]
    public async Task MissingSourceTask_Fails()
    {
        var harness = new Harness(withSourceTask: false);

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage!.ShouldContain("sourceTask");
        harness.Registry.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task UnresolvableSourceTask_Fails()
    {
        var harness = new Harness();
        harness.TaskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Fail(Error.NotFound("task.notfound", "source not found")));

        var result = await harness.ExecuteAsync();

        result.Value!.IsSuccess.ShouldBeFalse();
        harness.Registry.ReceivedCalls().ShouldBeEmpty();
    }

    private sealed class Harness
    {
        public IStateStoreCacheGateway Gateway { get; } = Substitute.For<IStateStoreCacheGateway>();
        public ICacheKeyEvaluator KeyEvaluator { get; } = Substitute.For<ICacheKeyEvaluator>();
        public IRemoteInvokerService RemoteInvoker { get; } = Substitute.For<IRemoteInvokerService>();
        public IScriptEngine ScriptEngine { get; } = Substitute.For<IScriptEngine>();
        public ITaskFactory TaskFactory { get; } = Substitute.For<ITaskFactory>();
        public ITaskExecutorRegistry Registry { get; } = Substitute.For<ITaskExecutorRegistry>();
        public ITaskExecutor SourceExecutor { get; } = Substitute.For<ITaskExecutor>();
        public List<TaskExecutorContext> SourceContexts { get; } = [];
        public HttpTask SourceTask { get; } = WorkflowTaskFactory.CreateHttpTask("src");
        public CacheAsideTask Task { get; }
        public ScriptContext OuterScriptContext { get; private set; } = null!;
        public CacheAsideTaskExecutor Executor { get; }

        /// <summary>What the source executor answers; defaults to a 200 with shaped data.</summary>
        public StandardTaskResponse SourceResponse { get; set; } = new()
        {
            IsSuccess = true,
            StatusCode = 200,
            Data = new { name = "shaped" },
            TaskType = "Http"
        };

        private ScriptCode _outerMapping = ScriptCode.FromNative(string.Empty);

        public Harness(
            bool withSourceMapping = true,
            bool bypassOnCacheError = true,
            bool forceRefresh = false,
            string? keyScriptCode = null,
            string staticKey = StaticKey,
            bool withSourceTask = true)
        {
            TaskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
                .Returns(Result<WorkflowTask>.Ok(SourceTask));

            Registry.GetExecutor(Arg.Any<TaskType>()).Returns(Result<ITaskExecutor>.Ok(SourceExecutor));
            SourceExecutor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    SourceContexts.Add((TaskExecutorContext)ci[0]);
                    return System.Threading.Tasks.Task.FromResult(Result<StandardTaskResponse>.Ok(SourceResponse));
                });

            RemoteInvoker.CreateTraceContext(Arg.Any<ScriptContext>()).Returns(new TaskTraceContext());

            SetReadResult(new CacheGetResult(true, false, default));
            SetWriteResult(new CacheSetResult(true, Metadata: new Dictionary<string, object>
            {
                ["Key"] = "custom:" + StaticKey,
                ["StoreName"] = "vnext-state"
            }));

            var config = new Dictionary<string, object?>
            {
                ["key"] = staticKey,
                ["storeName"] = "vnext-state",
                ["ttlInSeconds"] = 300,
                ["consistency"] = "Eventual",
                ["bypassOnCacheError"] = bypassOnCacheError,
                ["forceRefresh"] = forceRefresh
            };
            if (withSourceTask)
            {
                config["sourceTask"] = new { key = "src", domain = "core", flow = "sys-tasks", version = "1.0.0" };
            }

            if (withSourceMapping)
            {
                config["sourceMapping"] = new { location = "./src/SourceMapping.csx", code = SourceMappingCode, encoding = "NAT" };
            }

            if (keyScriptCode is not null)
            {
                config["key"] = new { location = "./src/Key.csx", code = keyScriptCode, encoding = "NAT" };
            }

            Task = CacheAsideTask.Create(JsonSerializer.SerializeToElement(config));
            Task.SetReference(new Reference("customer-cache", "core", "sys-tasks", "1.0.0"));

            var serviceProvider = Substitute.For<IServiceProvider>();
            serviceProvider.GetService(typeof(ITaskExecutorRegistry)).Returns(Registry);

            Executor = new CacheAsideTaskExecutor(
                ScriptEngine,
                TaskFactory,
                KeyEvaluator,
                Gateway,
                RemoteInvoker,
                serviceProvider,
                NullLogger<CacheAsideTaskExecutor>.Instance);
        }

        public void SetReadResult(CacheGetResult result) =>
            Gateway.GetAsync(
                    Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(),
                    Arg.Any<CancellationToken>(), Arg.Any<string?>())
                .Returns(result);

        public void SetWriteResult(CacheSetResult result) =>
            Gateway.SetWithResultAsync(
                    Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
                .Returns(result);

        /// <summary>Gives the CacheAside task a non-empty task-level mapping and returns its compiled instance.</summary>
        public IMapping UseOuterMapping()
        {
            _outerMapping = ScriptCode.FromNative("public class OuterMapping {}");
            var mapping = Substitute.For<IMapping>();
            mapping.InputHandler(Arg.Any<WorkflowTask>(), Arg.Any<ScriptContext>())
                .Returns(System.Threading.Tasks.Task.FromResult(new ScriptResponse()));
            mapping.OutputHandler(Arg.Any<ScriptContext>())
                .Returns(ci => System.Threading.Tasks.Task.FromResult(new ScriptResponse()));
            Func<IMapping> factory = () => mapping;
            ScriptEngine.CompileToFactoryAsync<IMapping>(
                    Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings?>(), Arg.Any<CancellationToken>())
                .Returns(System.Threading.Tasks.Task.FromResult(factory));
            return mapping;
        }

        public Task<Result<StandardTaskResponse>> ExecuteAsync()
        {
            var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
            OuterScriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .SetInstance(instance)
                .Build();
            var onExecute = OnExecuteTask.Create(1, Task, _outerMapping);
            var context = new TaskExecutorContext(
                Task, onExecute, OuterScriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
            return Executor.ExecuteAsync(context, CancellationToken.None);
        }
    }
}
