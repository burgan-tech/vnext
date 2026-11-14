using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// A Start task's <c>version</c> config is the version the new instance resolves and starts with. The task reads
/// it once (<see cref="StartTask.TriggerVersion"/>); the same-domain path passes it to the start call and the
/// cross-domain path carries it on the remote binding, so a wrong read breaks both.
/// </summary>
public sealed class TaskBindingMapperStartTaskVersionTests
{
    [Fact]
    public void Create_ReadsTheVersionFromTheVersionField()
    {
        var task = CreateStartTask("""{ "domain": "partner", "flow": "order", "version": "1.0.2" }""");

        task.TriggerVersion.ShouldBe("1.0.2");
    }

    [Fact]
    public void CreateEnvelope_CarriesTheConfiguredVersion()
    {
        var task = CreateStartTask("""{ "domain": "partner", "flow": "order", "version": "1.0.2" }""");

        Binding(task).Version.ShouldBe("1.0.2");
    }

    [Fact]
    public void Create_WithoutAVersion_DoesNotTakeTheFlowName()
    {
        var task = CreateStartTask("""{ "domain": "partner", "flow": "order" }""");

        task.TriggerVersion.ShouldNotBe("order");
        Binding(task).Version.ShouldNotBe("order");
    }

    private static StartTask CreateStartTask(string config)
    {
        var task = StartTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("start-remote", "test-domain", "sys-tasks", "1.0.0"));
        return task;
    }

    private static StartTriggerBinding Binding(StartTask task)
    {
        var envelope = TaskBindingMapper.CreateEnvelope(task);

        envelope.IsSuccess.ShouldBeTrue();
        return envelope.Value!.Binding.Deserialize<StartTriggerBinding>()
               ?? throw new InvalidOperationException("The envelope carries no StartTriggerBinding.");
    }
}
