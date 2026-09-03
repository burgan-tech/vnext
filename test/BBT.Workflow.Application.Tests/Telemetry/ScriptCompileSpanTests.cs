using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the <see cref="ScriptActivityHelper"/> span contract: <c>Script.Compile</c> tags the
/// cache outcome and maps a non-success status to an error span; <c>Script.Execute</c> carries
/// the script-kind tag and <c>Script.Invoke</c> accounts for the compiled delegate call. This
/// reverses the earlier "no compile span" decision — see
/// <see cref="ScriptActivityHelper"/>'s class doc.
/// </summary>
public sealed class ScriptCompileSpanTests : IDisposable
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
    public void CompileActivity_RecordsCacheOutcome()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Scripting", collected);

        using (var activity = ScriptActivityHelper.StartCompileActivity())
        {
            ScriptActivityHelper.SetCompileResult(activity, cacheMiss: false, status: "success");
        }

        var span = Assert.Single(collected);
        Assert.Equal("Script.Compile", span.DisplayName);
        Assert.Equal(true, span.GetTagItem(TelemetryConstants.TagNames.ScriptCacheHit));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public void CompileActivity_FailureStatus_MarksError()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Scripting", collected);
        using (var activity = ScriptActivityHelper.StartCompileActivity())
        {
            ScriptActivityHelper.SetCompileResult(activity, cacheMiss: true, status: "compilation_error");
        }

        Assert.Equal(ActivityStatusCode.Error, collected.Single().Status);
    }

    [Fact]
    public void ExecuteActivity_CarriesScriptKind()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Scripting", collected);
        using (ScriptActivityHelper.StartExecuteActivity("lockKey")) { }

        Assert.Equal("lockKey", collected.Single().GetTagItem(TelemetryConstants.TagNames.ScriptKind));
    }

    [Fact]
    public void InvokeActivity_IsChildOfExecuteActivity()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Scripting", collected);

        using (var execute = ScriptActivityHelper.StartExecuteActivity("subflowInputMapping"))
        {
            using var invoke = ScriptActivityHelper.StartInvokeActivity();
        }

        var executeSpan = collected.Single(x => x.DisplayName == "Script.Execute");
        var invokeSpan = collected.Single(x => x.DisplayName == "Script.Invoke");
        Assert.Equal(executeSpan.TraceId, invokeSpan.TraceId);
        Assert.Equal(executeSpan.SpanId, invokeSpan.ParentSpanId);
    }
}
