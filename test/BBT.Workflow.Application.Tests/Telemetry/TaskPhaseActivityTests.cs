using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks.Coordinator;
using Xunit;

// The detail level this class relies on (Business, the default) is process-global; see
// TracingDetailLevelCollection.

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the always-on creation rule for task phase spans (Task 4): PrepareInput / Invoke /
/// ProcessOutput are business-level and created unconditionally, no longer gated behind verbose
/// tracing. They become visible under the existing <c>Task.Execute.{key}</c> span created by
/// Aether's <c>[Trace]</c> aspect on <c>TaskExecutionEngine.ExecuteAsync</c>.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class TaskPhaseActivityTests : IDisposable
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
            // The listener is process-wide: spans of tests running in parallel arrive here too, on their threads.
            ActivityStopped = activity =>
            {
                lock (collected)
                {
                    collected.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return listener;
    }

    [Fact]
    public void StartActivity_InBusinessMode_CreatesPhaseSpan()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Tasks", collected);

        ActivitySpanId spanId;
        using (var activity = TaskExecutionActivityHelper.StartActivity(
                   TaskExecutionActivityHelper.OperationPrepareInput, "my-task", "Http"))
        {
            Assert.NotNull(activity);
            spanId = activity.SpanId;
        }

        Activity[] mine;
        lock (collected)
        {
            mine = collected.Where(a => a.SpanId == spanId).ToArray();
        }

        var span = Assert.Single(mine);
        Assert.Equal("Task.PrepareInput", span.DisplayName);
        Assert.Equal("my-task", span.GetTagItem(TelemetryConstants.TagNames.TaskKey));
        Assert.Equal(TelemetryConstants.SpanCategories.Business,
            span.GetTagItem(TelemetryConstants.TagNames.SpanCategory));
    }
}
