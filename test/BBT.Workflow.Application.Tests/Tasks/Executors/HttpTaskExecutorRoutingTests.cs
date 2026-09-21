using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// The executor must ask the router and honour its answer — and both paths must receive the SAME
/// prepared binding, so a task definition cannot behave differently depending on where it ran.
/// </summary>
/// <remarks>
/// This namespace has both <c>BBT.Workflow.Execution</c> (for <see cref="TaskTypes"/>) and
/// <c>BBT.Workflow.Tasks</c> in scope; both declare identically-named
/// <c>TaskEnvelope</c>/<c>TaskInvocationResult</c>/<c>TaskTraceContext</c> twins, so every
/// unqualified reference below to one of those three is fully qualified with
/// <c>BBT.Workflow.Tasks.</c> to avoid CS0104 — the executor and dispatcher work with the
/// orchestrator-side family, never the wire-side one. Same convention as
/// <c>TaskInvocationDispatcherTests</c>.
/// </remarks>
public sealed class HttpTaskExecutorRoutingTests
{
    [Fact]
    public async Task InvokeAsync_RouterSaysLocal_UsesTheLocalInvokerAndNotTheRemoteOne()
    {
        var harness = new Harness(ExecutionMode.Local);

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        harness.LocalBindings.Count.ShouldBe(1);
        await harness.RemoteInvoker.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
            Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_RouterSaysRemote_UsesTheExecutionService()
    {
        var harness = new Harness(ExecutionMode.Remote);

        var result = await harness.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        harness.LocalBindings.Count.ShouldBe(0);
        await harness.RemoteInvoker.Received(1).InvokeAsync(
            TaskTypes.Http, "call-partner", Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
            Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_BothModes_ReceiveTheSameBinding()
    {
        var local = new Harness(ExecutionMode.Local);
        var remote = new Harness(ExecutionMode.Remote);

        await local.ExecuteAsync();
        await remote.ExecuteAsync();

        local.LocalBindings[0].GetRawText()
            .ShouldBe(remote.CapturedRemoteEnvelope!.Binding.GetRawText());
    }

    private sealed class Harness
    {
        public IRemoteInvokerService RemoteInvoker { get; } = Substitute.For<IRemoteInvokerService>();

        public List<JsonElement> LocalBindings { get; } = [];

        public BBT.Workflow.Tasks.TaskEnvelope? CapturedRemoteEnvelope { get; private set; }

        private readonly HttpTask _task;
        private readonly HttpTaskExecutor _executor;

        public Harness(ExecutionMode mode)
        {
            _task = WorkflowTaskFactory.CreateHttpTask("call-partner");

            RemoteInvoker.CreateTraceContext(Arg.Any<ScriptContext>())
                .Returns(new BBT.Workflow.Tasks.TaskTraceContext());
            RemoteInvoker.InvokeAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
                    Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    CapturedRemoteEnvelope = call.Arg<BBT.Workflow.Tasks.TaskEnvelope>();
                    return Result<BBT.Workflow.Tasks.TaskInvocationResult>.Ok(
                        BBT.Workflow.Tasks.TaskInvocationResult.Success(data: JsonSerializer.SerializeToElement(new { ok = true })));
                });

            var router = Substitute.For<ITaskInvocationRouter>();
            router.Resolve(Arg.Any<WorkflowTask>(), TaskTypes.Http)
                .Returns(new TaskInvocationDecision(mode, "test"));

            var registry = Substitute.For<ILocalTaskInvokerRegistry>();
            registry.Get(TaskTypes.Http).Returns(new RecordingLocalInvoker(LocalBindings));
            registry.Has(TaskTypes.Http).Returns(true);

            // A REAL dispatcher over stubbed collaborators: this test is about the executor
            // honouring the routing decision end to end, not about mocking the dispatcher away.
            var dispatcher = new TaskInvocationDispatcher(
                router, registry, RemoteInvoker,
                Options.Create(new TaskInvocationOptions()), NullLogger<TaskInvocationDispatcher>.Instance);

            _executor = new HttpTaskExecutor(
                RemoteInvoker,
                Substitute.For<IScriptEngine>(),
                dispatcher,
                NullLogger<HttpTaskExecutor>.Instance);
        }

        public Task<Result<StandardTaskResponse>> ExecuteAsync()
        {
            var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
            var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .SetInstance(instance)
                .Build();
            var onExecute = OnExecuteTask.Create(1, _task, ScriptCode.FromNative(string.Empty));
            var context = new TaskExecutorContext(
                _task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
            return _executor.ExecuteAsync(context, CancellationToken.None);
        }

        private sealed class RecordingLocalInvoker(List<JsonElement> bindings) : ILocalTaskInvoker
        {
            public string TaskType => TaskTypes.Http;

            public Task<BBT.Workflow.Tasks.TaskInvocationResult> InvokeAsync(
                string? taskKey, JsonElement binding, BBT.Workflow.Tasks.TaskTraceContext? traceContext,
                CancellationToken cancellationToken = default)
            {
                bindings.Add(binding.Clone());
                return Task.FromResult(BBT.Workflow.Tasks.TaskInvocationResult.Success(
                    data: JsonSerializer.SerializeToElement(new { ok = true })));
            }
        }
    }
}
