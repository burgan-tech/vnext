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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Pins <see cref="TaskExecutorBase{TTask}"/>'s two response-variable-name derivation points
/// (<c>UpdateScriptContextWithResponse</c> for <c>ScriptContext.TaskResponse</c>, and the
/// Extension-trigger-only <c>SetOutputResponse</c> for <c>ScriptContext.OutputResponse</c>)
/// against <see cref="TaskExecutorContext.ResponseVariableKey"/>. Exercised through
/// <see cref="HttpTaskExecutor"/> because, with no output mapping configured, both derivation
/// points run unconditionally without needing a script engine.
/// </summary>
/// <remarks>
/// The Preprod fault traced back to the <c>TaskResponse</c> merge (<c>Models.cs</c>): two
/// extensions sharing one task key overwrote each other's entry because both keyed off
/// <c>taskKey.ToVariableName()</c>. <see cref="TaskEngineExecutionOptions.ResponseVariableKey"/>
/// lets a caller (the extension path) file the response under a distinct key instead — these
/// tests pin that BOTH dictionaries honor it, and that a transition-scoped task (the option left
/// null) is byte-identical to today's behavior.
/// </remarks>
public sealed class TaskExecutorBaseResponseVariableKeyTests
{
    [Fact]
    public async Task ExecuteAsync_ResponseVariableKeyNull_TransitionScopedTask_UsesDerivedKey_TodaysBehavior()
    {
        var harness = new Harness();
        harness.StubInvokerToReturn(JsonSerializer.SerializeToElement(new { ok = true }));

        var result = await harness.ExecuteAsync(TaskTrigger.OnExecute, responseVariableKey: null);

        result.IsSuccess.ShouldBeTrue();
        var derivedKey = harness.TaskKey.ToVariableName();
        harness.ScriptContext.TaskResponse.ShouldContainKey(derivedKey);
        // Transition-scoped (non-Extension) trigger never touches OutputResponse, with or without
        // the option — this is the byte-identical guarantee for onExecute/onEntry/onExit tasks.
        harness.ScriptContext.OutputResponse.ShouldNotContainKey(derivedKey);
        harness.ScriptContext.OutputResponse.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ResponseVariableKeySet_ExtensionTrigger_WritesBothDictionariesUnderThatKey()
    {
        var harness = new Harness();
        harness.StubInvokerToReturn(JsonSerializer.SerializeToElement(new { ok = true }));
        const string customKey = "extensionOutputKey";

        var result = await harness.ExecuteAsync(TaskTrigger.Extension, responseVariableKey: customKey);

        result.IsSuccess.ShouldBeTrue();
        var derivedKey = harness.TaskKey.ToVariableName();

        // Both TaskResponse (the Preprod crash site) and OutputResponse are filed under the
        // caller-supplied key instead of the task-key-derived one.
        harness.ScriptContext.TaskResponse.ShouldContainKey(customKey);
        harness.ScriptContext.TaskResponse.ShouldNotContainKey(derivedKey);
        harness.ScriptContext.OutputResponse.ShouldContainKey(customKey);
        harness.ScriptContext.OutputResponse.ShouldNotContainKey(derivedKey);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseVariableKeyNull_ExtensionTrigger_FallsBackToDerivedKey()
    {
        var harness = new Harness();
        harness.StubInvokerToReturn(JsonSerializer.SerializeToElement(new { ok = true }));

        var result = await harness.ExecuteAsync(TaskTrigger.Extension, responseVariableKey: null);

        result.IsSuccess.ShouldBeTrue();
        var derivedKey = harness.TaskKey.ToVariableName();
        harness.ScriptContext.TaskResponse.ShouldContainKey(derivedKey);
        harness.ScriptContext.OutputResponse.ShouldContainKey(derivedKey);
    }

    private sealed class Harness
    {
        private readonly HttpTask _task = WorkflowTaskFactory.CreateHttpTask("shared-http-task");
        public IRemoteInvokerService RemoteInvoker { get; } = Substitute.For<IRemoteInvokerService>();
        public ScriptContext ScriptContext { get; }
        public string TaskKey => _task.Key;

        public Harness()
        {
            var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
            ScriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .SetInstance(instance)
                .Build();
        }

        public void StubInvokerToReturn(JsonElement data)
        {
            RemoteInvoker.InvokeAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                    Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
                .Returns(Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(data: data)));
        }

        public Task<Result<StandardTaskResponse>> ExecuteAsync(TaskTrigger trigger, string? responseVariableKey)
        {
            var executor = new HttpTaskExecutor(
                RemoteInvoker, Substitute.For<IScriptEngine>(), NullLogger<HttpTaskExecutor>.Instance);

            var onExecute = OnExecuteTask.Create(0, _task, ScriptCode.FromNative(string.Empty));
            var context = new TaskExecutorContext(_task, onExecute, ScriptContext, null, trigger, TaskExecutionOrigin.Flow)
            {
                ResponseVariableKey = responseVariableKey
            };

            return executor.ExecuteAsync(context, CancellationToken.None);
        }
    }
}
