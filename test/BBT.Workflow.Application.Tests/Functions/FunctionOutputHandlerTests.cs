using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for Function multi-task execution and output handler behavior.
/// </summary>
public class FunctionOutputHandlerTests
{
    private static readonly Reference TaskRef = new("my-task", "test-domain", "sys-tasks", "1.0.0");
    private static readonly Reference Task2Ref = new("second-task", "test-domain", "sys-tasks", "1.0.0");

    private static OnExecuteTask CreateTask(int order, Reference reference) =>
        OnExecuteTask.Create(order, reference, ScriptCode.FromNative(string.Empty));

    // ─── GetExecuteTasks ───────────────────────────────────────────────────────

    [Fact]
    public void GetExecuteTasks_WithoutOnExecutionTasks_ReturnsSingleLegacyTask()
    {
        var legacyTask = CreateTask(1, TaskRef);
        var function = new Function(TaskScope.Domain, legacyTask);

        var tasks = function.GetExecuteTasks();

        tasks.ShouldHaveSingleItem();
        tasks[0].ShouldBe(legacyTask);
    }

    [Fact]
    public void GetExecuteTasks_WithOnExecutionTasks_ReturnsOnExecutionTasksList()
    {
        var legacyTask = CreateTask(1, TaskRef);
        var task1 = CreateTask(1, TaskRef);
        var task2 = CreateTask(2, Task2Ref);
        var function = new Function(TaskScope.Domain, legacyTask, onExecutionTasks: [task1, task2]);

        var tasks = function.GetExecuteTasks();

        tasks.Count.ShouldBe(2);
        tasks[0].ShouldBe(task1);
        tasks[1].ShouldBe(task2);
    }

    [Fact]
    public void GetExecuteTasks_WithEmptyOnExecutionTasksList_FallsBackToLegacyTask()
    {
        var legacyTask = CreateTask(1, TaskRef);
        var function = new Function(TaskScope.Domain, legacyTask, onExecutionTasks: []);

        var tasks = function.GetExecuteTasks();

        tasks.ShouldHaveSingleItem();
        tasks[0].ShouldBe(legacyTask);
    }

    // ─── Output property ──────────────────────────────────────────────────────

    [Fact]
    public void Output_IsNull_WhenNotProvided()
    {
        var function = new Function(TaskScope.Domain, CreateTask(1, TaskRef));

        function.Output.ShouldBeNull();
    }

    [Fact]
    public void Output_IsSet_WhenProvided()
    {
        var script = ScriptCode.FromNative("return new ScriptResponse();");
        var function = new Function(TaskScope.Domain, CreateTask(1, TaskRef), output: script);

        function.Output.ShouldNotBeNull();
        function.Output.DecodedCode.ShouldBe("return new ScriptResponse();");
    }

    // ─── JSON deserialization (backward compat) ────────────────────────────────

    [Fact]
    public void Function_DeserializesFromLegacyJson_WithoutOnExecutionTasksOrOutput()
    {
        var json = """
            {
                "scope": "D",
                "task": { "order": 1, "task": { "key": "my-task", "domain": "d", "flow": "f", "version": "1" }, "mapping": { "code": "" } }
            }
            """;

        var function = JsonSerializer.Deserialize<Function>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        function.ShouldNotBeNull();
        function!.OnExecutionTasks.ShouldBeEmpty();
        function.Output.ShouldBeNull();
        function.GetExecuteTasks().ShouldHaveSingleItem();
    }

    [Fact]
    public void Function_DeserializesOnExecutionTasksFromJson()
    {
        var json = """
            {
                "scope": "D",
                "task": { "order": 1, "task": { "key": "t1", "domain": "d", "flow": "f", "version": "1" }, "mapping": { "code": "" } },
                "onExecutionTasks": [
                    { "order": 1, "task": { "key": "check", "domain": "d", "flow": "f", "version": "1" }, "mapping": { "code": "" } },
                    { "order": 2, "task": { "key": "limit", "domain": "d", "flow": "f", "version": "1" }, "mapping": { "code": "" } }
                ]
            }
            """;

        var function = JsonSerializer.Deserialize<Function>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        function.ShouldNotBeNull();
        function!.OnExecutionTasks.Count.ShouldBe(2);
        function.GetExecuteTasks().Count.ShouldBe(2);
    }

    [Fact]
    public void Function_DeserializesOutputScriptFromJson()
    {
        var json = """
            {
                "scope": "D",
                "task": { "order": 1, "task": { "key": "t1", "domain": "d", "flow": "f", "version": "1" }, "mapping": { "code": "" } },
                "output": { "code": "", "encoding": "Native" }
            }
            """;

        var function = JsonSerializer.Deserialize<Function>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        function.ShouldNotBeNull();
        function!.Output.ShouldNotBeNull();
    }
}
