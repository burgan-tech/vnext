using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Events;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

using Workflow = BBT.Workflow.Definitions.Workflow;

/// <summary>
/// Contract of the declarative <c>output</c> member on workflow and function definitions and of the
/// event <c>input</c> block: script and block shapes, the mixed-shape rejection and the round-trip of
/// today's script form.
/// </summary>
public class DeclarativeOutputContractTests
{
    private const string ScriptJson = """{"location":"./output.csx","code":"YQ==","type":"L","encoding":"B64"}""";

    private static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonSerializerConstants.JsonOptions)!;

    private static string Write<T>(T value) =>
        JsonSerializer.Serialize(value, JsonSerializerConstants.JsonOptions);

    private static string OutputMemberOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("output").GetRawText();
    }

    [Fact]
    public void Output_Script_RoundTrips()
    {
        var workflow = Read<Workflow>($$"""{"type":"F","output":{{ScriptJson}}}""");

        workflow.Output.ShouldNotBeNull();
        workflow.Output!.Location.ShouldBe("./output.csx");
        workflow.Output.Code.ShouldBe("YQ==");
        workflow.OutputBlock.ShouldBeNull();

        OutputMemberOf(Write(workflow)).ShouldBe(ScriptJson);
        OutputMemberOf(Write(workflow)).ShouldBe(Write(workflow.Output));
    }

    [Fact]
    public void Output_Block_RoundTrips()
    {
        var workflow = Read<Workflow>("""{"type":"F","output":{"select":"$.data","into":"$.data","set":{"$.statusCode":"201","$.data.b":"now","$.data.a":"1"}}}""");

        workflow.Output.ShouldBeNull();
        workflow.OutputBlock.ShouldNotBeNull();
        workflow.OutputBlock!.Select.ShouldBe("$.data");
        workflow.OutputBlock.Into.ShouldBe("$.data");
        workflow.OutputBlock.Set!.Keys.ToArray().ShouldBe(["$.statusCode", "$.data.b", "$.data.a"]);

        var again = Read<Workflow>(Write(workflow));
        again.Output.ShouldBeNull();
        again.OutputBlock.ShouldNotBeNull();
        again.OutputBlock!.Set!.Keys.ToArray().ShouldBe(["$.statusCode", "$.data.b", "$.data.a"]);
    }

    [Theory]
    [InlineData("""{"code":"x","set":{}}""")]
    [InlineData("""{"location":"./o.csx","select":"$.data"}""")]
    [InlineData("""{"code":"x","into":"$.data"}""")]
    public void Output_ScriptAndBlockMembers_FailsToDeserialize(string output)
    {
        Should.Throw<JsonException>(() => Read<Workflow>($$"""{"output":{{output}}}"""));
        Should.Throw<JsonException>(() => Read<Function>($$"""{"scope":"D","output":{{output}}}"""));
    }

    [Fact]
    public void Output_Null_IsNone()
    {
        var explicitNull = Read<Workflow>("""{"output":null}""");
        explicitNull.Output.ShouldBeNull();
        explicitNull.OutputBlock.ShouldBeNull();

        var absent = Read<Workflow>("{}");
        absent.Output.ShouldBeNull();
        absent.OutputBlock.ShouldBeNull();
    }

    [Fact]
    public void Workflow_SetOutput_KeepsScript()
    {
        var workflow = Read<Workflow>("{}");
        var script = Read<ScriptCode>(ScriptJson);

        workflow.SetOutput(script);

        workflow.Output.ShouldBe(script);
        workflow.OutputBlock.ShouldBeNull();
    }

    [Fact]
    public void Function_OutputBlock_Deserializes()
    {
        var function = Read<Function>("""{"scope":"D","output":{"set":{"$.data.total":"result.calc.body.total","$.statusCode":"200"}}}""");

        function.Output.ShouldBeNull();
        function.OutputBlock.ShouldNotBeNull();
        function.OutputBlock!.Set!.Keys.ToArray().ShouldBe(["$.data.total", "$.statusCode"]);

        var again = Read<Function>(Write(function));
        again.OutputBlock.ShouldNotBeNull();
        again.OutputBlock!.Set!["$.data.total"].ShouldBe("result.calc.body.total");
    }

    [Fact]
    public void Function_OutputScript_RoundTrips()
    {
        var function = Read<Function>($$"""{"scope":"D","output":{{ScriptJson}}}""");

        function.Output.ShouldNotBeNull();
        function.OutputBlock.ShouldBeNull();
        OutputMemberOf(Write(function)).ShouldBe(ScriptJson);
    }

    [Fact]
    public void Function_ConstructedWithScript_ExposesScript()
    {
        var script = Read<ScriptCode>(ScriptJson);

        var function = new Function(TaskScope.Domain, task: null, output: script);

        function.Output.ShouldBe(script);
        function.OutputBlock.ShouldBeNull();
    }

    [Fact]
    public void Event_InputOnly_Deserializes()
    {
        var @event = Read<Event>("""{"input":{"select":"$.input","into":"$.attributes","set":{"$.key":"input.orderId"}}}""");

        @event.Mapping.ShouldBeNull();
        @event.Input.ShouldNotBeNull();
        @event.Input!.Select.ShouldBe("$.input");
        @event.Input.Into.ShouldBe("$.attributes");
        @event.Input.Set!["$.key"].ShouldBe("input.orderId");
    }

    [Fact]
    public void Event_MappingOnly_KeepsScript()
    {
        var @event = Read<Event>($$"""{"mapping":{{ScriptJson}}}""");

        @event.Mapping.ShouldNotBeNull();
        @event.Mapping!.Location.ShouldBe("./output.csx");
        @event.Input.ShouldBeNull();
    }
}
