using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Invocation.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The in-process HTTP invoker must produce exactly what the Execution service's HttpTaskInvoker
/// produces for the same binding: same status/body/headers/metadata, because output mapping
/// scripts read those shapes and must not care which host made the call.
/// </summary>
public sealed class LocalHttpTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_SuccessfulJsonResponse_ReturnsParsedResult()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"orderId": 42}""")
        });
        var invoker = new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(handler), NullLogger<LocalHttpTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("order-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);
        ((JsonElement)result.Data!).GetProperty("orderId").GetInt32().ShouldBe(42);
        result.TaskType.ShouldBe(TaskTypes.Http);
        result.Metadata!["Url"].ShouldBe("https://workflow.local/endpoint");
    }

    [Fact]
    public async Task TaskType_IsTheWireHttpConstant()
    {
        var invoker = new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(new StubHttpMessageHandler(new HttpResponseMessage())),
            NullLogger<LocalHttpTaskInvoker>.Instance);

        invoker.TaskType.ShouldBe(TaskTypes.Http);
    }

    [Fact]
    public async Task InvokeAsync_TransportFailure_ReturnsFailedResultNotException()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var invoker = new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(handler), NullLogger<LocalHttpTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("order-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBeNull();
        result.ErrorMessage.ShouldBe("connection refused");
    }

    [Fact]
    public async Task InvokeAsync_Type6Failure_LogsTheGenericLocalEventNotTheExternalHttpOne()
    {
        // F3: a plain type-6 HTTP task must log under the generic 1016x local-invocation events
        // (10161/10162/10163), not the deprecated type-22 ExternalHttp events (10098/10099/10108) —
        // an operator alerting on 10161 must see this failure, and the message must not call a
        // type-6 task "external HTTP".
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var logger = new CapturingLogger();
        var invoker = new LocalHttpTaskInvoker(new CapturingHttpClientFactory(handler), logger);

        var result = await invoker.InvokeAsync("order-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
        logger.EventIds.ShouldContain(10161); // LocalTaskInvocationFailed
        logger.EventIds.ShouldNotContain(10098); // ExternalHttpTaskRequestFailed
        logger.EventIds.ShouldNotContain(10099); // ExternalHttpTaskRequestCancelled
        logger.EventIds.ShouldNotContain(10108); // ExternalHttpTaskSslValidationDisabled
    }

    [Fact]
    public async Task InvokeAsync_ExternalHttpLabelFailure_KeepsLoggingTheShippedExternalHttpEvent()
    {
        // The type-22 wrapper (ExternalHttpTaskInvoker) passes TaskType.ExternalHttp.ToString() as
        // taskTypeLabel — that shipped, already-alerted-on path must be completely unchanged.
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var logger = new CapturingLogger();
        var invoker = new LocalHttpTaskInvoker(new CapturingHttpClientFactory(handler), logger);

        var result = await invoker.InvokeAsync(
            "order-call", Binding().Deserialize<HttpTaskBinding>()!, traceContext: null, "ExternalHttp");

        result.IsSuccess.ShouldBeFalse();
        logger.EventIds.ShouldContain(10098); // ExternalHttpTaskRequestFailed
        logger.EventIds.ShouldNotContain(10161); // LocalTaskInvocationFailed
    }

    private static JsonElement Binding() => JsonSerializer.SerializeToElement(new HttpTaskBinding
    {
        Url = "https://workflow.local/endpoint",
        Method = "GET",
        TimeoutSeconds = 30,
        ValidateSSL = true
    });

    private sealed class CapturingLogger : ILogger<LocalHttpTaskInvoker>
    {
        private readonly List<int> _eventIds = [];

        public IReadOnlyList<int> EventIds => _eventIds;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _eventIds.Add(eventId.Id);
    }
}
