using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Application.Tests.Tasks.Invocation;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// A Python task is a direct Dapr call from the Orchestration host to the domain's Python runtime
/// app-id, in the v1 runtime contract. No Execution hop, and the handler output (not the contract
/// envelope) is the task result.
/// </summary>
public sealed class PythonTaskExecutorTests
{
    private static readonly Guid InstanceId = Guid.NewGuid();

    [Fact]
    public async Task Invoke_PostsTheContractRequestToTheRuntimeAppId()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"contractVersion":"1","status":"success","output":{"total":10}}""");
        });

        var response = await ExecuteAsync(handler, appId: "core-python-runtime");

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.Host.ShouldBe("core-python-runtime");
        handler.LastRequest.RequestUri.AbsolutePath.ShouldBe("/v1/invoke");

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("contractVersion").GetString().ShouldBe("1");
        sent.RootElement.GetProperty("handler").GetString().ShouldBe("sum_values");
        sent.RootElement.GetProperty("input").GetProperty("values").GetArrayLength().ShouldBe(3);
        sent.RootElement.GetProperty("timeoutMs").GetInt32().ShouldBe(12_000);
        sent.RootElement.GetProperty("context").GetProperty("taskKey").GetString().ShouldBe("sum");

        response.IsSuccess.ShouldBeTrue();
        ((JsonElement)response.Data!).GetProperty("total").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Invoke_HandlerError_FailsWithTheRuntimeErrorCodeAndMessage()
    {
        var handler = new StubHttpMessageHandler(Json(
            HttpStatusCode.UnprocessableEntity,
            """{"contractVersion":"1","status":"error","error":{"code":"handler_error","message":"division by zero"}}"""));

        var response = await ExecuteAsync(handler, appId: "core-python-runtime");

        response.IsSuccess.ShouldBeFalse();
        response.StatusCode.ShouldBe(422);
        ((string)response.ErrorMessage!).ShouldBe("handler_error: division by zero");
    }

    [Fact]
    public async Task Invoke_NoRuntimeConfigured_FailsWith503WithoutCallingAnything()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("must not be called"));

        var response = await ExecuteAsync(handler, appId: "");

        response.IsSuccess.ShouldBeFalse();
        response.StatusCode.ShouldBe(503);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Invoke_MappedInputOverTheLimit_FailsWith413WithoutCallingTheRuntime()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("must not be called"));

        var response = await ExecuteAsync(handler, appId: "core-python-runtime",
            configure: task => task.SetInput(new { blob = new string('x', PythonTask.MaxInputBytes) }));

        response.IsSuccess.ShouldBeFalse();
        response.StatusCode.ShouldBe(413);
        ((string)response.ErrorMessage!).ShouldStartWith("request_too_large: ");
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Invoke_RuntimeNeverAnswers_FailsWith408()
    {
        var response = await ExecuteAsync(
            new HangingHandler(), appId: "core-python-runtime", timeoutSeconds: 1, graceSeconds: 1);

        response.IsSuccess.ShouldBeFalse();
        response.StatusCode.ShouldBe(408);
    }

    private static async Task<StandardTaskResponse> ExecuteAsync(
        HttpMessageHandler handler, string appId, int timeoutSeconds = 12, int graceSeconds = 5,
        Action<PythonTask>? configure = null)
    {
        var task = PythonTask.Create($$"""
            {"handler":"sum_values","input":{"values":[2,3,5]},"timeoutSeconds":{{timeoutSeconds}}}
            """.ToJsonElement());
        task.SetReference(new Reference("sum", "core", "sys-tasks", "1.0.0"));
        configure?.Invoke(task);

        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.CreateTraceContext(Arg.Any<ScriptContext>())
            .Returns(new BBT.Workflow.Tasks.TaskTraceContext { InstanceId = InstanceId, Domain = "core", WorkflowKey = "calc" });

        var executor = new PythonTaskExecutor(
            remoteInvoker,
            Substitute.For<IScriptEngine>(),
            new DaprServiceInvocationClient(new HttpClient(handler)),
            Options.Create(new PythonRuntimeOptions { AppId = appId, TimeoutGraceSeconds = graceSeconds }),
            NullLogger<PythonTaskExecutor>.Instance);

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetHeaders(new Dictionary<string, string>())
            .SetInstance(Instance.Create(InstanceId, "calc", "1.0", "ctx-key"))
            .Build();
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));

        var result = await executor.ExecuteAsync(
            new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        return result.Value!;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
