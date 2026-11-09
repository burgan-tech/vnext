using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Agents;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Agents;

/// <summary>
/// The MAF runtime end to end with the deterministic local provider (issue #1105): tools are offered and
/// called through function invocation, the structured output satisfies the decision enum, and the session
/// round-trips so the next entry resumes with the earlier turns.
/// </summary>
public class MafAgentRuntimeLocalProviderTests
{
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        { "type": "object",
          "properties": { "category": { "type": "string", "enum": ["question", "fraud"] },
                          "decision": { "type": "string", "enum": ["is-question", "is-fraud"] },
                          "reason": { "type": "string" } },
          "required": ["category", "decision", "reason"] }
        """).RootElement;

    private static readonly JsonElement ToolSchema = JsonDocument.Parse("""
        { "type": "object", "properties": { "customerId": { "type": "string", "default": "c-1" } }, "required": ["customerId"] }
        """).RootElement;

    private static AgentRunRequest Request(IReadOnlyList<AgentToolDefinition> tools, byte[]? session = null) => new(
        Guid.NewGuid(), "triage", "local",
        new AgentProviderProfile { Kind = AgentProviderKinds.Local, Model = "local" },
        "Classify the message.", "My card was stolen.", OutputSchema, null, tools,
        EffectiveAgentPolicy.Resolve(new AgentPolicy(), null, new AiAgentOptions()),
        session, null, false, 40, CaptureTranscript: true);

    private static MafAgentRuntime Runtime() =>
        new(new ChatClientProvider(Substitute.For<ISecretResolver>()), NullLoggerFactory.Instance);

    [Fact]
    public async Task Run_calls_tools_and_returns_the_first_candidate()
    {
        var calls = new List<AgentToolInvocation>();
        var tool = new AgentToolDefinition("get_customer", "Customer by id.", ToolSchema, (invocation, _) =>
        {
            calls.Add(invocation);
            return Task.FromResult(new AgentToolResult(AgentToolCallStatus.Completed, """{"segment":"retail"}"""));
        });

        var result = await Runtime().RunAsync(Request([tool]), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Completed, result.FailureReason);
        calls.Count.ShouldBe(1);
        calls[0].Arguments.GetProperty("customerId").GetString().ShouldBe("c-1");
        result.Output!.Value.GetProperty("decision").GetString().ShouldBe("is-question");
        result.Session.ShouldNotBeNull();
        result.SessionMessageCount.ShouldBeGreaterThan(0);
        result.Transcript.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Resumed_session_keeps_the_earlier_turns()
    {
        var first = await Runtime().RunAsync(Request([]), CancellationToken.None);
        var second = await Runtime().RunAsync(Request([], first.Session), CancellationToken.None);

        second.Status.ShouldBe(AgentRunStatus.Completed, second.FailureReason);
        second.SessionMessageCount.ShouldBeGreaterThan(first.SessionMessageCount);
    }

    [Fact]
    public async Task Session_is_trimmed_to_max_messages()
    {
        byte[]? session = null;
        AgentRunResult? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await Runtime().RunAsync(Request([], session) with { MaxMessages = 3 }, CancellationToken.None);
            session = last.Session;
        }

        last!.SessionMessageCount.ShouldBeLessThanOrEqualTo(3);
        last.SessionMessagesTrimmed.ShouldBeGreaterThan(0);
    }
}
