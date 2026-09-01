using BBT.Workflow.Execution.Grpc;
using Grpc.Core;

namespace BBT.Workflow.Execution.Invocation;

/// <summary>
/// gRPC surface of the task-invoke endpoint, served over Dapr gRPC proxy mode on the same
/// Kestrel endpoint as the HTTP controller.
/// <para>
/// A thin shell by design: transport decode → <see cref="TaskInvokeHandler"/> → transport
/// encode. Everything with behavior (activity enrichment, trace restore, the registry
/// call) lives in the shared handler so the HTTP controller and this service cannot
/// drift apart.
/// </para>
/// </summary>
public sealed class TaskInvokerGrpcService(TaskInvokeHandler handler)
    : TaskInvoker.TaskInvokerBase
{
    /// <summary>
    /// Deserializes the request envelope, runs it through the shared handler, and re-serializes
    /// the response — see the class summary for why nothing else belongs here.
    /// </summary>
    /// <param name="request">The gRPC request carrying the JSON-encoded <see cref="TaskInvokeRequest"/>.</param>
    /// <param name="context">The gRPC call context; only its cancellation token is used.</param>
    /// <returns>The gRPC reply carrying the JSON-encoded <see cref="TaskInvokeResponse"/>.</returns>
    public override async Task<InvokeReply> Invoke(InvokeRequest request, ServerCallContext context)
    {
        var invokeRequest = TaskInvokePayload.Deserialize<TaskInvokeRequest>(request.PayloadJson);
        var response = await handler.HandleAsync(invokeRequest, context.CancellationToken);
        return new InvokeReply { PayloadJson = TaskInvokePayload.Serialize(response) };
    }
}
