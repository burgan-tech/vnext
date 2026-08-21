using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Tasks.Executors;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// The observability CONTRACT of <see cref="FanOutTaskExecutor"/>: one batch recording per batch,
/// carrying counters that agree with the batch's own output.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Metrics only — logging is deliberately not asserted here.</strong> Every log line the
/// executor emits goes through a source-generated <c>WorkflowLogs</c> extension, and no test in
/// this repository asserts on logging at all: there is no fake-logger harness, no
/// <c>Microsoft.Extensions.Diagnostics.Testing</c> reference, and the only way to reach the calls
/// would be to assert on <c>ILogger.Log</c> with a hand-rolled state reader. Standing up a logging
/// assertion framework for one executor would make this suite the odd one out and pin the
/// plumbing rather than the behaviour, so the failure PATH is covered here through the metric it
/// also drives (<c>failed</c>), and the log lines themselves are left unasserted.
/// </para>
/// <para>
/// Item spans are likewise not asserted: <c>TaskExecutionActivityHelper</c> only starts an
/// activity when the host's tracing runtime is in verbose mode and an OpenTelemetry listener is
/// attached, so a unit test would be asserting on ambient host configuration rather than on this
/// executor.
/// </para>
/// </remarks>
public sealed class FanOutTaskExecutorObservabilityTests
{
    [Fact]
    public async Task ASettledBatch_RecordsExactlyOneBatchMetric_WithTheBatchsOwnCounters()
    {
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(5));

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();

        harness.Metrics.Received(1).RecordFanOutBatch(
            "fan-out-docs",
            Arg.Any<string>(),
            5,
            5,
            0,
            Arg.Any<double>());
    }

    [Fact]
    public async Task AFailedItem_IsCountedInTheBatchRecordings_FailedTally()
    {
        // allSettled: two of five items fail, and the batch still succeeds — so the failure count
        // is only visible through the recording, which is exactly why it is recorded.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(5),
            joinPolicy: FanOutJoinPolicy.AllSettled);
        harness.Engine.FailOrders.Add(1);
        harness.Engine.ThrowOrders.Add(3);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();

        harness.Metrics.Received(1).RecordFanOutBatch(
            "fan-out-docs",
            Arg.Any<string>(),
            5,
            3,
            2,
            Arg.Any<double>());
    }

    [Fact]
    public async Task AFailedJoin_StillRecordsTheBatch_BecauseTheWorkStillRan()
    {
        // 'all' fails the batch on the first failed item. The recording must not be tied to the
        // task's verdict: a batch that failed is precisely the one an operator goes looking for.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(3),
            joinPolicy: FanOutJoinPolicy.All,
            maxDop: 1);
        harness.Engine.FailOrders.Add(0);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();

        harness.Metrics.Received(1).RecordFanOutBatch(
            "fan-out-docs",
            Arg.Any<string>(),
            3,
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<double>());
    }

    [Fact]
    public async Task AnOutputHandlerFailure_DoesNotSuppressTheBatchRecording()
    {
        // The recording is taken as soon as the batch settles, before the author's output handler
        // gets a chance to throw. A batch whose items all ran must be counted either way.
        var mapping = new StubFanOutMapping
        {
            OutputHandlerThrows = new InvalidOperationException("output handler blew up")
        };
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(2), mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();

        harness.Metrics.Received(1).RecordFanOutBatch(
            "fan-out-docs", Arg.Any<string>(), 2, 2, 0, Arg.Any<double>());
    }

    [Fact]
    public async Task AConfigurationFailure_RecordsNothing_BecauseNoBatchEverRan()
    {
        // No item source configured: the executor fails before dispatching anything. Recording a
        // zero-sized batch here would put a phantom series in the batch-size histogram.
        var harness = new FanOutHarness(itemsPath: null);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();

        harness.Metrics.DidNotReceiveWithAnyArgs().RecordFanOutBatch(
            default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task AnEmptyBatch_IsStillRecorded_WithZeroCounters()
    {
        // An empty collection is a real batch outcome, not a no-op: it is the shape that quietly
        // fails a quorum/firstSuccess join, and an operator needs to see that it was empty.
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(0));

        await harness.ExecuteAsync();

        harness.Metrics.Received(1).RecordFanOutBatch(
            "fan-out-docs", Arg.Any<string>(), 0, 0, 0, Arg.Any<double>());
    }

    [Fact]
    public void TheBulkhead_ReportsItsConfiguredCapacity_AlongsideItsActiveCount()
    {
        // Capacity is the denominator the saturation warning is read against; without it "Active=8"
        // says nothing about whether the bulkhead is the bottleneck.
        var limiter = new FanOutConcurrencyLimiter(
            Microsoft.Extensions.Options.Options.Create(new FanOutOptions { MaxConcurrentItems = 3 }));

        limiter.Capacity.ShouldBe(3);
        limiter.ActiveCount.ShouldBe(0);
    }
}
