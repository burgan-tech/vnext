using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

public sealed class TaskBindingMapperStateStoreTaskTests
{
    [Fact]
    public void CreateEnvelope_MapsWriteCacheTask()
    {
        var config = """
            {
              "command": "writeCache",
              "storeName": "vnext-state",
              "key": "customer:42:profile",
              "value": { "name": "Ada", "count": 1 },
              "ttlInSeconds": 300,
              "consistency": "strong",
              "concurrency": "lastWrite"
            }
            """;
        var task = StateStoreTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("write-cache", "test-domain", "sys-tasks", "1.0.0"));

        var result = TaskBindingMapper.CreateEnvelope(task);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.TaskType.ShouldBe(TaskTypes.StateStore);

        var binding = result.Value!.Binding.Deserialize<StateStoreBinding>();
        binding.ShouldNotBeNull();
        binding!.Command.ShouldBe("writeCache");
        binding.StoreName.ShouldBe("vnext-state");
        binding.Key.ShouldBe("customer:42:profile");
        binding.TtlInSeconds.ShouldBe(300);
        binding.Consistency.ShouldBe("strong");
        binding.Concurrency.ShouldBe("lastWrite");

        var parsedValue = JsonSerializer.Deserialize<JsonElement>(binding.Value!);
        parsedValue.GetProperty("name").GetString().ShouldBe("Ada");
        parsedValue.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void CreateEnvelope_DefaultsStoreName_WhenOmitted()
    {
        var config = """
            {
              "command": "getCache",
              "key": "customer:42:profile"
            }
            """;
        var task = StateStoreTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("get-cache", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.Command.ShouldBe("getCache");
        binding.StoreName.ShouldBe(StateStoreTask.DefaultStoreName);
        binding.Key.ShouldBe("customer:42:profile");
        binding.Value.ShouldBeNull();
    }

    [Fact]
    public void CreateEnvelope_MapsInvalidateCacheKeyList()
    {
        var config = """
            {
              "command": "invalidateCache",
              "keys": [ "customer:1", "customer:2" ]
            }
            """;
        var task = StateStoreTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("invalidate-cache", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.Command.ShouldBe("invalidateCache");
        binding.Keys.ShouldNotBeNull();
        binding.Keys!.ShouldBe(["customer:1", "customer:2"]);
    }

    private static StateStoreBinding MapToBinding(StateStoreTask task)
    {
        var result = TaskBindingMapper.CreateEnvelope(task);
        result.IsSuccess.ShouldBeTrue();
        var binding = result.Value!.Binding.Deserialize<StateStoreBinding>();
        binding.ShouldNotBeNull();
        return binding!;
    }
}
