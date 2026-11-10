using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Agents;

/// <summary>Publish-time validation of <c>sys-agents</c> components and MCP tasks (issue #1105).</summary>
public class AgentComponentValidationTests
{
    private const string ValidAgent = """
    {
      "provider": "local",
      "instructions": { "text": "Classify." },
      "output": { "schema": { "type": "object", "properties": { "category": { "type": "string" } } } },
      "policy": { "maxIterations": 4, "maxToolCalls": 2, "timeoutSeconds": 30 },
      "tools": [
        { "kind": 2, "name": "days_since", "description": "Days since a date.",
          "parameters": { "type": "object", "properties": { "date": { "type": "string" } }, "required": ["date"] },
          "binding": { "code": { "location": "dynamicExpresso", "encoding": "NAT", "code": "1" } } },
        { "kind": 1, "name": "get_customer", "description": "Customer by id.",
          "parameters": { "type": "object", "properties": {} },
          "binding": { "task": { "key": "get-customer", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" } } },
        { "kind": 3, "binding": { "task": { "key": "crm-mcp", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
                                  "allow": ["search"], "prefix": "crm_" } }
      ]
    }
    """;

    private static ComponentValidationResult ValidateAgent(string json) =>
        new AgentComponentValidator().Validate(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Valid_agent_passes()
    {
        ValidateAgent(ValidAgent).ValidationErrors.Select(e => e.ErrorMessage).ShouldBeEmpty();
    }

    [Fact]
    public void Inline_tools_deserialize_with_their_kinds()
    {
        var agent = JsonSerializer.Deserialize<Agent>(ValidAgent, JsonSerializerConstants.JsonOptions)!;

        agent.Tools.Select(t => t.Kind).ShouldBe([AgentToolKind.Script, AgentToolKind.Task, AgentToolKind.Mcp]);
        agent.Tools[2].Binding.Allow.ShouldBe(["search"]);
        agent.Instructions!.Resolved.ShouldBe("Classify.");
    }

    [Theory]
    [InlineData("\"provider\": \"local\"", "\"provider\": \"\"", "provider")]
    [InlineData("\"name\": \"days_since\"", "\"name\": \"get_customer\"", "used more than once")]
    [InlineData("\"type\": \"object\", \"properties\": { \"category\"", "\"type\": \"array\", \"properties\": { \"category\"", "output.schema")]
    [InlineData("\"allow\": [\"search\"]", "\"allow\": []", "binding.allow")]
    public void Invalid_agent_is_rejected(string from, string to, string expected)
    {
        var result = ValidateAgent(ValidAgent.Replace(from, to));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains(expected) || e.MemberNames.Any(m => m.Contains(expected)));
    }

    [Fact]
    public void Policy_above_the_environment_ceiling_is_rejected()
    {
        var validator = new AgentComponentValidator(Options.Create(new BBT.Workflow.Agents.AiAgentOptions { MaxIterations = 3 }));

        validator.Validate(JsonDocument.Parse(ValidAgent).RootElement)
            .ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("exceeds the environment ceiling"));
    }

    private static ComponentValidationResult ValidateMcp(string config, params string[] allowedHosts) =>
        new TaskComponentValidator(Options.Create(new McpOptions { AllowedHosts = allowedHosts.ToList() }))
            .Validate(JsonDocument.Parse($$"""{ "type": "24", "config": {{config}} }""").RootElement);

    [Fact]
    public void Mcp_task_on_an_allowed_host_with_secret_headers_passes()
    {
        var result = ValidateMcp("""
            { "endpoint": "https://crm-mcp.internal/mcp", "operation": "callTool", "tool": "search",
              "auth": { "scheme": "Bearer", "secretRef": "vault/workflow-secret/CRM" },
              "headers": { "X-Tenant": { "secretRef": "CRM_TENANT" } } }
            """, "crm-mcp.internal");

        result.ValidationErrors.Select(e => e.ErrorMessage).ShouldBeEmpty();
    }

    [Fact]
    public void Mcp_task_rejects_unknown_hosts_plain_headers_and_stdio()
    {
        var result = ValidateMcp("""
            { "transport": "stdio", "endpoint": "https://evil.example/mcp", "headers": { "X-Key": "plain" } }
            """, "crm-mcp.internal");

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("not in Mcp:AllowedHosts"));
        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("plain-text header values are not allowed"));
        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("transport must be 'http'"));
    }

    [Fact]
    public void Mcp_task_config_parses()
    {
        var task = (McpTask)JsonSerializer.Deserialize<WorkflowTask>(
            """{ "type": "24", "config": { "endpoint": "http://localhost:9000/mcp", "operation": "listTools", "timeoutSeconds": 5, "arguments": { "q": 1 } } }""",
            JsonSerializerConstants.JsonOptions)!;

        task.SetReference(new Reference("crm-mcp", "core", "sys-tasks", "1.0.0"));
        task.GetTaskType().ShouldBe(TaskType.Mcp);
        task.Operation.ShouldBe(McpOperations.ListTools);
        task.TimeoutSeconds.ShouldBe(5);
        task.CloneTyped().Arguments!.Value.GetProperty("q").GetInt32().ShouldBe(1);
    }
}
