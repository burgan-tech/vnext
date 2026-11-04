using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

public sealed class PythonTaskTests
{
    [Fact]
    public void Deserialize_ParsesPythonTaskContract()
    {
        var json = """
            {
              "type": "23",
              "config": {
                "handler": "credit_score",
                "input": { "score": 42 },
                "timeoutSeconds": 12
              }
            }
            """;

        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions)
            .ShouldBeOfType<PythonTask>();

        task.GetTaskType().ShouldBe(TaskType.Python);
        task.Handler.ShouldBe("credit_score");
        task.Input!.Value.GetProperty("score").GetInt32().ShouldBe(42);
        task.TimeoutSeconds.ShouldBe(12);
    }

    [Fact]
    public void Deserialize_DefaultsTimeoutAndLeavesInputEmpty()
    {
        var task = JsonSerializer.Deserialize<WorkflowTask>(
                """{"type":"23","config":{"handler":"noop"}}""", JsonSerializerConstants.JsonOptions)
            .ShouldBeOfType<PythonTask>();

        task.Input.ShouldBeNull();
        task.TimeoutSeconds.ShouldBe(PythonTask.DefaultTimeoutSeconds);
    }

    [Fact]
    public void SetInput_ReplacesTheConfiguredInput()
    {
        var task = PythonTask.Create(JsonDocument.Parse("""{"handler":"h","input":{"a":1}}""").RootElement);

        task.SetInput(new { b = 2 });

        task.Input!.Value.TryGetProperty("a", out _).ShouldBeFalse();
        task.Input!.Value.GetProperty("b").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void CloneAndReset_PreserveThenClearPythonFields()
    {
        var task = PythonTask.Create(JsonDocument.Parse("""
            {
              "handler":"scorer",
              "input":[1,2],
              "timeoutSeconds":20
            }
            """).RootElement);
        task.SetReference(new Reference("python", "domain", "sys-tasks", "1.0.0"));

        var clone = task.CloneTyped();
        clone.Handler.ShouldBe("scorer");
        clone.Input!.Value.GetArrayLength().ShouldBe(2);
        clone.TimeoutSeconds.ShouldBe(20);

        clone.Reset();
        clone.Handler.ShouldBeNull();
        clone.Input.ShouldBeNull();
        clone.TimeoutSeconds.ShouldBe(PythonTask.DefaultTimeoutSeconds);
    }
}
