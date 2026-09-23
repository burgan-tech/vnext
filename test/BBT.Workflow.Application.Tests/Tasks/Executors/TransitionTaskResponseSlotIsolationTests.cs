using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Pins <c>TaskResponse</c> slot isolation through the TRANSITION caller path: two sequential
/// <see cref="HttpTaskExecutor"/> runs with <see cref="TaskTrigger.OnExecute"/> on one shared
/// <see cref="ScriptContext"/> — the exact shape of a transition hook's cross-order task group.
/// <see cref="BBT.Workflow.Scripting.TaskResponseSlotIsolationTests"/> (Domain.Tests) pins the
/// same invariant on <c>ScriptContext.SetStandardResponse</c> directly; this test pins that the
/// one production call site every executor shares (<c>TaskExecutorBase.UpdateScriptContextWithResponse</c>)
/// reaches it with per-task keys, so the collision fixed for functions
/// (vnext-client-sdk-core#6) cannot recur for onExecute/onEntry/onExit task groups either.
/// </summary>
public sealed class TransitionTaskResponseSlotIsolationTests
{
    [Fact]
    public async Task TwoSequentialTransitionTasks_EachTaskResponseSlotKeepsItsOwnPayload()
    {
        var harness = new Harness();

        var first = await harness.ExecuteAsync(
            "slot-task-one", new { source = "task-one", amount = 1 });
        var second = await harness.ExecuteAsync(
            "slot-task-two", new { source = "task-two", amount = 2 });

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();

        var slotOne = AsJson((object?)harness.ScriptContext.TaskResponse["slotTaskOne"]);
        var slotTwo = AsJson((object?)harness.ScriptContext.TaskResponse["slotTaskTwo"]);

        slotOne.GetProperty("data").GetProperty("source").GetString().ShouldBe("task-one");
        slotOne.GetProperty("data").GetProperty("amount").GetInt32().ShouldBe(1);
        slotTwo.GetProperty("data").GetProperty("source").GetString().ShouldBe("task-two");
        slotTwo.GetProperty("data").GetProperty("amount").GetInt32().ShouldBe(2);

        // Body keeps its accumulate-last-wins semantics for the same run.
        var body = AsJson((object?)harness.ScriptContext.Body);
        body.GetProperty("data").GetProperty("source").GetString().ShouldBe("task-two");
    }

    private static JsonElement AsJson(object? value) =>
        JsonSerializer.SerializeToElement(value, ScriptContext.JsonScriptBodyOptions);

    /// <summary>
    /// Same plumbing as <see cref="TaskExecutorBaseResponseVariableKeyTests"/>' harness, except the
    /// ScriptContext is shared across executions and each call runs a DIFFERENT task with its own
    /// stubbed payload — the collision needs two tasks, not two option shapes.
    /// </summary>
    private sealed class Harness
    {
        private readonly IRemoteInvokerService _remoteInvoker = Substitute.For<IRemoteInvokerService>();
        public ScriptContext ScriptContext { get; }

        public Harness()
        {
            var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
            ScriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .SetInstance(instance)
                .Build();
        }

        public Task<Result<StandardTaskResponse>> ExecuteAsync(string taskKey, object payload)
        {
            var task = WorkflowTaskFactory.CreateHttpTask(taskKey);
            var data = JsonSerializer.SerializeToElement(payload);

            _remoteInvoker.InvokeAsync(
                    Arg.Any<string>(), taskKey, Arg.Any<TaskEnvelope>(),
                    Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
                .Returns(Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(data: data)));

            var dispatcher = Substitute.For<ITaskInvocationDispatcher>();
            dispatcher.DispatchAsync(
                    Arg.Any<WorkflowTask>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                    Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
                .Returns(call => _remoteInvoker.InvokeAsync(
                    call.ArgAt<string>(1),
                    call.ArgAt<WorkflowTask>(0).Key,
                    call.ArgAt<TaskEnvelope>(2),
                    call.ArgAt<TaskTraceContext>(3),
                    call.ArgAt<CancellationToken>(4)));

            var executor = new HttpTaskExecutor(
                _remoteInvoker, Substitute.For<IScriptEngine>(), dispatcher, NullLogger<HttpTaskExecutor>.Instance);

            var onExecute = OnExecuteTask.Create(0, task, ScriptCode.FromNative(string.Empty));
            var context = new TaskExecutorContext(
                task, onExecute, ScriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);

            return executor.ExecuteAsync(context, CancellationToken.None);
        }
    }
}
