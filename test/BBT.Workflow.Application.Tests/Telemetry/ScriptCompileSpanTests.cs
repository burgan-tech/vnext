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
    public void Dispose()
    {
        Activity.Current = null;
    }

    [Fact]
    public void CompileActivity_RecordsCacheOutcome()
    {
        using var spans = new TraceSpanCollector("BBT.Workflow.Scripting");

        using (var activity = ScriptActivityHelper.StartCompileActivity())
        {
            ScriptActivityHelper.SetCompileResult(activity, cacheMiss: false, status: "success");
        }

        var span = Assert.Single(spans.Spans);
        Assert.Equal("Script.Compile", span.DisplayName);
        Assert.Equal(true, span.GetTagItem(TelemetryConstants.TagNames.ScriptCacheHit));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public void CompileActivity_FailureStatus_MarksError()
    {
        using var spans = new TraceSpanCollector("BBT.Workflow.Scripting");
        using (var activity = ScriptActivityHelper.StartCompileActivity())
        {
            ScriptActivityHelper.SetCompileResult(activity, cacheMiss: true, status: "compilation_error");
        }

        Assert.Equal(ActivityStatusCode.Error, spans.Spans.Single().Status);
    }

    [Fact]
    public void ExecuteActivity_CarriesScriptKind()
    {
        using var spans = new TraceSpanCollector("BBT.Workflow.Scripting");
        using (ScriptActivityHelper.StartExecuteActivity("lockKey")) { }

        Assert.Equal("lockKey", spans.Spans.Single().GetTagItem(TelemetryConstants.TagNames.ScriptKind));
    }

    [Fact]
    public void InvokeActivity_IsChildOfExecuteActivity()
    {
        using var spans = new TraceSpanCollector("BBT.Workflow.Scripting");

        using (var execute = ScriptActivityHelper.StartExecuteActivity("subflowInputMapping"))
        {
            using var invoke = ScriptActivityHelper.StartInvokeActivity();
        }

        var executeSpan = spans.Spans.Single(x => x.DisplayName == "Script.Execute");
        var invokeSpan = spans.Spans.Single(x => x.DisplayName == "Script.Invoke");
        Assert.Equal(executeSpan.TraceId, invokeSpan.TraceId);
        Assert.Equal(executeSpan.SpanId, invokeSpan.ParentSpanId);
    }
}
