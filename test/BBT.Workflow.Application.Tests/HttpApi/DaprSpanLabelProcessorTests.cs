using System.Diagnostics;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Pins the labelling contract of <see cref="DaprSpanLabelProcessor"/>: a Dapr gRPC client span
/// starting while a <see cref="DaprCallLabel"/> ambient is set gets the key as
/// <c>vnext.dapr.key</c>; any other span, or a Dapr span with no ambient, stays untouched.
/// </summary>
public sealed class DaprSpanLabelProcessorTests
{
    private readonly DaprSpanLabelProcessor _processor = new();

    [Fact]
    public void ADaprSpan_StartedInsideALabelScope_CarriesTheKey()
    {
        using var activity = new Activity("dapr.proto.runtime.v1.Dapr/GetState").Start();

        using (DaprCallLabel.Use("flow:core:my-workflow:gen"))
        {
            _processor.OnStart(activity);
        }

        activity.GetTagItem("vnext.dapr.key").ShouldBe("flow:core:my-workflow:gen");
    }

    [Fact]
    public void ADaprSpan_WithNoAmbientLabel_StaysUntagged()
    {
        using var activity = new Activity("dapr.proto.runtime.v1.Dapr/GetState").Start();

        _processor.OnStart(activity);

        activity.GetTagItem("vnext.dapr.key").ShouldBeNull();
    }

    [Fact]
    public void ANonDaprSpan_InsideALabelScope_StaysUntagged()
    {
        // The ambient may legitimately be set while other children start (e.g. the System.Net.Http
        // span under the gRPC call) — only the Dapr method span carries the key.
        using var activity = new Activity("POST").Start();

        using (DaprCallLabel.Use("some-key"))
        {
            _processor.OnStart(activity);
        }

        activity.GetTagItem("vnext.dapr.key").ShouldBeNull();
    }

    [Fact]
    public void NestedScopes_UnwindToThePreviousLabel()
    {
        using (DaprCallLabel.Use("outer-lock-key"))
        {
            using (DaprCallLabel.Use("inner-cache-key"))
            {
                DaprCallLabel.Current.ShouldBe("inner-cache-key");
            }

            DaprCallLabel.Current.ShouldBe("outer-lock-key");
        }

        DaprCallLabel.Current.ShouldBeNull();
    }
}
