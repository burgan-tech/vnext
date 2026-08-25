using System;
using System.Collections.Generic;
using System.Diagnostics;
using BBT.Workflow.Data;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// Pins the <c>Instance.AppendData</c> span emitted by
/// <see cref="InstanceDataWriteService.StartAppendActivity"/> around each instance-data append
/// (<see cref="InstanceDataWriteService.AppendAsync"/> / <see cref="InstanceDataWriteService.AppendExplicitAsync"/>).
/// The service itself talks raw Npgsql (DbContext, transactions, row locks) and is disproportionate
/// to construct for a telemetry-wiring test, so this pins the extracted helper directly — the
/// service methods just call it at the point version/size are known.
/// </summary>
public sealed class InstanceDataWriteServiceActivityTests : IDisposable
{
    private readonly List<ActivityListener> _listeners = new();

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        Activity.Current = null;
    }

    private ActivityListener CreateListener(string sourceName, List<Activity> collected)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return listener;
    }

    [Fact]
    public void StartAppendActivity_CarriesVersionAndSize()
    {
        // Unique version per test run: the BBT.Workflow.Pipeline ActivitySource is process-wide
        // and xUnit runs test classes in parallel, so a listener here can observe spans from
        // other concurrently running tests on the same source.
        var version = $"1.2.3-{Guid.NewGuid():N}";
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        using (InstanceDataWriteService.StartAppendActivity(version, 2048))
        {
        }

        var span = Assert.Single(collected, a => a.DisplayName == "Instance.AppendData" &&
            Equals(a.GetTagItem(TelemetryConstants.TagNames.DataVersion), version));

        span.GetTagItem(TelemetryConstants.TagNames.DataVersion).ShouldBe(version);
        span.GetTagItem(TelemetryConstants.TagNames.DataSizeBytes).ShouldBe(2048L);
    }
}
