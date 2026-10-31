using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// vnext-client-sdk-core#142-D: an input mapping sets the per-call shape of a DaprBinding task — the object key of the
/// file to delete or read is only known at run time — and the envelope carries what the mapping set.
/// </summary>
public sealed class TaskBindingMapperDaprBindingTaskTests
{
    [Fact]
    public void MappingSetters_ReachTheEnvelope()
    {
        var task = DaprBindingTask.Create(JsonDocument.Parse("""{ "bindingName": "vnext-blob-s3", "operation": "get", "metadata": { "key": "static" } }""").RootElement);
        task.SetOperation("delete");
        task.SetMetadata(new Dictionary<string, string?> { ["key"] = "rt/0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80" });
        task.SetData(new { reason = "retention" });

        var envelope = TaskBindingMapper.CreateEnvelope(task).Value!;
        var binding = envelope.Binding.Deserialize<DaprBindingTaskBinding>()!;

        binding.BindingName.ShouldBe("vnext-blob-s3");
        binding.Operation.ShouldBe("delete");
        binding.Metadata!["key"].ShouldBe("rt/0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80");
        JsonDocument.Parse(binding.Body!).RootElement.GetProperty("reason").GetString().ShouldBe("retention");
    }

    [Fact]
    public void WithoutMappingSetters_TheDefinitionIsUsedAsBefore()
    {
        var task = DaprBindingTask.Create(JsonDocument.Parse("""{ "bindingName": "b", "operation": "list", "metadata": { "prefix": "rt/" } }""").RootElement);
        var binding = TaskBindingMapper.CreateEnvelope(task).Value!.Binding.Deserialize<DaprBindingTaskBinding>()!;
        binding.Operation.ShouldBe("list");
        binding.Metadata!["prefix"].ShouldBe("rt/");
        binding.Body.ShouldBeNull();
    }
}
