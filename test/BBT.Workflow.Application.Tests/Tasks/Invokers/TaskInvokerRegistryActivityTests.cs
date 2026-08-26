using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invokers;

/// <summary>
/// Pins the Execution-side invocation span: every <see cref="TaskInvokerRegistry.InvokeAsync"/>
/// call opens <c>Task.Invoke.{type}/{key}</c>, and the span's status carries the INVOCATION's own
/// verdict — a business failure (IsSuccess=false, e.g. an HTTP 5xx surfaced by HttpTaskInvoker), a
/// missing invoker, and an escaped exception must all be red, because before this span existed the
/// only Execution-side span was the green server span and failed tasks were invisible in traces.
/// </summary>
public sealed class TaskInvokerRegistryActivityTests : IDisposable
{
    /// <summary>
    /// Name of the Execution-side ActivitySource, duplicated as a literal: reading the helper's
    /// static field from inside the listener predicate would run its static initializer
    /// re-entrantly under the listener lock (see FanOutTraceCapture for the full story).
    /// </summary>
    private const string ExecutionSourceName = "BBT.Workflow.Execution";

    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<Activity> _stopped = new();
    private readonly Activity _root;

    public TaskInvokerRegistryActivityTests()
    {
        _root = new Activity("invoker-activity-tests");
        _root.SetIdFormat(ActivityIdFormat.W3C);
        _root.Start();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ExecutionSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _root.Stop();
        _root.Dispose();
        Activity.Current = null;
    }

    [Fact]
    public async Task ASuccessfulInvocation_ProducesAnOkSpan_NamedByTypeAndKey()
    {
        var registry = Registry(new StubInvoker(_ => TaskInvocationResult.Success(taskType: "http")));

        await registry.InvokeAsync(Envelope("http", "send-mail"));

        var span = InvokeSpans().ShouldHaveSingleItem();
        span.OperationName.ShouldBe("Task.Invoke.http/send-mail");
        span.Status.ShouldBe(ActivityStatusCode.Ok);
        span.GetTagItem("vnext.task.key").ShouldBe("send-mail");
        span.GetTagItem("vnext.task.type").ShouldBe("http");
        span.GetTagItem("vnext.layer").ShouldBe("execution");
        span.GetTagItem("vnext.span.category").ShouldBe("business");
    }

    [Fact]
    public async Task ABusinessFailure_MakesTheSpanRed_WithTheStatusCodeAttached()
    {
        // The invoker itself returns a Result — an HTTP 5xx is a normal return value here, not an
        // exception, and used to leave every span green while the task failed.
        var registry = Registry(new StubInvoker(_ =>
            TaskInvocationResult.Failure("upstream exploded", statusCode: 502)));

        var result = await registry.InvokeAsync(Envelope("http", "send-mail"));

        result.IsSuccess.ShouldBeFalse();
        var span = InvokeSpans().ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe("upstream exploded");
        span.GetTagItem("error.code").ShouldBe(502);
    }

    [Fact]
    public async Task AMissingInvoker_MakesTheSpanRed_WithInvokerNotFound()
    {
        var registry = Registry(new StubInvoker(_ => TaskInvocationResult.Success()));

        var result = await registry.InvokeAsync(Envelope("soap", "legacy-call"));

        result.IsSuccess.ShouldBeFalse();
        var span = InvokeSpans().ShouldHaveSingleItem();
        span.OperationName.ShouldBe("Task.Invoke.soap/legacy-call");
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("error.type").ShouldBe("InvokerNotFound");
    }

    [Fact]
    public async Task AnEscapedException_MakesTheSpanRed_AndRecordsAnExceptionEvent()
    {
        var registry = Registry(new StubInvoker(_ => throw new InvalidOperationException("boom")));

        var result = await registry.InvokeAsync(Envelope("http", "send-mail"));

        // The registry converts the exception into a failure result — the span must still show it.
        result.IsSuccess.ShouldBeFalse();
        var span = InvokeSpans().ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("error.type").ShouldBe(nameof(InvalidOperationException));
        span.Events.ShouldContain(e => e.Name == "exception");
    }

    private static TaskInvokerRegistry Registry(params ITaskInvoker[] invokers) =>
        new(invokers, NullLogger<TaskInvokerRegistry>.Instance);

    private static TaskEnvelope Envelope(string taskType, string taskKey) => new()
    {
        TaskType = taskType,
        TaskKey = taskKey,
        Binding = JsonDocument.Parse("{}").RootElement
    };

    private System.Collections.Generic.List<Activity> InvokeSpans() => _stopped
        .Where(activity => activity.OperationName.StartsWith("Task.Invoke.", StringComparison.Ordinal)
                           && activity.TraceId == _root.TraceId)
        .ToList();

    private sealed class StubInvoker(Func<string?, TaskInvocationResult> behavior) : ITaskInvoker
    {
        public string TaskType => "http";

        public Type BindingType => typeof(object);

        public Task<TaskInvocationResult> InvokeAsync(
            string? taskKey,
            JsonElement binding,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(behavior(taskKey));
    }
}
