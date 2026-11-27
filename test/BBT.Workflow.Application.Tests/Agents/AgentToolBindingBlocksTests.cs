using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Agents;
using BBT.Workflow.Agents.Tools;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Agents;

/// <summary>
/// Agent tool kind 1 <c>binding.input</c> / <c>binding.output</c> (Phase B): deserialized and round-tripped with the
/// agent, validated at publish like a task binding's blocks (and exclusive with <c>binding.mapping</c>), and passed to
/// the task engine as the binding's declarative blocks.
/// </summary>
public sealed class AgentToolBindingBlocksTests
{
    private const string TaskRef = """{ "key": "get-customer", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" }""";

    private static string AgentJson(string bindingExtra) => $$"""
    {
      "provider": "local",
      "instructions": { "text": "Classify." },
      "output": { "schema": { "type": "object", "properties": { "category": { "type": "string" } } } },
      "policy": { "maxIterations": 4, "maxToolCalls": 2, "timeoutSeconds": 30 },
      "tools": [
        { "kind": 1, "name": "get_customer", "description": "Customer by id.",
          "parameters": { "type": "object", "properties": {} },
          "binding": { "task": {{TaskRef}}{{bindingExtra}} } }
      ]
    }
    """;

    private const string Blocks = """
        , "input": { "set": { "$.url": "\"https://crm/customers/\" + urlEncode(input.id)" } },
          "output": { "select": "$.body.customer", "into": "$.customer" }
        """;

    private static ComponentValidationResult Validate(string json) =>
        new AgentComponentValidator().Validate(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Blocks_Deserialize_And_RoundTrip_WithTheAgent()
    {
        var agent = JsonSerializer.Deserialize<Agent>(AgentJson(Blocks), JsonSerializerConstants.JsonOptions)!;

        var binding = agent.Tools.ShouldHaveSingleItem().Binding;
        binding.Input!.Set!["$.url"].ShouldBe("\"https://crm/customers/\" + urlEncode(input.id)");
        binding.Output!.Select.ShouldBe("$.body.customer");

        var written = JsonSerializer.Serialize(agent, JsonSerializerConstants.JsonOptions);
        var back = JsonSerializer.Deserialize<Agent>(written, JsonSerializerConstants.JsonOptions)!;

        var roundTripped = back.Tools.ShouldHaveSingleItem().Binding;
        roundTripped.Input!.Set!["$.url"].ShouldBe(binding.Input.Set["$.url"]);
        roundTripped.Output!.Into.ShouldBe("$.customer");
        written.ShouldContain("\"input\":");
        written.ShouldContain("\"output\":");
    }

    [Fact]
    public void Valid_Blocks_Pass()
    {
        Validate(AgentJson(Blocks)).ValidationErrors.Select(e => e.ErrorMessage).ShouldBeEmpty();
    }

    [Fact]
    public void Mapping_And_Blocks_Together_AreRejected()
    {
        var mapping = """, "mapping": { "location": "./src/M.csx", "encoding": "NAT", "code": "public class M {}" }""";

        var errors = Validate(AgentJson(Blocks + mapping)).ValidationErrors.Select(e => e.ErrorMessage!).ToList();

        errors.ShouldContain(m => m.Contains("cannot carry both a 'binding.mapping' script"));
    }

    [Fact]
    public void Invalid_Block_IsRejected_AtItsBindingPath()
    {
        var bad = """, "output": { "into": "$.customer[0]" }""";

        var errors = Validate(AgentJson(bad)).ValidationErrors.Select(e => e.ErrorMessage!).ToList();

        errors.ShouldHaveSingleItem().ShouldStartWith("Agent.tools[0].binding.output.into: ");
    }

    [Fact]
    public void Expression_ThatDoesNotCompile_IsRejected()
    {
        var bad = """, "input": { "set": { "$.url": "data.x." } }""";

        var errors = Validate(AgentJson(bad)).ValidationErrors.Select(e => e.ErrorMessage!).ToList();

        errors.ShouldHaveSingleItem().ShouldContain("does not compile");
    }

    [Fact]
    public async Task Catalog_PassesTheBlocks_ToTheTaskEngine()
    {
        var agent = JsonSerializer.Deserialize<Agent>(AgentJson(Blocks), JsonSerializerConstants.JsonOptions)!;
        var captured = new List<OnExecuteTask>();

        var engine = Substitute.For<ITaskExecutionEngine>();
        engine.ExecuteAsync(
                Arg.Do<OnExecuteTask>(captured.Add), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(), Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(), Arg.Any<TaskEngineExecutionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<TasksExecutionResult>.Fail(Error.Failure("test", "stop here")));
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ITaskExecutionEngine)).Returns(engine);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var taskFactory = Substitute.For<ITaskFactory>();
        taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(WorkflowTaskFactory.CreateHttpTask("get-customer")));

        var catalog = new AgentToolCatalog(scopeFactory, taskFactory, Substitute.For<IScriptEngine>(),
            new ExpressionCompiler(16), NullLogger<AgentToolCatalog>.Instance);
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "k"))
            .Build();
        var recorder = new AgentRunRecorder(Guid.NewGuid(), Guid.NewGuid(),
            EffectiveAgentPolicy.Resolve(null, null, new AiAgentOptions()), true, DateTime.UtcNow, NullLogger.Instance);

        var tools = await catalog.BuildAsync(
            new AgentToolBuildContext(agent, new HashSet<string>(), [], scriptContext, null, recorder, Stub: false),
            CancellationToken.None);
        await tools.ShouldHaveSingleItem().InvokeAsync(
            new AgentToolInvocation("call-1", "get_customer", JsonDocument.Parse("""{"id":"42"}""").RootElement, 1),
            CancellationToken.None);

        var binding = captured.ShouldHaveSingleItem();
        binding.Input.ShouldBe(agent.Tools[0].Binding.Input);
        binding.Output.ShouldBe(agent.Tools[0].Binding.Output);
        TaskMappingRouter.InputKind(binding).ShouldBe(TaskMappingKind.Declarative);
        TaskMappingRouter.OutputKind(binding).ShouldBe(TaskMappingKind.Declarative);
    }
}
