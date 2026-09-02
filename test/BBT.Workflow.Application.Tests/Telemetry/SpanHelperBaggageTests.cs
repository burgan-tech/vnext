using System;
using System.Diagnostics;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Scripting;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Tasks.Coordinator;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the implicit-parent contract for span helpers: a span started under an ambient activity
/// must keep the Activity CHAIN intact (Parent != null) so baggage is inherited. The explicit
/// parentContext overload sets ParentSpanId but leaves Parent null, silently severing baggage —
/// the defect documented in InstanceReadActivityHelper and the read-path-trace-gap work.
/// </summary>
[Collection("TracingDetailLevel")]
public sealed class SpanHelperBaggageTests : IDisposable
{
    // Literals, NOT Helper.ActivitySource.Name — reading the static field inside
    // ShouldListenTo re-enters the helper's type initializer (process-poisoning NRE).
    private static readonly string[] Sources =
    [
        "BBT.Workflow.Tasks", "BBT.Workflow.Scripting",
        "BBT.Workflow.Execution.Invokers", "BBT.Workflow.Pipeline",
        "BBT.Workflow.SubFlow", "BBT.Workflow.Cache"
    ];

    private readonly ActivityListener _listener;

    public SpanHelperBaggageTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => Array.IndexOf(Sources, s.Name) >= 0,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    private static Activity StartAmbientWithBaggage()
    {
        var ambient = new Activity("ambient-root");
        ambient.AddBaggage("vnext.test.baggage", "carried");
        ambient.Start();
        return ambient;
    }

    private static void AssertInheritsBaggage(Activity? span)
    {
        span.ShouldNotBeNull();
        span.Parent.ShouldNotBeNull("explicit-parent overload severs the Activity chain");
        span.GetBaggageItem("vnext.test.baggage").ShouldBe("carried");
        span.Dispose();
    }

    [Fact]
    public void TaskExecutionHelper_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(TaskExecutionActivityHelper.StartActivity("Task.PrepareInput", "k", "Http"));
    }

    [Fact]
    public void Script_execute_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(ScriptActivityHelper.StartExecuteActivity("lockKey"));
    }

    [Fact]
    public void Invoker_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(InvokerActivityHelper.StartInvokeActivity("http", "k"));
    }

    [Fact]
    public void SubFlow_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(SubFlowActivityHelper.StartActivity("SubFlow.Test"));
    }

    [Fact]
    public void Cache_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(CacheActivityHelper.StartActivity("Cache.Get", "component-key"));
    }
}
