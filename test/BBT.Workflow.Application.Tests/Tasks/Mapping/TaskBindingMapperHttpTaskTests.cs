using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

public sealed class TaskBindingMapperHttpTaskTests
{
    [Fact]
    public void CreateEnvelope_MapsHttpTaskAcceptedStatusCodes()
    {
        var config = """
            {
              "url": "https://workflow.local/functions/send-otp",
              "method": "POST",
              "acceptedStatusCodes": [ "400", "4xx" ],
              "timeoutSeconds": 30
            }
            """;
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("send-otp", "test-domain", "sys-tasks", "1.0.0"));

        var result = TaskBindingMapper.CreateEnvelope(task);

        result.IsSuccess.ShouldBeTrue();
        var binding = result.Value!.Binding.Deserialize<HttpTaskBinding>();
        binding.ShouldNotBeNull();
        binding.AcceptedStatusCodes.ShouldNotBeNull();
        binding.AcceptedStatusCodes.ShouldBe(["400", "4xx"]);
    }
}
