using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using TaskEnvelope = BBT.Workflow.Tasks.TaskEnvelope;
using TaskTraceContext = BBT.Workflow.Tasks.TaskTraceContext;
using TaskInvokeResponse = BBT.Workflow.Tasks.TaskInvokeResponse;
using TaskInvocationResult = BBT.Workflow.Tasks.TaskInvocationResult;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

using BBT.Aether.Tracing;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Tests the per-invocation Dapr timeout and parent-cancellation propagation in
/// <see cref="RemoteInvokerService"/>.
/// </summary>
/// <remarks>
/// The HTTP path runs through the SDK's REAL <see cref="InvocationHandler"/> over a recording stub
/// — the same pipeline <c>DaprClient.CreateInvokeHttpClient()</c> builds — so what these tests
/// observe is exactly what the sidecar would receive. No <c>DaprClient</c> mock: the obsolete
/// <c>InvokeMethod*</c> family is no longer called.
/// </remarks>
public class RemoteInvokerServiceTests
{
    private const string Sidecar = "http://127.0.0.1:3500";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Mock<ICorrelationIdProvider> _correlationIdProvider = new();
    private readonly Mock<ILogger<RemoteInvokerService>> _logger = new();
    private readonly RecordingHandler _stub = new();

    private RemoteInvokerService CreateService(int invocationTimeoutSeconds = 60)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExecutionApi:AppId"] = "test-execution",
                ["ExecutionApi:InvocationTimeoutSeconds"] = invocationTimeoutSeconds.ToString()
            })
            .Build();

        // Real (not mocked) provider: it's cheap and lazy — building it does not open a
        // gRPC channel, and these tests never exercise the "grpc" transport branch.
        var grpcClientProvider = new GrpcTaskInvokerClientProvider(config);

        var invokeClient = new HttpClient(new InvocationHandler { InnerHandler = _stub, DaprEndpoint = Sidecar });

        return new RemoteInvokerService(
            new DaprServiceInvocationClient(invokeClient),
            config, _logger.Object, _correlationIdProvider.Object, grpcClientProvider);
    }

    private static TaskEnvelope CreateEnvelope() => new()
    {
        TaskType = "HttpTask",
        TaskKey = "call-api",
        Binding = JsonDocument.Parse("{}").RootElement
    };

    private static TaskTraceContext CreateTraceContext() => TaskTraceContext.Create(
        instanceId: Guid.NewGuid(),
        domain: "test",
        workflowKey: "test-flow",
        workflowVersion: "1.0.0");

    private static HttpResponseMessage SuccessResponse() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new TaskInvokeResponse
        {
            Success = true,
            Result = TaskInvocationResult.Success(statusCode: 200, taskType: "HttpTask")
        }, options: Web)
    };

    /// <summary>
    /// When only the per-invocation CTS fires (parent pipeline is fine),
    /// InvokeAsync should return a failure Result with HTTP 408 — not throw.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenInvocationTimeoutExpires_ReturnsFailureResult()
    {
        _stub.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return SuccessResponse();
        };

        var service = CreateService(invocationTimeoutSeconds: 0);

        var result = await service.InvokeAsync(
            "HttpTask", "call-api",
            CreateEnvelope(), CreateTraceContext(),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.StatusCode.ShouldBe(408);
        result.Value.ErrorMessage.ShouldNotBeNull();
        result.Value.ErrorMessage!.ShouldContain("timeout");
    }

    /// <summary>
    /// When the parent pipeline token is cancelled (not the invocation CTS),
    /// InvokeAsync must propagate OperationCanceledException to the pipeline.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenParentTokenCancelled_ThrowsOperationCanceledException()
    {
        _stub.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return SuccessResponse();
        };

        var service = CreateService(invocationTimeoutSeconds: 300);
        using var parentCts = new CancellationTokenSource();
        parentCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => service.InvokeAsync(
                "HttpTask", "call-api",
                CreateEnvelope(), CreateTraceContext(),
                parentCts.Token));
    }

    /// <summary>
    /// When the sidecar cannot be reached, InvokeAsync returns a failure Result with 500.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenSidecarUnreachable_ReturnsFailureResult()
    {
        _stub.Respond = (_, _) => throw new HttpRequestException("connection refused");

        var service = CreateService();

        var result = await service.InvokeAsync(
            "HttpTask", "call-api",
            CreateEnvelope(), CreateTraceContext(),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.StatusCode.ShouldBe(500);
    }

    /// <summary>
    /// A non-2xx from Execution is a transport-level failure of the invoke (the obsolete
    /// <c>InvokeMethodAsync&lt;T&gt;</c> threw on it too) and maps to the same 500 failure result.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenExecutionAnswersNonSuccess_ReturnsFailureResult()
    {
        _stub.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{\"errorCode\":\"ERR_DIRECT_INVOKE\"}")
        });

        var result = await CreateService().InvokeAsync(
            "HttpTask", "call-api", CreateEnvelope(), CreateTraceContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.StatusCode.ShouldBe(500);
    }

    /// <summary>
    /// On a successful response, InvokeAsync returns a mapped success result — and the request
    /// reached the sidecar's invoke API for the configured Execution app-id.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenExecutionRespondsSuccessfully_ReturnsSuccessResult()
    {
        _stub.Respond = (_, _) => Task.FromResult(SuccessResponse());

        var service = CreateService();

        var result = await service.InvokeAsync(
            "HttpTask", "call-api",
            CreateEnvelope(), CreateTraceContext(),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value.StatusCode.ShouldBe(200);

        // Real InvocationHandler rewrite: http://test-execution/... → sidecar invoke API.
        _stub.Requests.ShouldHaveSingleItem().RequestUri!.AbsoluteUri.ShouldBe(
            $"{Sidecar}/v1.0/invoke/test-execution/method/api/v1/execution/invoke/HttpTask/call-api");
        _stub.Requests[0].Method.ShouldBe(HttpMethod.Post);
    }

    /// <summary>
    /// Pins the wire-casing invariant found via <c>InstanceIncidents</c> evidence on a live
    /// runtime (2026-09-19): an HTTP-shaped remote task result whose <c>Metadata</c> arrives
    /// camel-cased over the wire — exactly how <c>System.Text.Json</c> with
    /// <c>JsonSerializerDefaults.Web</c> serializes it on the Execution host and deserializes it
    /// here — must still let <see cref="ErrorNormalizer"/> resolve a non-null
    /// <see cref="NormalizedError.ExceptionType"/> and <see cref="NormalizedError.OriginalCode"/>,
    /// exactly as it does on the in-process path where the same <c>Dictionary&lt;string,object&gt;</c>
    /// instance is never JSON round-tripped and keeps its original PascalCase keys
    /// (<c>"ExceptionType"</c>, built by <c>HttpTaskInvocation.SendAsync</c>). Before the fix,
    /// <see cref="RemoteInvokerService"/> passed the deserialized dictionary through unchanged, so
    /// <see cref="ErrorNormalizer"/>'s ordinal <c>TryGetValue("ExceptionType", ...)</c> missed the
    /// camelCased <c>"exceptionType"</c> key and every <c>errorTypes</c> error-boundary rule
    /// silently stopped matching the moment a task's invocation mode was Remote instead of Local.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenMetadataArrivesCamelCasedOverTheWire_ErrorNormalizerStillResolvesExceptionTypeAndOriginalCode()
    {
        // Hand-written JSON body: mirrors exactly what the Execution host's ASP.NET Core
        // controller (System.Text.Json, Web/camelCase defaults) puts on the wire for a
        // TaskCanceledException from a timed-out HTTP task — never a POCO round-tripped back
        // through the same options, which would hide the casing change this test exists to catch.
        const string wireJson = """
            {
              "success": true,
              "result": {
                "isSuccess": false,
                "statusCode": null,
                "body": null,
                "data": null,
                "errorMessage": "The operation was canceled.",
                "headers": null,
                "taskType": "http",
                "metadata": {
                  "url": "http://mocklab/slow",
                  "method": "POST",
                  "exceptionType": "TaskCanceledException",
                  "errorCode": "Task:Http:til-http-slow:408"
                },
                "executionDurationMs": 42
              },
              "errorMessage": null,
              "executionDurationMs": 42
            }
            """;

        _stub.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(wireJson, System.Text.Encoding.UTF8, "application/json")
        });

        var result = await CreateService().InvokeAsync(
            "http", "til-http-slow", CreateEnvelope(), CreateTraceContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var invocationResult = result.Value!;
        invocationResult.IsSuccess.ShouldBeFalse();

        // The dictionary itself must resolve a PascalCase lookup despite camelCase wire keys —
        // exactly the shape ErrorNormalizer queries.
        // Values are boxed System.Text.Json.JsonElement instances (Dictionary<string,object>
        // deserialization with no custom converter) — .ToString() is exactly what
        // ErrorNormalizer's own extraction does, so the comparison mirrors production.
        invocationResult.Metadata.ShouldNotBeNull();
        invocationResult.Metadata!.TryGetValue("ExceptionType", out var exceptionType).ShouldBeTrue();
        exceptionType!.ToString().ShouldBe("TaskCanceledException");
        invocationResult.Metadata!.TryGetValue("ErrorCode", out var originalCode).ShouldBeTrue();
        originalCode!.ToString().ShouldBe("Task:Http:til-http-slow:408");

        // And the consumer that actually drives error-boundary matching must resolve it too.
        // TaskExecutorBase builds StandardTaskResponse.Metadata straight from
        // TaskInvocationResult.Metadata (no re-copy, no re-casing) before handing it to
        // ErrorNormalizer.NormalizeTaskResponse — reproduced here rather than going through
        // ErrorNormalizer.NormalizeTaskResult, which binds (via this file's own namespace
        // resolution rules) to the unrelated, unused BBT.Workflow.Execution.TaskInvocationResult
        // overload and is not the type this dictionary flows through in production.
        var response = new StandardTaskResponse
        {
            IsSuccess = invocationResult.IsSuccess,
            StatusCode = invocationResult.StatusCode,
            ErrorMessage = invocationResult.ErrorMessage,
            Metadata = invocationResult.Metadata
        };
        var normalized = new ErrorNormalizer().NormalizeTaskResponse(response, "til-http-slow", "http");
        normalized.ExceptionType.ShouldBe("TaskCanceledException");
        normalized.OriginalCode.ShouldBe("Task:Http:til-http-slow:408");
    }

    [Fact]
    public async Task InvokeAsync_WithTraceContext_PropagatesCanonicalCorrelationHeaders()
    {
        _stub.Respond = (_, _) => Task.FromResult(SuccessResponse());

        var instanceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var traceContext = TaskTraceContext.Create(
            instanceId,
            "test",
            "test-flow",
            "1.0.0",
            correlationId.ToString("N"),
            sub: "12345678901",
            actSub: "U0B006");

        var result = await CreateService().InvokeAsync(
            "HttpTask", "call-api", CreateEnvelope(), traceContext, CancellationToken.None);

        result.Value!.ErrorMessage.ShouldBeNull();
        var capturedRequest = _stub.Requests.ShouldHaveSingleItem();
        capturedRequest.Headers.GetValues(TelemetryConstants.HeaderNames.WorkflowInstanceId)
            .Single().ShouldBe(instanceId.ToString("D").ToLowerInvariant());
        capturedRequest.Headers.GetValues(TelemetryConstants.HeaderNames.CorrelationId)
            .Single().ShouldBe(correlationId.ToString("N"));
        capturedRequest.Headers.GetValues(TelemetryConstants.HeaderNames.Sub)
            .Single().ShouldBe("12345678901");
        capturedRequest.Headers.GetValues(TelemetryConstants.HeaderNames.ActSub)
            .Single().ShouldBe("U0B006");
    }

    [Fact]
    public void CreateTraceContext_WithBusinessCorrelationBaggage_CopiesCorrelationId()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "instance-key");
        var workflow = BBT.Workflow.Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test", "sys-flows", "1.0.0"));
        using var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance)
            .SetWorkflow(workflow)
            .SetHeaders(new Dictionary<string, string>
            {
                ["sub"] = "12345678901",
                ["act_sub"] = "U0B006"
            })
            .Build();
        var correlationId = Guid.NewGuid().ToString("N");

        using var activity = new Activity("create-trace-context-test").Start();
        activity.SetBaggage(TelemetryConstants.TagNames.CorrelationId, correlationId);

        var result = CreateService().CreateTraceContext(scriptContext);

        result.InstanceId.ShouldBe(instance.Id);
        result.CorrelationId.ShouldBe(correlationId);
        result.Sub.ShouldBe("12345678901");
        result.ActSub.ShouldBe("U0B006");
    }

    [Fact]
    public void CreateTraceContext_WithoutBusinessCorrelationBaggage_UsesCurrentTraceId()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "instance-key");
        var workflow = BBT.Workflow.Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test", "sys-flows", "1.0.0"));
        using var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance)
            .SetWorkflow(workflow)
            .Build();

        using var activity = new Activity("create-trace-context-fallback-test").Start();

        var result = CreateService().CreateTraceContext(scriptContext);

        result.InstanceId.ShouldBe(instance.Id);
        result.CorrelationId.ShouldBe(activity.TraceId.ToString());
    }

    /// <summary>
    /// Records what reached the wire. The URI is snapshotted because <see cref="InvocationHandler"/>
    /// restores the original <c>http://{appId}/…</c> in its <c>finally</c>.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var snapshot = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) snapshot.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(snapshot);
            return Respond(request, ct);
        }
    }
}
