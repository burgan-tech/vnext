using System;
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
/// The binding's answer reaches an output mapping, but an UNMAPPED binding task keeps contributing no instance data:
/// a create/notify task must not start merging the binding's response (an S3 location, an HTTP body) into the
/// instance now that the answer is returned (vnext-client-sdk-core#142-B, no breaking change).
/// </summary>
public sealed class DaprBindingTaskExecutorTests
{
    [Theory]
    [InlineData(ExecutionMode.Local)]
    [InlineData(ExecutionMode.Remote)]
    public async Task UnmappedTask_AddsNoInstanceData_ThoughTheBindingAnswered(ExecutionMode mode)
    {
        var task = DaprBindingTask.Create(JsonDocument.Parse("""{ "bindingName": "vnext-blob-s3", "operation": "create", "metadata": { "key": "k" } }""").RootElement);
        task.SetReference(new Reference("store-file", "test", "sys-tasks", "1.0.0"));
        var answer = BBT.Workflow.Tasks.TaskInvocationResult.Success(
            data: JsonSerializer.SerializeToElement(new { location = "https://s3/k", versionID = "v1" }));

        var remote = Substitute.For<IRemoteInvokerService>();
        remote.CreateTraceContext(Arg.Any<ScriptContext>()).Returns(new BBT.Workflow.Tasks.TaskTraceContext());
        remote.InvokeAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(Result<BBT.Workflow.Tasks.TaskInvocationResult>.Ok(answer));
        var router = Substitute.For<ITaskInvocationRouter>();
        router.Resolve(Arg.Any<WorkflowTask>(), TaskTypes.DaprBinding).Returns(new TaskInvocationDecision(mode, "test"));
        var local = Substitute.For<ILocalTaskInvoker>();
        local.TaskType.Returns(TaskTypes.DaprBinding);
        local.InvokeAsync(default, default, default, default).ReturnsForAnyArgs(answer);
        var registry = Substitute.For<ILocalTaskInvokerRegistry>();
        registry.Has(TaskTypes.DaprBinding).Returns(true);
        registry.Get(TaskTypes.DaprBinding).Returns(local);
        var dispatcher = new TaskInvocationDispatcher(router, registry, remote,
            Options.Create(new TaskInvocationOptions()), NullLogger<TaskInvocationDispatcher>.Instance);
        var executor = new DaprBindingTaskExecutor(remote, dispatcher, Substitute.For<IScriptEngine>(), NullLogger<DaprBindingTaskExecutor>.Instance);

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(Instance.Create(Guid.NewGuid(), "flow", "1.0", "key"))
            .Build();
        var context = new TaskExecutorContext(task, OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty)),
            scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);

        var response = await executor.ExecuteAsync(context, CancellationToken.None);

        response.IsSuccess.ShouldBeTrue();
        ((object?)response.Value!.Data).ShouldBeNull();
        if (mode == ExecutionMode.Local)
            await local.ReceivedWithAnyArgs(1).InvokeAsync(default, default, default, default);
        else
            await remote.ReceivedWithAnyArgs(1).InvokeAsync(default!, default!, default!, default!, default);
    }
}
