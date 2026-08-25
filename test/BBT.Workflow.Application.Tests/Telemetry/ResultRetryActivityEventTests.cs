using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Application.Resilience;
using BBT.Workflow.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins that every Polly retry performed by <see cref="ResultResiliencePipelineFactory"/> pipelines
/// marks the active span with a <c>result.retry</c> event. Without it, the retry delay is an
/// unexplained hole in the trace timeline — the "dead wait" a DirectTrigger shows while spinning
/// on a Busy target instance.
/// </summary>
public sealed class ResultRetryActivityEventTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Tests.ResultRetryEvents";

    private readonly ActivitySource _source = new(SourceName);
    private readonly ActivityListener _listener;

    public ResultRetryActivityEventTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
        Activity.Current = null;
    }

    private static ResultResiliencePipelineFactory CreateFactory(int maxAttempts) => new(
        Options.Create(new ResultRetryOptions
        {
            MaxRetryAttempts = maxAttempts,
            RetryDelayMilliseconds = 1,
            UseJitter = false,
            RetryOnErrorCodes = ["Instance:100031"]
        }),
        NullLogger<ResultResiliencePipelineFactory>.Instance);

    [Fact]
    public async Task Retry_AddsResultRetryEvent_ToActiveSpan()
    {
        using var span = _source.StartActivity("Task.Execute.notify-parent-workflow", ActivityKind.Internal);
        span.ShouldNotBeNull();

        var pipeline = CreateFactory(maxAttempts: 2).CreatePipeline<string>("DirectTrigger.ExecuteLocal");

        var attempts = 0;
        var result = await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            return ValueTask.FromResult(attempts < 3
                ? Result<string>.Fail(Error.Failure("Instance:100031", "Instance is busy"))
                : Result<string>.Ok("done"));
        });

        result.IsSuccess.ShouldBeTrue();
        attempts.ShouldBe(3);

        var retryEvents = span!.Events.Where(e => e.Name == "result.retry").ToList();
        retryEvents.Count.ShouldBe(2);
        retryEvents[0].Tags.Single(t => t.Key == "retry.attempt").Value.ShouldBe(0);
        retryEvents[0].Tags.Single(t => t.Key == "error.code").Value.ShouldBe("Instance:100031");
        retryEvents[0].Tags.Single(t => t.Key == "operation").Value.ShouldBe("DirectTrigger.ExecuteLocal");
    }

    [Fact]
    public async Task NonRetriableError_ProducesNoRetryEvent()
    {
        using var span = _source.StartActivity("Task.Execute.some-task", ActivityKind.Internal);
        span.ShouldNotBeNull();

        var pipeline = CreateFactory(maxAttempts: 3).CreatePipeline<string>("op");

        var result = await pipeline.ExecuteAsync(_ =>
            ValueTask.FromResult(Result<string>.Fail(Error.Validation("Some:Other", "nope"))));

        result.IsSuccess.ShouldBeFalse();
        span!.Events.Any(e => e.Name == "result.retry").ShouldBeFalse();
    }
}
