using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

/// <summary>
/// The declarative input/output blocks are a compatible extension of the task binding: a binding
/// without them keeps its exact meaning and its exact serialized form.
/// </summary>
public class OnExecuteTaskSerializationTests
{
    private static readonly JsonSerializerOptions Options = JsonSerializerConstants.JsonOptions;

    // Real binding shape from vnext-example (history-none-subflow-lab/hns-child.json), code body
    // shortened, in the canonical form the runtime writes (so the round trip can be byte-equal).
    private const string ExistingBinding =
        """{"order":1,"task":{"key":"hns-child-echo","flow":"sys-tasks","domain":"core","version":"1.0.0"},"mapping":{"location":"./src/HnsChildEchoMapping.csx","code":"dXNpbmcgU3lzdGVtOw==","type":"L","encoding":"B64"}}""";

    private const string DeclarativeBinding =
        """{"order":2,"task":{"key":"score-task","flow":"sys-tasks","domain":"core","version":"1.0.0"},"input":{"set":{"$.body.customerId":"data.customer.id","$.body.amount":"input.amount","$.headers.x-channel":"\u0022web\u0022","$.Body.Zeta":"1","$.a":"2"}},"output":{"select":"$.body.score","into":"$.kyc.score","set":{"$.kyc.checkedAt":"now","$.kyc.state":"instance.currentState"}}}""";

    [Fact]
    public void Deserialize_ExistingBindingWithMapping_Unchanged()
    {
        var task = JsonSerializer.Deserialize<OnExecuteTask>(ExistingBinding, Options)!;

        task.Mapping.ShouldNotBeNull();
        task.Mapping!.Location.ShouldBe("./src/HnsChildEchoMapping.csx");
        task.Input.ShouldBeNull();
        task.Output.ShouldBeNull();
        task.HasDeclarativeMapping.ShouldBeFalse();
        JsonSerializer.Serialize(task, Options).ShouldBe(ExistingBinding);
    }

    [Fact]
    public void Deserialize_DeclarativeBinding_ReadsBlocksInOrder()
    {
        var task = JsonSerializer.Deserialize<OnExecuteTask>(DeclarativeBinding, Options)!;

        task.Mapping.ShouldBeNull();
        task.HasDeclarativeMapping.ShouldBeTrue();
        task.Input!.Select.ShouldBeNull();
        task.Input.Into.ShouldBeNull();
        task.Input.Set!.Keys.ShouldBe(
            new[] { "$.body.customerId", "$.body.amount", "$.headers.x-channel", "$.Body.Zeta", "$.a" });
        task.Output!.Select.ShouldBe("$.body.score");
        task.Output.Into.ShouldBe("$.kyc.score");
        task.Output.Set!.Select(kv => kv.Key).ShouldBe(new[] { "$.kyc.checkedAt", "$.kyc.state" });
        task.Output.Set["$.kyc.state"].ShouldBe("instance.currentState");
    }

    [Fact]
    public void RoundTrip_DeclarativeBinding_IsByteEqualAndKeepsOrder()
    {
        var task = JsonSerializer.Deserialize<OnExecuteTask>(DeclarativeBinding, Options)!;

        JsonSerializer.Serialize(task, Options).ShouldBe(DeclarativeBinding);
    }

    [Fact]
    public void Deserialize_NoMappingNoBlocks_MappingNull()
    {
        var task = JsonSerializer.Deserialize<OnExecuteTask>(
            """{"order":1,"task":{"key":"t","domain":"core","version":"1.0.0","flow":"sys-tasks"}}""", Options)!;

        task.Mapping.ShouldBeNull();
        task.Input.ShouldBeNull();
        task.Output.ShouldBeNull();
        task.HasDeclarativeMapping.ShouldBeFalse();
        var json = JsonSerializer.Serialize(task, Options);
        json.ShouldNotContain("input");
        json.ShouldNotContain("output");
        json.ShouldNotContain("mapping");
    }

    [Fact]
    public void Deserialize_DuplicateSetKey_Throws() =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<OnExecuteTask>(
            """{"order":1,"task":{"key":"t","domain":"core","version":"1.0.0","flow":"sys-tasks"},"input":{"set":{"$.a":"1","$.a":"2"}}}""",
            Options));

    [Fact]
    public void Deserialize_NonStringSetValue_Throws() =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<OnExecuteTask>(
            """{"order":1,"task":{"key":"t","domain":"core","version":"1.0.0","flow":"sys-tasks"},"input":{"set":{"$.a":1}}}""",
            Options));
}
