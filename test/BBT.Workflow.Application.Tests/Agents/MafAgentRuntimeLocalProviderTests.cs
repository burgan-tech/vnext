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
        new(new ChatClientProvider(Substitute.For<ISecretResolver>()), new BBT.Workflow.Validation.CachedJsonSchemaValidator(), NullLoggerFactory.Instance);

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

    [Fact]
    public async Task Approval_required_tool_holds_the_run_and_resumes_on_approval()
    {
        var calls = new List<AgentToolInvocation>();
        var tool = new AgentToolDefinition("get_customer", "Customer by id.", ToolSchema, (invocation, _) =>
        {
            calls.Add(invocation);
            return Task.FromResult(new AgentToolResult(AgentToolCallStatus.Completed, """{"segment":"retail"}"""));
        }, RequiresApproval: true);

        var held = await Runtime().RunAsync(Request([tool]), CancellationToken.None);

        held.Status.ShouldBe(AgentRunStatus.ApprovalRequested, held.FailureReason);
        calls.ShouldBeEmpty();
        var pending = held.PendingApprovals.ShouldHaveSingleItem();
        pending.ToolName.ShouldBe("get_customer");
        pending.RequiresConfirmation.ShouldBeTrue();
        pending.Arguments.GetProperty("customerId").GetString().ShouldBe("c-1");
        held.Session.ShouldNotBeNull();

        var resumed = await Runtime().RunAsync(
            Request([tool], held.Session) with { ApprovalAnswers = [new AgentApprovalAnswer(pending, true, null)] },
            CancellationToken.None);

        resumed.Status.ShouldBe(AgentRunStatus.Completed, resumed.FailureReason);
        calls.ShouldHaveSingleItem().CallId.ShouldBe(pending.CallId);
        resumed.Output!.Value.GetProperty("decision").GetString().ShouldBe("is-question");
    }

    [Fact]
    public async Task Denied_approval_does_not_run_the_tool()
    {
        var calls = 0;
        var tool = new AgentToolDefinition("get_customer", "Customer by id.", ToolSchema, (_, _) =>
        {
            calls++;
            return Task.FromResult(new AgentToolResult(AgentToolCallStatus.Completed, "{}"));
        }, RequiresApproval: true);

        var held = await Runtime().RunAsync(Request([tool]), CancellationToken.None);
        var pending = held.PendingApprovals.ShouldHaveSingleItem();

        var resumed = await Runtime().RunAsync(
            Request([tool], held.Session) with { ApprovalAnswers = [new AgentApprovalAnswer(pending, false, "not allowed")] },
            CancellationToken.None);

        resumed.Status.ShouldBe(AgentRunStatus.Completed, resumed.FailureReason);
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task Summarize_compaction_replaces_old_messages_with_a_summary()
    {
        byte[]? session = null;
        AgentRunResult? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await Runtime().RunAsync(
                Request([], session) with { MaxMessages = 3, Compaction = AgentMemoryCompaction.Summarize },
                CancellationToken.None);
            last.Status.ShouldBe(AgentRunStatus.Completed, last.FailureReason);
            session = last.Session;
        }

        last!.SessionMessagesTrimmed.ShouldBeGreaterThan(0);
        last.SessionMessageCount.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task Seed_messages_are_sent_and_kept_in_the_session()
    {
        var plain = await Runtime().RunAsync(Request([]), CancellationToken.None);
        var seeded = await Runtime().RunAsync(
            Request([]) with { SeedMessages = [new AgentSeedMessage("user", "Example: card lost"), new AgentSeedMessage("assistant", "{\"category\":\"question\"}")] },
            CancellationToken.None);

        seeded.Status.ShouldBe(AgentRunStatus.Completed, seeded.FailureReason);
        seeded.SessionMessageCount.ShouldBe(plain.SessionMessageCount + 2);
    }
}
