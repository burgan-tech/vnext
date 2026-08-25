using System.Diagnostics;
using BBT.Workflow.Logging;
using OpenTelemetry;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// Stamps <c>vnext.dapr.key</c> onto Dapr gRPC client spans
/// (<c>dapr.proto.runtime.v1.Dapr/GetState</c>, <c>/SaveState</c>, <c>/TryLockAlpha1</c>, …)
/// from the <see cref="DaprCallLabel"/> ambient, at <see cref="OnStart"/>.
/// <para>
/// The state/lock key travels in the protobuf request body, which the gRPC instrumentation cannot
/// read — so the labelling decorators around the Dapr-backed cache/lock services put the key into
/// the ambient just before the call, and this processor moves it onto the span the call starts.
/// Without it a transition's many GetState spans are indistinguishable from one another.
/// OnStart is safe here: Grpc.Net.Client names the activity with the full method path at
/// creation, before any await.
/// </para>
/// </summary>
public sealed class DaprSpanLabelProcessor : BaseProcessor<Activity>
{
    private const string DaprGrpcMethodPrefix = "dapr.proto.runtime.v1.Dapr/";

    /// <inheritdoc />
    public override void OnStart(Activity activity)
    {
        if (DaprCallLabel.Current is { } key &&
            activity.OperationName.StartsWith(DaprGrpcMethodPrefix, StringComparison.Ordinal))
        {
            activity.SetTag(TelemetryConstants.TagNames.DaprKey, key);
        }
    }
}
