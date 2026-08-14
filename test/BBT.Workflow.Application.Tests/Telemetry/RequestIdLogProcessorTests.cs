using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Tracing;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the global request-id stamping: every log record must carry x_request_id from
/// ICorrelationIdProvider — including where no HttpContext exists — without ever duplicating a
/// value a scope or log parameter already supplied.
/// </summary>
public sealed class RequestIdLogProcessorTests
{
    /// <summary>
    /// Runs after <see cref="RequestIdLogProcessor"/> and snapshots the attributes. LogRecord
    /// instances are pooled and reused by OpenTelemetry, so the values must be copied out here
    /// rather than holding on to the record.
    /// </summary>
    private sealed class CapturingProcessor(List<List<KeyValuePair<string, object?>>> sink)
        : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord record) =>
            sink.Add(record.Attributes?.ToList() ?? []);
    }

    private static (List<List<KeyValuePair<string, object?>>> Captured, ILoggerFactory Factory) CreateHarness(
        string? providerValue)
    {
        var provider = new Mock<ICorrelationIdProvider>();
        provider.Setup(p => p.Get()).Returns(providerValue);

        var captured = new List<List<KeyValuePair<string, object?>>>();
        var factory = LoggerFactory.Create(builder =>
            builder.AddOpenTelemetry(options =>
            {
                options.AddProcessor(new RequestIdLogProcessor(provider.Object));
                options.AddProcessor(new CapturingProcessor(captured));
            }));
        return (captured, factory);
    }

    [Fact]
    public void FieldName_IsTheNormalizedHeaderName()
    {
        // Pinned: dashboards and saved queries filter on this literal. It must stay dot-free so
        // backends that flatten dotted keys do not rewrite it, and it must match the normalized
        // X-Request-Id header name. Renaming this is a breaking change for every saved query.
        TelemetryConstants.TagNames.RequestId.ShouldBe("x_request_id");
    }

    [Fact]
    public void WhenProviderHasRequestId_StampsItOnTheRecord()
    {
        var (captured, factory) = CreateHarness("req-abc-123");

        factory.CreateLogger("test").LogInformation("hello");
        factory.Dispose();

        captured.ShouldHaveSingleItem();
        captured[0].Single(a => a.Key == TelemetryConstants.TagNames.RequestId)
            .Value.ShouldBe("req-abc-123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WhenProviderHasNoRequestId_RecordIsUntouched(string? providerValue)
    {
        var (captured, factory) = CreateHarness(providerValue);

        factory.CreateLogger("test").LogInformation("hello");
        factory.Dispose();

        captured.ShouldHaveSingleItem();
        captured[0].ShouldNotContain(a => a.Key == TelemetryConstants.TagNames.RequestId);
    }

    [Fact]
    public void WhenRecordAlreadyCarriesRequestId_DoesNotDuplicate()
    {
        var (captured, factory) = CreateHarness("from-provider");

        // A structured log parameter named like the tag lands in record.Attributes.
        factory.CreateLogger("test").LogInformation(
            "hello {" + TelemetryConstants.TagNames.RequestId + "}", "from-log-parameter");
        factory.Dispose();

        captured.ShouldHaveSingleItem();
        var matches = captured[0]
            .Where(a => a.Key == TelemetryConstants.TagNames.RequestId)
            .ToList();
        matches.Count.ShouldBe(1);
        matches[0].Value.ShouldBe("from-log-parameter");
    }
}
