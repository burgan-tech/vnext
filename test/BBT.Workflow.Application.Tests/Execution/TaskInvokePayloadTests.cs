using System;
using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Grpc;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution;

/// <summary>
/// Pins the gRPC payload serializer to the HTTP endpoint's JSON conventions. If these
/// two ever diverge, the same Execution build would parse a request differently
/// depending on which transport delivered it.
/// </summary>
public sealed class TaskInvokePayloadTests
{
    [Fact]
    public void RoundTrip_PreservesTheTaskInvokeRequest()
    {
        var binding = JsonSerializer.SerializeToElement(new { url = "https://x", method = "GET" });
        var request = new TaskInvokeRequest
        {
            Envelope = new TaskEnvelope
            {
                TaskType = "3",
                TaskKey = "get-iban-history",
                Binding = binding
            },
            TraceContext = new TaskTraceContext
            {
                InstanceId = Guid.NewGuid(),
                Domain = "core",
                WorkflowKey = "money-transfer",
                WorkflowVersion = "1.0.2",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Sub = "user-1",
                RequestId = "req-1"
            }
        };

        var bytes = TaskInvokePayload.Serialize(request);
        var back = TaskInvokePayload.Deserialize<TaskInvokeRequest>(bytes);

        back.Envelope.TaskKey.ShouldBe("get-iban-history");
        back.Envelope.Binding.GetProperty("url").GetString().ShouldBe("https://x");
        back.TraceContext!.Domain.ShouldBe("core");
    }

    [Fact]
    public void Serialize_UsesCamelCase_MatchingTheHttpEndpoint()
    {
        var response = new TaskInvokeResponse { Success = true, ExecutionDurationMs = 42 };

        var json = TaskInvokePayload.Serialize(response).ToStringUtf8();

        json.ShouldContain("\"success\":true");     // camelCase, not PascalCase
        json.ShouldContain("\"executionDurationMs\":42");
    }
}
