using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Tasks.Executors;
using TaskEnvelope = BBT.Workflow.Tasks.TaskEnvelope;
using TaskTraceContext = BBT.Workflow.Tasks.TaskTraceContext;
using TaskInvokeResponse = BBT.Workflow.Execution.TaskInvokeResponse;
using TaskInvocationResult = BBT.Workflow.Execution.TaskInvocationResult;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Tests the per-invocation Dapr timeout and parent-cancellation propagation in
/// <see cref="RemoteInvokerService"/>.
/// </summary>
public class RemoteInvokerServiceTests
{
    private readonly Mock<DaprClient> _daprClient = new();
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

        return new RemoteInvokerService(_daprClient.Object, config, _logger.Object);
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
        workflowVersion: "1.0.0",
        headers: null,
        instanceDataJson: null);

    private void SetupDaprCreateRequest()
    {
        // CreateInvokeMethodRequest(HttpMethod, string, string) is the abstract base.
        // The concrete generic overload delegates to it, then attaches the serialized body.
        _daprClient
            .Setup(d => d.CreateInvokeMethodRequest(
                HttpMethod.Post,
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(new HttpRequestMessage());
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
}
