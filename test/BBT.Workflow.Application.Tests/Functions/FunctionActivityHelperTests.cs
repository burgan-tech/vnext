using System;
using System.Diagnostics;
using BBT.Workflow.Functions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Functions;

[Collection("TracingDetailLevel")]
public sealed class FunctionActivityHelperTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Functions"; // literal — ShouldListenTo trap

    private readonly ActivityListener _listener;

    public FunctionActivityHelperTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() { _listener.Dispose(); Activity.Current = null; }

    [Fact]
    public void Execute_span_carries_key_layer_and_category()
    {
        using var span = FunctionActivityHelper.StartExecute("my-fn");
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Function.Execute/my-fn");
        span.GetTagItem("vnext.span.category").ShouldBe("business");
        span.GetTagItem("vnext.layer").ShouldBe("orchestration");
    }

    [Fact]
    public void Phase_span_inherits_baggage_from_ambient()
    {
        using var ambient = new Activity("root");
        ambient.AddBaggage("k", "v");
        ambient.Start();

        using var span = FunctionActivityHelper.StartPhase(FunctionActivityHelper.OperationValidateRequest);
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Function.ValidateRequest");
        span.GetBaggageItem("k").ShouldBe("v");
    }
}
