using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks.Factory;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Factory;

public sealed class PythonTaskPoolingTests
{
    [Fact]
    public void Registry_CreatesCopiesAndResetsPythonTask()
    {
        var source = PythonTask.Create(JsonDocument.Parse("""
            {
              "handler":"scorer",
              "input":{"value":42},
              "timeoutSeconds":12
            }
            """).RootElement);
        source.SetReference(new Reference("python", "domain", "sys-tasks", "1.0.0"));

        var target = PoolableTaskRegistry.TryCreateEmpty(typeof(PythonTask))
            .ShouldBeOfType<PythonTask>();
        PoolableTaskRegistry.TryCopyProperties(source, target).ShouldBeTrue();

        target.GetTaskType().ShouldBe(TaskType.Python);
        target.Handler.ShouldBe("scorer");
        target.Input!.Value.GetProperty("value").GetInt32().ShouldBe(42);
        target.TimeoutSeconds.ShouldBe(12);

        target.Reset();
        target.Handler.ShouldBeNull();
        target.Input.ShouldBeNull();
        target.TimeoutSeconds.ShouldBe(PythonTask.DefaultTimeoutSeconds);
    }
}
