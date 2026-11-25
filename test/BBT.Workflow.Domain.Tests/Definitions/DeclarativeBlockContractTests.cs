using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

public class DeclarativeBlockContractTests
{
    private static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonSerializerConstants.JsonOptions)!;

    [Fact]
    public void Transition_Output_RoundTrips()
    {
        var json = """{"key":"t","target":"s2","output":{"select":"$.input.a","into":"$.a","set":{"$.z":"1","$.b":"now","$.a2":"2"}}}""";

        var transition = Read<Transition>(json);

        transition.Output.ShouldNotBeNull();
        transition.Output!.Set!.Keys.ToArray().ShouldBe(["$.z", "$.b", "$.a2"]);

        var again = Read<Transition>(JsonSerializer.Serialize(transition, JsonSerializerConstants.JsonOptions));
        again.Output.ShouldNotBeNull();
        again.Output!.Set!.Keys.ToArray().ShouldBe(["$.z", "$.b", "$.a2"]);
    }

    [Fact]
    public void Transition_HasDataMapping()
    {
        Read<Transition>("""{"key":"t","target":"s2","mapping":{"location":"./a.csx","code":"YQ=="}}""").HasDataMapping.ShouldBeTrue();
        Read<Transition>("""{"key":"t","target":"s2","output":{"set":{"$.b":"now"}}}""").HasDataMapping.ShouldBeTrue();
        Read<Transition>("""{"key":"t","target":"s2"}""").HasDataMapping.ShouldBeFalse();
    }

    [Fact]
    public void SubFlow_WithoutMapping_Deserializes()
    {
        var subFlow = Read<BBT.Workflow.Definitions.SubFlow>("""{"type":"S","process":{"key":"p","domain":"d","flow":"sys-flows","version":"1.0.0"},"input":{"set":{"$.a":"1"}},"output":{"set":{"$.b":"2"}}}""");

        subFlow.Mapping.ShouldBeNull();
        subFlow.Input.ShouldNotBeNull();
        subFlow.Output.ShouldNotBeNull();
    }

    [Fact]
    public void SubFlow_InputOutput_RoundTrip()
    {
        var subFlow = Read<BBT.Workflow.Definitions.SubFlow>("""{"type":"S","process":{"key":"p","domain":"d","flow":"sys-flows","version":"1.0.0"},"input":{"select":"$.data.c","into":"$.attributes.c","set":{"$.a":"1"}},"output":{"set":{"$.b":"2"}}}""");

        var again = Read<BBT.Workflow.Definitions.SubFlow>(JsonSerializer.Serialize(subFlow, JsonSerializerConstants.JsonOptions));

        again.Mapping.ShouldBeNull();
        again.Input.ShouldNotBeNull();
        again.Input!.Select.ShouldBe("$.data.c");
        again.Input.Into.ShouldBe("$.attributes.c");
        again.Input.Set!["$.a"].ShouldBe("1");
        again.Output.ShouldNotBeNull();
        again.Output!.Set!["$.b"].ShouldBe("2");
    }
}
