using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Tasks;

/// <summary>
/// Builds realistic <see cref="TaskExecutorContext"/> fixtures for a <see cref="ScriptTask"/>, with or
/// without an <see cref="OnExecuteTask.Mapping"/> that has actual mapping code
/// (<see cref="ScriptCode.HasMappingCode"/>). (Extracted from the removed metrics test file — the
/// mapping-memo tests still build their contexts here.)
/// </summary>
internal static class TestTaskContexts
{
    public static TaskExecutorContext ScriptTaskWithMapping()
        => Build(ScriptCode.FromNative("return null;"));

    public static TaskExecutorContext ScriptTaskWithoutMapping()
        => Build(ScriptCode.FromNative(string.Empty));

    private static TaskExecutorContext Build(ScriptCode mapping)
    {
        var task = ScriptTask.Create(JsonSerializer.SerializeToElement(new { }));
        task.SetReference(new Reference("probe-script", "test", "sys-tasks", "1.0.0"));

        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(NSubstitute.Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .Build();

        var onExecute = OnExecuteTask.Create(1, task, mapping);

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }
}
