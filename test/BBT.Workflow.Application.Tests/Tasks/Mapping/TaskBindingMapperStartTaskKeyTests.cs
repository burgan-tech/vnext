using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// A cross-domain Start task must carry the instance key. The remote binding is all the Execution host
/// sees, so a key that is not mapped onto it is dropped without an error and the target instance
/// starts keyless — while the same task in the same domain, and a SubProcess task on either path,
/// keep theirs.
/// </summary>
public sealed class TaskBindingMapperStartTaskKeyTests
{
    [Fact]
    public void CreateEnvelope_CarriesTheKeySetByTheMapping()
    {
        var task = CreateStartTask("""{ "domain": "partner", "flow": "order" }""");
        task.SetKey("order-42");

        Binding(task).Key.ShouldBe("order-42");
    }

    [Fact]
    public void CreateEnvelope_CarriesTheKeyFromTheTaskConfig()
    {
        var task = CreateStartTask("""{ "domain": "partner", "flow": "order", "key": "order-7" }""");

        Binding(task).Key.ShouldBe("order-7");
    }

    [Fact]
    public void CreateEnvelope_WithoutAKey_LeavesTheBindingKeyEmpty()
    {
        var task = CreateStartTask("""{ "domain": "partner", "flow": "order" }""");

        Binding(task).Key.ShouldBeNullOrEmpty();
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
