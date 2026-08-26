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
}
