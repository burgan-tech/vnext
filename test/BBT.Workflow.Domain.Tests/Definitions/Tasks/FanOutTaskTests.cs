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

    private const string FullConfig = """
    {
      "mode": "inline",
      "itemsPath": "$.documents",
      "itemAlias": "document",
      "task": { "key": "process-doc", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
      "execution": { "maxDegreeOfParallelism": 5, "itemTimeoutSeconds": 30, "batchTimeoutSeconds": 120 },
      "join": { "policy": "quorum", "minSuccess": 2, "resultKey": "documentResults", "ordered": true },
      "errorBoundary": { "onError": [ { "action": "ignore", "priority": 1 } ] }
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
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f" } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "" } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "execution": { "itemTimeoutSeconds": 0 } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "join": { "policy": "bogus" } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "join": { "policy": "0" } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "join": { "policy": "99" } }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": "oops" }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "execution": [] }""")]
    [InlineData("""{ "itemsPath": "$.x", "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "join": [] }""")]
    public void Configure_Should_Reject_Invalid_Config(string json)
    {
        Should.Throw<ArgumentException>(() => FanOutTask.Create(Parse(json)));
    }

    [Fact]
    public void Configure_Should_Accept_Valid_Quorum_Policy()
    {
        var task = FanOutTask.Create(Parse("""
        { "itemsPath": "$.x",
          "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" },
          "join": { "policy": "quorum", "minSuccess": 3 } }
        """));

        task.JoinPolicy.ShouldBe(FanOutJoinPolicy.Quorum);
        task.MinSuccess.ShouldBe(3);
    }

    [Fact]
    public void Configure_Should_Parse_ErrorBoundary_When_Present()
    {
        var task = FanOutTask.Create(Parse("""
        { "itemsPath": "$.x",
          "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" },
          "errorBoundary": { "onError": [ { "action": "ignore", "priority": 1 } ] } }
        """));

        task.ItemErrorBoundary.ShouldNotBeNull();
        task.ItemErrorBoundary!.OnError.Count.ShouldBe(1);
        task.ItemErrorBoundary.OnError[0].Action.ShouldBe(ErrorAction.Ignore);
        task.ItemErrorBoundary.OnError[0].Priority.ShouldBe(1);
    }

    [Fact]
    public void Clone_Should_Copy_All_Properties_And_Reset_Should_Clear()
    {
        var task = FanOutTask.Create(Parse(FullConfig));
        task.SetReference(new Reference("fan-out-task", "core", "sys-tasks", "1.0.0"));

        var clone = (FanOutTask)task.Clone();

        clone.Mode.ShouldBe(task.Mode);
        clone.ItemsPath.ShouldBe(task.ItemsPath);
        clone.ItemAlias.ShouldBe(task.ItemAlias);
        clone.ItemTask.ShouldNotBeNull();
        clone.ItemTask!.Key.ShouldBe(task.ItemTask!.Key);
        clone.ItemTask.Domain.ShouldBe(task.ItemTask.Domain);
        clone.ItemTask.Flow.ShouldBe(task.ItemTask.Flow);
        clone.ItemTask.Version.ShouldBe(task.ItemTask.Version);
        clone.MaxDegreeOfParallelism.ShouldBe(task.MaxDegreeOfParallelism);
        clone.ItemTimeoutSeconds.ShouldBe(task.ItemTimeoutSeconds);
        clone.BatchTimeoutSeconds.ShouldBe(task.BatchTimeoutSeconds);
        clone.JoinPolicy.ShouldBe(task.JoinPolicy);
        clone.MinSuccess.ShouldBe(task.MinSuccess);
        clone.ResultKey.ShouldBe(task.ResultKey);
        clone.Ordered.ShouldBe(task.Ordered);
        clone.ItemErrorBoundary.ShouldNotBeNull();
        clone.ItemErrorBoundary!.OnError.Count.ShouldBe(task.ItemErrorBoundary!.OnError.Count);

        clone.Reset();

        clone.Mode.ShouldBe(FanOutTask.InlineMode);
        clone.ItemsPath.ShouldBeNull();
        clone.ItemAlias.ShouldBeNull();
        clone.ItemTask.ShouldBeNull();
        clone.MaxDegreeOfParallelism.ShouldBe(FanOutTask.DefaultMaxDegreeOfParallelism);
        clone.ItemTimeoutSeconds.ShouldBe(FanOutTask.DefaultItemTimeoutSeconds);
        clone.BatchTimeoutSeconds.ShouldBe(FanOutTask.DefaultBatchTimeoutSeconds);
        clone.JoinPolicy.ShouldBe(FanOutJoinPolicy.AllSettled);
        clone.MinSuccess.ShouldBeNull();
        clone.ResultKey.ShouldBe(FanOutTask.DefaultResultKey);
        clone.Ordered.ShouldBeTrue();
        clone.ItemErrorBoundary.ShouldBeNull();
    }

    [Fact]
    public void Should_Deserialize_Via_Polymorphic_Discriminator_21()
    {
        var json = $$"""{ "type": "21", "config": {{ValidConfig}} }""";
        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions);
        task.ShouldBeOfType<FanOutTask>();
    }
}
