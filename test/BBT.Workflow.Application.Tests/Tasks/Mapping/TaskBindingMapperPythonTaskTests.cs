using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

public sealed class TaskBindingMapperPythonTaskTests
{
    [Fact]
    public void CreateEnvelope_DecodesScriptAndPreservesJsonInput()
    {
        var source = "def main(input):\n    return {'value': input['value'] * 2}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source));
        var task = PythonTask.Create(JsonDocument.Parse($$"""
            {
              "script":{"location":"double.py","code":"{{encoded}}","encoding":"B64"},
              "executionMode":"process",
              "input":{"value":21},
              "timeoutSeconds":15
            }
            """).RootElement);
        task.SetReference(new Reference("double", "test", "sys-tasks", "1.0.0"));

        var result = TaskBindingMapper.CreateEnvelope(task);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.TaskType.ShouldBe(TaskTypes.Python);
        var binding = result.Value.Binding.Deserialize<PythonTaskBinding>();
        binding.ShouldNotBeNull();
        binding!.Script.ShouldBe(source);
        binding.Location.ShouldBe("double.py");
        binding.ExecutionMode.ShouldBe("process");
        binding.Input!.Value.GetProperty("value").GetInt32().ShouldBe(21);
        binding.TimeoutSeconds.ShouldBe(15);
    }
}
