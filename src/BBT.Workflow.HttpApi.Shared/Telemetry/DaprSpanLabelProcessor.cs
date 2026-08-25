using System.Diagnostics;
using BBT.Workflow.Logging;
using OpenTelemetry;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// Stamps <c>vnext.dapr.key</c> onto Dapr gRPC client spans
/// (<c>dapr.proto.runtime.v1.Dapr/GetState</c>, <c>/SaveState</c>, <c>/TryLockAlpha1</c>, …)
/// from the <see cref="DaprCallLabel"/> ambient.
/// <para>
/// The state/lock key travels in the protobuf request body, which the gRPC instrumentation cannot
/// read — so the labelling decorators around the Dapr-backed cache/lock services put the key into
/// the ambient just before the call, and this processor moves it onto the span the call starts.
/// Without it a transition's many GetState spans are indistinguishable from one another.
/// </para>
/// <para>
/// Matching note: Grpc.Net.Client creates a LEGACY DiagnosticSource activity whose
/// <see cref="Activity.OperationName"/> is fixed at <c>Grpc.Net.Client.GrpcOut</c>; the
/// instrumentation only renames <see cref="Activity.DisplayName"/> to the gRPC method path. The
/// exported span name is the DisplayName, so matching must look at both — and because the rename
/// order relative to processor <see cref="OnStart"/> is an implementation detail, <see cref="OnEnd"/>
/// re-checks and stamps late when the start-side match missed. The ambient is still set at OnEnd:
/// the activity stops inside the awaited Dapr call, within the decorator's scope.
/// </para>
/// </summary>
public sealed class DaprSpanLabelProcessor : BaseProcessor<Activity>
{
    private const string DaprGrpcMethodPrefix = "dapr.proto.runtime.v1.Dapr/";
    private const string GrpcLegacyOperationName = "Grpc.Net.Client.GrpcOut";

    /// <inheritdoc />
    public override void OnStart(Activity activity)
    {
        TryStamp(activity);
    }

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        if (activity.GetTagItem(TelemetryConstants.TagNames.DaprKey) is null)
        {
            TryStamp(activity);
        }
    }

    private static void TryStamp(Activity activity)
    {
        if (DaprCallLabel.Current is { } key && IsDaprClientSpan(activity))
        {
            activity.SetTag(TelemetryConstants.TagNames.DaprKey, key);
        }
    }

    private static bool IsDaprClientSpan(Activity activity) =>
        activity.OperationName.Equals(GrpcLegacyOperationName, StringComparison.Ordinal)
        || activity.DisplayName.StartsWith(DaprGrpcMethodPrefix, StringComparison.Ordinal)
        || activity.OperationName.StartsWith(DaprGrpcMethodPrefix, StringComparison.Ordinal);
}
