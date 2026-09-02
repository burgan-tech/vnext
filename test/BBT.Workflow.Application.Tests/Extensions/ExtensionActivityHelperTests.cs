using System;
using System.Diagnostics;
using BBT.Workflow.Definitions;
using BBT.Workflow.Extentions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Extensions;

[Collection("TracingDetailLevel")]
public sealed class ExtensionActivityHelperTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Extensions"; // literal — ShouldListenTo trap

    private readonly ActivityListener _listener;

    public ExtensionActivityHelperTests()
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
    public void Process_span_names_scope_and_tags_workflow()
    {
        using var span = ExtensionActivityHelper.StartProcess("loan-disbursement", ExtensionScope.GetInstance);
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Extension.Process/GetInstance");
        span.GetTagItem("vnext.flow.key").ShouldBe("loan-disbursement");
        span.GetTagItem("vnext.layer").ShouldBe("orchestration");
        span.GetTagItem("vnext.span.category").ShouldBe("business");
    }

    [Fact]
    public void Resolve_span_tags_reference_count()
    {
        using var span = ExtensionActivityHelper.StartResolve(3);
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Extension.Resolve");
        span.GetTagItem("vnext.extension.ref.count").ShouldBe(3);
        span.GetTagItem("vnext.span.category").ShouldBe("business");
    }
}
