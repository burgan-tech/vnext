using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

public sealed class TaskBindingMapperDaprConversationTaskTests
{
    [Fact]
    public void CreateEnvelope_MapsConversationTask()
    {
        var config = """
            {
              "componentName": "openai",
              "contextId": "ctx-1",
              "temperature": 0.7,
              "scrubPII": true,
              "inputs": [
                { "role": "system", "content": "You are helpful." },
                { "role": "user", "content": "Hello", "name": "ada", "scrubPII": false }
              ],
              "parameters": { "model": "gpt-4o", "max_tokens": 256 },
              "metadata": { "region": "eu" }
            }
            """;
        var task = DaprConversationTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("ask-llm", "test-domain", "sys-tasks", "1.0.0"));

        var result = TaskBindingMapper.CreateEnvelope(task);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.TaskType.ShouldBe(TaskTypes.DaprConversation);

        var binding = result.Value!.Binding.Deserialize<DaprConversationBinding>();
        binding.ShouldNotBeNull();
        binding!.ComponentName.ShouldBe("openai");
        binding.ContextId.ShouldBe("ctx-1");
        binding.Temperature.ShouldBe(0.7);
        binding.ScrubPII.ShouldBe(true);

        binding.Inputs.Count.ShouldBe(2);
        binding.Inputs[0].Role.ShouldBe("system");
        binding.Inputs[0].Content.ShouldBe("You are helpful.");
        binding.Inputs[1].Role.ShouldBe("user");
        binding.Inputs[1].Content.ShouldBe("Hello");
        binding.Inputs[1].Name.ShouldBe("ada");
        binding.Inputs[1].ScrubPII.ShouldBe(false);

        binding.Parameters.ShouldNotBeNull();
        binding.Parameters!["model"].ShouldBe("gpt-4o");
        binding.Parameters!["max_tokens"].ShouldBe("256");
        binding.Metadata.ShouldNotBeNull();
        binding.Metadata!["region"].ShouldBe("eu");
    }

    [Fact]
    public void CreateEnvelope_DefaultsRoleToUser_WhenMissing()
    {
        var config = """
            {
              "componentName": "openai",
              "inputs": [ { "content": "Hi there" } ]
            }
            """;
        var task = DaprConversationTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("ask-llm", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.Inputs.Count.ShouldBe(1);
        binding.Inputs[0].Role.ShouldBe("user");
        binding.Inputs[0].Content.ShouldBe("Hi there");
        binding.ContextId.ShouldBeNull();
        binding.Parameters.ShouldBeNull();
        binding.Metadata.ShouldBeNull();
    }

    private static DaprConversationBinding MapToBinding(DaprConversationTask task)
    {
        var result = TaskBindingMapper.CreateEnvelope(task);
        result.IsSuccess.ShouldBeTrue();
        var binding = result.Value!.Binding.Deserialize<DaprConversationBinding>();
        binding.ShouldNotBeNull();
        return binding!;
    }
}
