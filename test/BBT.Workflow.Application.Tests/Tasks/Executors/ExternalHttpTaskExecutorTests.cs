using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// End-to-end executor tests for the local HTTP task type (issue #399): a
/// <see cref="ExternalHttpTask"/> runs the full <see cref="TaskExecutorBase{TTask}"/> lifecycle
/// inside the orchestrator, performing the HTTP call in-process — no remote invoker involved.
/// </summary>
public sealed class ExternalHttpTaskExecutorTests
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";

    [Fact]
    public void TaskType_IsExternalHttp()
    {
        CreateExecutor(new StubHttpMessageHandler(Ok())).TaskType.ShouldBe(TaskType.ExternalHttp);
    }

    [Fact]
    public async Task ExecuteAsync_PerformsTheCallInProcess_AndReturnsParsedResponse()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok": true}""")
        });
        var executor = CreateExecutor(handler);
        var task = CreateTask("""
        {
            "url": "https://api.example.com/orders",
            "method": "POST",
            "body": { "orderId": 7 }
        }
        """);

        var result = await executor.ExecuteAsync(CreateContext(task));

        result.IsSuccess.ShouldBeTrue();
        var response = result.Value!;
        response.IsSuccess.ShouldBeTrue();
        response.StatusCode.ShouldBe(200);
        response.TaskType.ShouldBe("ExternalHttp");
        // The request was flattened through the shared TaskBindingMapper and sent in-process.
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.example.com/orders");
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        handler.LastBody!.ShouldContain("\"orderId\"");
    }

    /// <summary>
    /// The <c>HttpTask</c> arm of <c>TaskExecutorBase.GetAcceptedStatusCodes</c> must match the
    /// derived local task, or the accepted-status-code configuration would be silently ignored.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AcceptedStatusCodes_TurnErrorResponseIntoSuccess()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"missing": true}""")
        });
        var executor = CreateExecutor(handler);
        var task = CreateTask("""
        {
            "url": "https://api.example.com/orders/7",
            "method": "GET",
            "acceptedStatusCodes": [ "404" ]
        }
        """);

        var result = await executor.ExecuteAsync(CreateContext(task));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task ExecuteAsync_TransportFailure_ReturnsFailedResponseForTheErrorBoundary()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("connection refused"));
        var executor = CreateExecutor(handler);
        var task = CreateTask("""{ "url": "https://unreachable.example.com", "method": "GET" }""");

        var result = await executor.ExecuteAsync(CreateContext(task));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage.ShouldBe("connection refused");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    private static ExternalHttpTask CreateTask(string config)
    {
        var task = ExternalHttpTask.Create(JsonDocument.Parse(config).RootElement);
        task.SetReference(new Reference("local-call", TestDomain, "sys-tasks", TestVersion));
        return task;
    }

    private static ExternalHttpTaskExecutor CreateExecutor(HttpMessageHandler handler)
    {
        var invoker = new ExternalHttpTaskInvoker(
            new SingleClientFactory(handler),
            NullLogger<ExternalHttpTaskInvoker>.Instance);

        // No mapping code is attached in these tests, so the script engine is never invoked.
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.CreateTraceContext(Arg.Any<ScriptContext>())
            .Returns(TaskTraceContext.Create(
                instanceId: Guid.Empty, domain: "test", workflowKey: "test", workflowVersion: "1.0.0",
                correlationId: null, headers: null, instanceDataJson: null,
                traceParent: null, traceState: null, sub: null, actSub: null, requestId: null));

        return new ExternalHttpTaskExecutor(
            invoker,
            Substitute.For<IScriptEngine>(),
            remoteInvoker,
            NullLogger<ExternalHttpTaskExecutor>.Instance);
    }

    private static TaskExecutorContext CreateContext(ExternalHttpTask task)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var instance = Instances.Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "ctx-key");

        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .SetWorkflow(workflow)
            .Build();

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public StubHttpMessageHandler(HttpResponseMessage response)
            : this(_ => response)
        {
        }

        public StubHttpMessageHandler(Exception exception)
            : this(_ => throw exception)
        {
        }

        private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return _responder(request);
        }
    }
}
