using System;
using System.Text;
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
              "type": "22",
              "config": {
                "script": {
                  "location": "risk.py",
                  "code": "def main(input):\n    return input",
                  "encoding": "NAT"
                },
                "executionMode": "process",
                "input": { "score": 42 },
                "timeoutSeconds": 12
              }
            }
            """;

        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions)
            .ShouldBeOfType<PythonTask>();

        task.GetTaskType().ShouldBe(TaskType.Python);
        task.Script.ShouldNotBeNull();
        task.Script!.DecodedCode.ShouldContain("def main");
        task.Script.Location.ShouldBe("risk.py");
        task.ExecutionMode.ShouldBe("process");
        task.Input!.Value.GetProperty("score").GetInt32().ShouldBe(42);
        task.TimeoutSeconds.ShouldBe(12);
    }

    [Fact]
    public void CloneAndReset_PreserveThenClearPythonFields()
    {
        var task = PythonTask.Create(JsonDocument.Parse("""
            {
              "script": {"code":"def main(input): return input", "encoding":"NAT"},
              "executionMode":"container",
              "input":[1,2],
              "timeoutSeconds":20
            }
            """).RootElement);
        task.SetReference(new Reference("python", "domain", "sys-tasks", "1.0.0"));

        var clone = task.CloneTyped();
        clone.ExecutionMode.ShouldBe("container");
        clone.Input!.Value.GetArrayLength().ShouldBe(2);
        clone.TimeoutSeconds.ShouldBe(20);
        clone.Script!.DecodedCode.ShouldContain("return input");

        clone.Reset();
        clone.Script.ShouldBeNull();
        clone.ExecutionMode.ShouldBeNull();
        clone.Input.ShouldBeNull();
        clone.TimeoutSeconds.ShouldBe(PythonTask.DefaultTimeoutSeconds);
    }

    [Fact]
    public void Deserialize_DecodesBase64Script()
    {
        const string source = "def main(input): return {'ok': True}";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        var json = JsonSerializer.Serialize(new
        {
            type = "22",
            config = new { script = new { code = base64, encoding = "B64" } }
        });

        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions)
            .ShouldBeOfType<PythonTask>();

        task.Script!.DecodedCode.ShouldBe(source);
        task.TimeoutSeconds.ShouldBe(PythonTask.DefaultTimeoutSeconds);
    }
}
