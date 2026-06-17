using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
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

    // ─── RawResponse ──────────────────────────────────────────────────────────

    [Fact]
    public void RawResponse_DefaultsFalse()
    {
        var function = new Function(TaskScope.Domain, CreateTask(1, TaskRef));
        function.RawResponse.ShouldBeFalse();
    }

    [Fact]
    public void Function_DeserializesFromJson_WithoutRawResponse_DefaultsFalse()
    {
        var json = """
            {
                "scope": "D",
                "task": { "order": 1, "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "mapping": { "code": "" } }
            }
            """;

        var function = JsonSerializer.Deserialize<Function>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        function.ShouldNotBeNull();
        function!.RawResponse.ShouldBeFalse();
    }

    [Fact]
    public void Function_DeserializesRawResponseTrueFromJson()
    {
        var json = """
            {
                "scope": "D",
                "rawResponse": true,
                "task": { "order": 1, "task": { "key": "t", "domain": "d", "flow": "f", "version": "1" }, "mapping": { "code": "" } }
            }
            """;

        var function = JsonSerializer.Deserialize<Function>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        function.ShouldNotBeNull();
        function!.RawResponse.ShouldBeTrue();
    }

    [Fact]
    public void CreateRawResponse_UsesSingleTaskStatusCodeAndHeaders()
    {
        var function = CreateFunction(rawResponse: true);
        var context = CreateScriptContext();
        var taskKey = TaskRef.Key.ToVariableName();
        var payload = new Dictionary<string, object?>
        {
            ["title"] = "Validation failed",
            ["status"] = 400
        };
        context.SetStandardResponse(new StandardTaskResponse
        {
            Data = payload,
            StatusCode = 400,
            Headers = new Dictionary<string, string> { ["x-validation-source"] = "schema" }
        }, taskKey);
        context.SetOutputResponse(payload, taskKey);

        var rawData = FunctionAppService.ExtractRawFunctionResponse(function, context);
        var response = FunctionAppService.CreateRawResponse(function, context, rawData);

        response.StatusCode.ShouldBe(400);
        response.Headers.ShouldNotBeNull();
        response.Headers!["x-validation-source"].ShouldBe("schema");
        ((object?)((Dictionary<string, dynamic?>)response.Data)["title"]).ShouldBe("Validation failed");
        ((object?)((Dictionary<string, dynamic?>)response.Data)["status"]).ShouldBe(400L);
    }

    [Fact]
    public void CreateWrappedResponse_DoesNotForwardTaskStatusCode()
    {
        var function = CreateFunction(rawResponse: false);
        var context = CreateScriptContext();
        var taskKey = TaskRef.Key.ToVariableName();
        var payload = new Dictionary<string, object?> { ["title"] = "Validation failed" };
        context.SetStandardResponse(new StandardTaskResponse
        {
            Data = payload,
            StatusCode = 400,
            Headers = new Dictionary<string, string> { ["x-validation-source"] = "schema" }
        }, taskKey);
        context.SetOutputResponse(payload, taskKey);

        var response = FunctionAppService.CreateWrappedResponse(function, context);

        response.StatusCode.ShouldBeNull();
        response.Headers.ShouldBeNull();
        ((Dictionary<string, dynamic?>)response.Data).ShouldContainKey(function.Key.ToVariableName());
    }

    // ─── CreateRawResponse: output headers/status (multi-task) ─────────────────

    [Fact]
    public void CreateRawResponse_UsesOutputHeadersAndStatus_WhenProvided()
    {
        var function = CreateMultiTaskFunction(rawResponse: true);
        var context = CreateScriptContext();
        var outputHeaders = new Dictionary<string, string>
        {
            ["X-JWS-Signature"] = "eyJ...",
            ["X-Total-Count"] = "42"
        };

        var response = FunctionAppService.CreateRawResponse(
            function, context, data: new { ok = true }, outputHeaders: outputHeaders, outputStatusCode: 400);

        response.StatusCode.ShouldBe(400);
        response.Headers.ShouldNotBeNull();
        response.Headers!["X-JWS-Signature"].ShouldBe("eyJ...");
        response.Headers!["X-Total-Count"].ShouldBe("42");
    }

    [Fact]
    public void CreateRawResponse_OutputHeadersOverrideSingleTask()
    {
        var function = CreateFunction(rawResponse: true);
        var context = CreateScriptContext();
        var taskKey = TaskRef.Key.ToVariableName();
        context.SetStandardResponse(new StandardTaskResponse
        {
            Data = new Dictionary<string, object?> { ["title"] = "from task" },
            StatusCode = 201,
            Headers = new Dictionary<string, string> { ["x-source"] = "single-task" }
        }, taskKey);

        var response = FunctionAppService.CreateRawResponse(
            function, context, data: new { ok = true },
            outputHeaders: new Dictionary<string, string> { ["x-source"] = "output" },
            outputStatusCode: 410);

        response.StatusCode.ShouldBe(410);
        response.Headers!["x-source"].ShouldBe("output");
    }

    [Fact]
    public void CreateRawResponse_FallsBackToSingleTask_WhenOutputHeadersNull()
    {
        var function = CreateFunction(rawResponse: true);
        var context = CreateScriptContext();
        var taskKey = TaskRef.Key.ToVariableName();
        context.SetStandardResponse(new StandardTaskResponse
        {
            Data = new Dictionary<string, object?> { ["title"] = "from task" },
            StatusCode = 201,
            Headers = new Dictionary<string, string> { ["x-source"] = "single-task" }
        }, taskKey);

        var response = FunctionAppService.CreateRawResponse(
            function, context, data: new { ok = true });

        response.StatusCode.ShouldBe(201);
        response.Headers!["x-source"].ShouldBe("single-task");
    }

    [Fact]
    public void CreateRawResponse_MultiTaskWithoutOutputHeaders_HasNoHeaders()
    {
        var function = CreateMultiTaskFunction(rawResponse: true);
        var context = CreateScriptContext();

        var response = FunctionAppService.CreateRawResponse(function, context, data: new { ok = true });

        response.StatusCode.ShouldBeNull();
        response.Headers.ShouldBeNull();
    }

    [Fact]
    public void CreateRawResponse_NormalizesObjectValuedOutputHeaders()
    {
        var function = CreateMultiTaskFunction(rawResponse: true);
        var context = CreateScriptContext();
        var outputHeaders = new Dictionary<string, object?>
        {
            ["X-Total-Count"] = 42,
            ["X-Null"] = null
        };

        var response = FunctionAppService.CreateRawResponse(
            function, context, data: new { ok = true }, outputHeaders: outputHeaders);

        response.Headers.ShouldNotBeNull();
        response.Headers!["X-Total-Count"].ShouldBe("42");
        response.Headers.ShouldNotContainKey("X-Null");
    }

    private static Function CreateFunction(bool rawResponse)
    {
        var function = new Function(TaskScope.Domain, CreateTask(1, TaskRef), rawResponse: rawResponse);
        function.SetReference(new Reference("send-otp", "test-domain", "sys-functions", "1.0.0"));
        return function;
    }

    private static Function CreateMultiTaskFunction(bool rawResponse)
    {
        var function = new Function(
            TaskScope.Domain,
            CreateTask(1, TaskRef),
            onExecutionTasks: [CreateTask(1, TaskRef), CreateTask(2, Task2Ref)],
            rawResponse: rawResponse);
        function.SetReference(new Reference("get-hesaplar", "test-domain", "sys-functions", "1.0.0"));
        return function;
    }

    private static ScriptContext CreateScriptContext() =>
        new(NullLogger<ScriptContext>.Instance);
}
