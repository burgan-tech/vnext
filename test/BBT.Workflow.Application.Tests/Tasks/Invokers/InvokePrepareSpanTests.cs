using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invokers;

/// <summary>
/// Pins Invoke.Prepare: the gap between Invoke.{type}/{key} and the outbound client span
/// (binding deserialize + client create + header/URL/body build) gets its own always-on span.
/// HttpTaskInvoker is the representative; the same helper call is applied to every invoker.
/// </summary>
[Collection("TracingDetailLevel")]
public sealed class InvokePrepareSpanTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Execution.Invokers"; // literal — trap

    private readonly ActivityListener _listener;
    private readonly List<Activity> _started = [];

    public InvokePrepareSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { lock (_started) _started.Add(a); }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() { _listener.Dispose(); Activity.Current = null; }

    [Fact]
    public async Task HttpTaskInvoker_emits_InvokePrepare_before_send()
    {
        // Arrange helpers lifted from HttpTaskInvokerContentTypeTests: stub HttpMessageHandler +
        // IHttpClientFactory substitute + a minimal HttpTaskBinding descriptor for a simple GET.
        var handler = new CapturingHttpMessageHandler();
        var invoker = new HttpTaskInvoker(new FakeHttpClientFactory(handler), NullLogger<HttpTaskInvoker>.Instance);
        var taskKey = "http-task";
        var descriptor = new TaskDescriptor<HttpTaskBinding>
        {
            TaskType = TaskTypes.Http,
            TaskKey = taskKey,
            Binding = new HttpTaskBinding
            {
                Url = "https://workflow.local/endpoint",
                Method = "GET"
            }
        };

        var result = await invoker.InvokeAsync(descriptor, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        lock (_started)
        {
            _started.ShouldContain(a => a.OperationName == "Invoke.Prepare");
            var prep = _started.First(a => a.OperationName == "Invoke.Prepare");
            prep.GetTagItem("vnext.task.key").ShouldBe(taskKey);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
