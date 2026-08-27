using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Definitions;
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
public class RemoteInvokerServiceTests
{
    private readonly Mock<DaprClient> _daprClient = new();
    private readonly Mock<ICorrelationIdProvider> _correlationIdProvider = new();
    private readonly Mock<ILogger<RemoteInvokerService>> _logger = new();

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

        return new RemoteInvokerService(
            _daprClient.Object, config, _logger.Object, _correlationIdProvider.Object, grpcClientProvider);
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

    private void SetupDaprCreateRequest()
    {
        // CreateInvokeMethodRequest(HttpMethod, string, string) is the mockable base overload.
        // RemoteInvokerService attaches the serialized body after creating the request.
        _daprClient
            .Setup(d => d.CreateInvokeMethodRequest(
                It.IsAny<HttpMethod>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(new HttpRequestMessage(HttpMethod.Post, "http://test-execution/invoke"));
    }

    /// <summary>
    /// When only the per-invocation CTS fires (parent pipeline is fine),
    /// InvokeAsync should return a failure Result with HTTP 408 — not throw.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenInvocationTimeoutExpires_ReturnsFailureResult()
    {
        SetupDaprCreateRequest();

        _daprClient
            .Setup(d => d.InvokeMethodAsync<TaskInvokeResponse>(
                It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Returns<HttpRequestMessage, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new TaskInvokeResponse();
            });

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
        SetupDaprCreateRequest();

        _daprClient
            .Setup(d => d.InvokeMethodAsync<TaskInvokeResponse>(
                It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Returns<HttpRequestMessage, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new TaskInvokeResponse();
            });

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
    /// When Dapr throws an unexpected exception, InvokeAsync returns a failure Result with 500.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenDaprThrowsUnexpectedException_ReturnsFailureResult()
    {
        SetupDaprCreateRequest();

        _daprClient
            .Setup(d => d.InvokeMethodAsync<TaskInvokeResponse>(
                It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

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
    /// On a successful Dapr response, InvokeAsync returns a mapped success result.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenDaprRespondsSuccessfully_ReturnsSuccessResult()
    {
        SetupDaprCreateRequest();

        _daprClient
            .Setup(d => d.InvokeMethodAsync<TaskInvokeResponse>(
                It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskInvokeResponse
            {
                Success = true,
                Result = TaskInvocationResult.Success(statusCode: 200, taskType: "HttpTask")
            });

        var service = CreateService();

        var result = await service.InvokeAsync(
            "HttpTask", "call-api",
            CreateEnvelope(), CreateTraceContext(),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task InvokeAsync_WithTraceContext_PropagatesCanonicalCorrelationHeaders()
    {
        SetupDaprCreateRequest();
        HttpRequestMessage? capturedRequest = null;
        _daprClient
            .Setup(d => d.InvokeMethodAsync<TaskInvokeResponse>(
                It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new TaskInvokeResponse
            {
                Success = true,
                Result = TaskInvocationResult.Success(statusCode: 200, taskType: "HttpTask")
            });

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
        capturedRequest.ShouldNotBeNull();
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
}
