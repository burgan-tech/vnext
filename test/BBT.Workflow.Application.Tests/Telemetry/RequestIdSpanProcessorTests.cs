using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BBT.Aether.Tracing;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using BBT.Workflow.Logging;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the span-side half of request-id correlation: a trace must be filterable by the same
/// x_request_id value as the logs, without the processor ever overwriting a tag a caller set
/// deliberately (ExecutionController stamps the Execution invoke span itself).
/// </summary>
public sealed class RequestIdSpanProcessorTests
{
    private const string SourceName = "BBT.Workflow.Tests.RequestIdSpanProcessor";

    /// <summary>
    /// Runs the processor exactly as the SDK does — through a real tracer provider — and collects
    /// the finished activities so the assertions see the tags a span was exported with.
    /// </summary>
    private static (List<Activity> Exported, TracerProvider Provider, ActivitySource Source) CreateHarness(
        string? providerValue)
    {
        var correlationIdProvider = new Mock<ICorrelationIdProvider>();
        correlationIdProvider.Setup(p => p.Get()).Returns(providerValue);

        var exported = new List<Activity>();
        var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(SourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new RequestIdSpanProcessor(correlationIdProvider.Object))
            .AddProcessor(new CapturingProcessor(exported))
            .Build()!;

        return (exported, provider, new ActivitySource(SourceName));
    }

    private sealed class CapturingProcessor(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Add(data);
    }

    [Fact]
    public void WhenProviderHasRequestId_StampsItOnTheSpan()
    {
        var (exported, provider, source) = CreateHarness("req-abc-123");
        using (provider)
        using (source)
        {
            using (source.StartActivity("work"))
            {
            }

            provider.ForceFlush();
        }

        exported.ShouldHaveSingleItem();
        exported[0].GetTagItem(TelemetryConstants.TagNames.RequestId).ShouldBe("req-abc-123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WhenProviderHasNoRequestId_SpanIsUntouched(string? providerValue)
    {
        var (exported, provider, source) = CreateHarness(providerValue);
        using (provider)
        using (source)
        {
            using (source.StartActivity("work"))
            {
            }

            provider.ForceFlush();
        }

        exported.ShouldHaveSingleItem();
        exported[0].GetTagItem(TelemetryConstants.TagNames.RequestId).ShouldBeNull();
        exported[0].TagObjects.ShouldNotContain(t => t.Key == TelemetryConstants.TagNames.RequestId);
    }

    [Fact]
    public void WhenSpanAlreadyCarriesRequestId_TheExistingValueWins()
    {
        // ExecutionController tags the invoke span before the pipeline runs; that value is the
        // authoritative one for that span and must survive.
        var (exported, provider, source) = CreateHarness("from-provider");
        using (provider)
        using (source)
        {
            using (source.StartActivity(
                       "work",
                       ActivityKind.Server,
                       parentContext: default,
                       tags: new[]
                       {
                           new KeyValuePair<string, object?>(
                               TelemetryConstants.TagNames.RequestId, "from-caller")
                       }))
            {
            }

            provider.ForceFlush();
        }

        exported.ShouldHaveSingleItem();
        exported[0].TagObjects
            .Count(t => t.Key == TelemetryConstants.TagNames.RequestId)
            .ShouldBe(1);
        exported[0].GetTagItem(TelemetryConstants.TagNames.RequestId).ShouldBe("from-caller");
    }
}
