using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Tasks;

public class FanOutTaskTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private const string ValidConfig = """
    {
      "mode": "inline",
      "itemsPath": "$.documents",
      "itemAlias": "document",
      "task": { "key": "process-doc", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
      "execution": { "maxDegreeOfParallelism": 5, "itemTimeoutSeconds": 30, "batchTimeoutSeconds": 120 },
      "join": { "policy": "allSettled", "resultKey": "documentResults", "ordered": true }
    }
    """;

    [Fact]
    public void Configure_Should_Parse_Valid_Config()
    {
        var task = FanOutTask.Create(Parse(ValidConfig));

        task.GetTaskType().ShouldBe(TaskType.FanOut);
        task.ItemsPath.ShouldBe("$.documents");
        task.ItemAlias.ShouldBe("document");
        task.ItemTask.ShouldNotBeNull();
        task.ItemTask.Key.ShouldBe("process-doc");
        task.ItemTask.Domain.ShouldBe("core");
        task.MaxDegreeOfParallelism.ShouldBe(5);
        task.ItemTimeoutSeconds.ShouldBe(30);
        task.BatchTimeoutSeconds.ShouldBe(120);
        task.JoinPolicy.ShouldBe(FanOutJoinPolicy.AllSettled);
        task.ResultKey.ShouldBe("documentResults");
        task.Ordered.ShouldBeTrue();
    }

    [Fact]
    public void Configure_Should_Apply_Defaults()
    {
        var task = FanOutTask.Create(Parse("""
        { "itemsPath": "$.items",
          "task": { "key": "t", "domain": "d", "flow": "f", "version": "1.0.0" } }
        """));

        task.Mode.ShouldBe("inline");
        task.MaxDegreeOfParallelism.ShouldBe(4);
        task.ItemTimeoutSeconds.ShouldBe(30);
        task.BatchTimeoutSeconds.ShouldBe(120);
        task.JoinPolicy.ShouldBe(FanOutJoinPolicy.AllSettled);
        task.Ordered.ShouldBeTrue();
        task.ResultKey.ShouldBe("fanOutResults");
    }

    [Theory]
    [InlineData("""{ "mode": "durable", "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" } }""")]
    [InlineData("""{ "itemsPath": "$.x" }""")]
    [InlineData("""{ "itemsPath": "documents", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "join": { "policy": "quorum" } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "execution": { "maxDegreeOfParallelism": 0 } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "execution": { "itemTimeoutSeconds": 300, "batchTimeoutSeconds": 120 } }""")]
    public void Configure_Should_Reject_Invalid_Config(string json)
    {
        Should.Throw<ArgumentException>(() => FanOutTask.Create(Parse(json)));
    }

    [Fact]
    public void Clone_Should_Copy_All_Properties_And_Reset_Should_Clear()
    {
        var task = FanOutTask.Create(Parse(ValidConfig));
        task.SetReference(new Reference("fan-out-task", "core", "sys-tasks", "1.0.0"));

        var clone = (FanOutTask)task.Clone();
        clone.ItemsPath.ShouldBe(task.ItemsPath);
        clone.ItemTask!.Key.ShouldBe("process-doc");
        clone.JoinPolicy.ShouldBe(task.JoinPolicy);

        clone.Reset();
        clone.ItemsPath.ShouldBeNull();
        clone.ItemTask.ShouldBeNull();
        clone.MaxDegreeOfParallelism.ShouldBe(4);
    }

    [Fact]
    public void Should_Deserialize_Via_Polymorphic_Discriminator_21()
    {
        var json = $$"""{ "type": "21", "config": {{ValidConfig}} }""";
        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions);
        task.ShouldBeOfType<FanOutTask>();
    }
}
