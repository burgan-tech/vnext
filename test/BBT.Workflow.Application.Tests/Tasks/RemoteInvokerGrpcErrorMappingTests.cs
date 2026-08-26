using BBT.Workflow.Tasks.Executors;
using Grpc.Core;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks;

/// <summary>
/// Pins gRPC transport failures to the SAME TaskInvocationResult shapes the HTTP path
/// produces, so the error boundary sees one contract regardless of transport.
/// </summary>
public sealed class RemoteInvokerGrpcErrorMappingTests
{
    [Fact]
    public void DeadlineExceeded_MapsToTheSame408TheHttpTimeoutProduces()
    {
        var ex = new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline"));

        var result = RemoteInvokerService.MapRpcFailure(ex, elapsedMs: 60_000, taskType: "3", timeoutSeconds: 60);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(408);
        result.ErrorMessage.ShouldContain("60");
        result.TaskType.ShouldBe("3");
    }

    [Fact]
    public void Unavailable_MapsToTheSame500AnyTransportFailureProduces()
    {
        var ex = new RpcException(new Status(StatusCode.Unavailable, "connection refused"));

        var result = RemoteInvokerService.MapRpcFailure(ex, elapsedMs: 12, taskType: "3", timeoutSeconds: 60);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
        result.ErrorMessage.ShouldContain("connection refused");
    }

    /// <summary>
    /// This is the status the live gRPC call ACTUALLY produces for our own per-invocation
    /// timeout: Grpc.Net.Client's GrpcChannelOptions.ThrowOperationCanceledOnCancellation
    /// defaults to false, so both the linked CTS's CancelAfter and the explicit
    /// CallOptions.deadline surface as RpcException(Cancelled) — not
    /// OperationCanceledException the way the HTTP/Dapr path throws. MapRpcFailure is only
    /// ever invoked (from InvokeOverGrpcAsync's catch (RpcException ex) clause) after the
    /// parent-cancellation case has already been intercepted and rethrown by the preceding
    /// catch, so Cancelled reaching here can only mean "our own timeout fired" — it must
    /// produce the same 408 shape DeadlineExceeded does, not 500.
    /// </summary>
    [Fact]
    public void Cancelled_MapsToTheSame408OwnTimeoutProduces()
    {
        var ex = new RpcException(new Status(StatusCode.Cancelled, "cancelled"));

        var result = RemoteInvokerService.MapRpcFailure(ex, elapsedMs: 60_000, taskType: "3", timeoutSeconds: 60);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(408);
        result.ErrorMessage.ShouldContain("60");
        result.TaskType.ShouldBe("3");
    }
}
