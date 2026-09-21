using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.StateStores;
using BBT.Workflow.Tasks.Invocation.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

public sealed class LocalStateStoreTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_GetOnAHit_ReturnsTheStoredValue()
    {
        var store = new FakeStateStoreClient("statestore");
        store.Seed("cfg:1", JsonSerializer.SerializeToElement(new { limit = 5 }));
        var invoker = new LocalStateStoreTaskInvoker(store, NullLogger<LocalStateStoreTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("cfg-read", GetBinding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        ((JsonElement)result.Data!).GetProperty("limit").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task InvokeAsync_NoStoreNameConfigured_Fails()
    {
        var invoker = new LocalStateStoreTaskInvoker(
            new FakeStateStoreClient(defaultStoreName: null), NullLogger<LocalStateStoreTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("cfg-read", GetBinding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// M4: a returned validation failure (no store name configured — no exception was thrown) must
    /// not log at Error, matching the Execution host's own <c>StateStoreTaskInvoker</c> — that
    /// invoker is metered, not logged, when the failure carries no <c>ExceptionType</c> metadata.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NoStoreNameConfigured_DoesNotLogAtError()
    {
        var logger = new CapturingLogger();
        var invoker = new LocalStateStoreTaskInvoker(
            new FakeStateStoreClient(defaultStoreName: null), logger);

        var result = await invoker.InvokeAsync("cfg-read", GetBinding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Error);
    }

    /// <summary>
    /// The counterpart: a GENUINE thrown exception (the shared core's catch-all, stamped with
    /// <c>ExceptionType</c> metadata) must still log at Error — the gate narrows, it does not
    /// silence every failure.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_StateStoreThrows_LogsAtError()
    {
        var logger = new CapturingLogger();
        var invoker = new LocalStateStoreTaskInvoker(
            new FakeStateStoreClient("statestore") { ThrowOnGet = new InvalidOperationException("redis down") },
            logger);

        var result = await invoker.InvokeAsync("cfg-read", GetBinding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
        logger.Entries.Count(e => e.EventId.Id == 10161 && e.Level == LogLevel.Error).ShouldBe(1);
    }

    private static JsonElement GetBinding(string key) =>
        JsonSerializer.SerializeToElement(new StateStoreBinding { Command = "get", Key = key });

    private sealed class CapturingLogger : ILogger<LocalStateStoreTaskInvoker>
    {
        public List<(LogLevel Level, EventId EventId)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId));
    }
}
