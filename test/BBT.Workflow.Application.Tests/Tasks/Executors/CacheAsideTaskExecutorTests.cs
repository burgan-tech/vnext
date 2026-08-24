using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public sealed class CacheAsideTaskExecutorTests
{
    private const string CacheAsideType = "cacheaside";

    [Fact]
    public async Task InvokeAsync_BuildsCacheAsideEnvelope_WithEmbeddedSource_AndReturnsInvokerResult()
    {
        var harness = new Harness(ttlInSeconds: 300, forceRefresh: true, bypassOnCacheError: false);
        var payload = JsonSerializer.SerializeToElement(new { id = 7 });
        harness.RemoteInvoker.InvokeAsync(
                CacheAsideType, Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(data: payload)));

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();

        // The cache read-through is dispatched to the Execution service as a 'cacheaside' invoke, with the
        // resolved key, options and the pre-resolved source task envelope embedded.
        await harness.RemoteInvoker.Received(1).InvokeAsync(
            CacheAsideType, "customer-cache", Arg.Is<TaskEnvelope>(e =>
                e.TaskType == CacheAsideType &&
                e.Binding.GetProperty("Key").GetString() == "customer:42:profile" &&
                e.Binding.GetProperty("TtlInSeconds").GetInt32() == 300 &&
                e.Binding.GetProperty("ForceRefresh").GetBoolean() &&
                e.Binding.GetProperty("BypassOnCacheError").GetBoolean() == false &&
                e.Binding.GetProperty("SourceTask").GetProperty("TaskType").GetString() == "http"),
            Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_ReturnsInvokerData_WhenNoSourceMapping()
    {
        var harness = new Harness();
        var payload = JsonSerializer.SerializeToElement(new { name = "Ada" });
        harness.RemoteInvoker.InvokeAsync(
                CacheAsideType, Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(data: payload)));

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        string dataJson = JsonSerializer.Serialize((object?)result.Value!.Data, JsonSerializerConstants.JsonOptions);
        dataJson.ShouldContain("Ada");
    }

    [Fact]
    public async Task InvokeAsync_KeyExpression_OverridesKeyInEnvelope()
    {
        var harness = new Harness(keyExpressionCode: "\"ignored-by-mock\"");
        harness.ExpressoEvaluator.Evaluate(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>())
            .Returns(Result<string>.Ok("customer:99:profile"));
        harness.RemoteInvoker.InvokeAsync(
                CacheAsideType, Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: JsonSerializer.SerializeToElement(new { ok = true }))));

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        // The Dynamic Expresso result overrides the static key in the envelope sent to Execution.
        await harness.RemoteInvoker.Received(1).InvokeAsync(
            CacheAsideType, Arg.Any<string>(),
            Arg.Is<TaskEnvelope>(e => e.Binding.GetProperty("Key").GetString() == "customer:99:profile"),
            Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenSourceTaskCannotBeResolved_Fails()
    {
        var harness = new Harness();
        harness.TaskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Fail(Error.NotFound("task.notfound", "source not found")));

        var result = await harness.ExecuteAsync();

        // Source-resolution failure surfaces as a business failure (Result stays Ok via CreateErrorResponse).
        result.Value!.IsSuccess.ShouldBeFalse();
        // The cache-aside invoker is never called when the source task cannot be resolved.
        await harness.RemoteInvoker.DidNotReceive().InvokeAsync(
            CacheAsideType, Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
            Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public IRemoteInvokerService RemoteInvoker { get; } = Substitute.For<IRemoteInvokerService>();
        public ITaskFactory TaskFactory { get; } = Substitute.For<ITaskFactory>();
        public IDynamicExpressoValueEvaluator ExpressoEvaluator { get; } = Substitute.For<IDynamicExpressoValueEvaluator>();
        private readonly CacheAsideTask _task;

        public Harness(
            int? ttlInSeconds = 300,
            bool forceRefresh = false,
            bool bypassOnCacheError = true,
            string? keyExpressionCode = null)
        {
            var httpSource = WorkflowTaskFactory.CreateHttpTask("get-customer-http");
            TaskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
                .Returns(Result<WorkflowTask>.Ok(httpSource));

            var config = new Dictionary<string, object?>
            {
                ["key"] = "customer:42:profile",
                ["storeName"] = "vnext-state",
                ["ttlInSeconds"] = ttlInSeconds,
                ["consistency"] = "Eventual",
                ["sourceTask"] = new { key = "get-customer-http", domain = "core", flow = "sys-tasks", version = "1.0.0" },
                ["bypassOnCacheError"] = bypassOnCacheError,
                ["forceRefresh"] = forceRefresh
            };
            if (keyExpressionCode is not null)
            {
                config["keyExpression"] = new { location = "dynamicExpresso", code = keyExpressionCode, encoding = "NAT" };
            }

            _task = CacheAsideTask.Create(JsonSerializer.SerializeToElement(config));
            _task.SetReference(new Reference("customer-cache", "core", "sys-tasks", "1.0.0"));

            Executor = new CacheAsideTaskExecutor(
                RemoteInvoker,
                Substitute.For<IScriptEngine>(),
                TaskFactory,
                ExpressoEvaluator,
                NullLogger<CacheAsideTaskExecutor>.Instance,
                Substitute.For<IWorkflowMetrics>());
        }

        public CacheAsideTaskExecutor Executor { get; }

        public Task<Result<StandardTaskResponse>> ExecuteAsync()
        {
            var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
            var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .SetInstance(instance)
                .Build();
            var onExecute = OnExecuteTask.Create(1, _task, ScriptCode.FromNative(string.Empty));
            var context = new TaskExecutorContext(_task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
            return Executor.ExecuteAsync(context, CancellationToken.None);
        }
    }
}
