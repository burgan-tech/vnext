using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Agents;
using BBT.Workflow.Agents.Tools;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Agents;

/// <summary>
/// Model-produced JSON is checked against its schema (issue #1105): tool arguments before the tool runs and
/// before a call is held for approval, the structured output before it is returned, with one repair turn.
/// </summary>
public class AgentSchemaValidationTests
{
    private static readonly CachedJsonSchemaValidator Validator = new();

    private static readonly JsonElement OutputSchema = Json("""
        { "type": "object",
          "properties": { "amount": { "type": ["number", "null"] }, "purpose": { "type": "string" } },
          "required": ["amount", "purpose"] }
        """);

    private static readonly JsonElement RefundParameters = Json("""
        { "type": "object", "properties": { "amount": { "type": "number", "minimum": 1 } }, "required": ["amount"] }
        """);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Describe_names_the_failing_fields()
    {
        AgentSchemaCheck.Describe(Validator, OutputSchema, Json("""{"amount": 5, "purpose": "car"}""")).ShouldBeNull();
        AgentSchemaCheck.Describe(Validator, OutputSchema, Json("""{"amount": null, "purpose": "car"}""")).ShouldBeNull();

        var errors = AgentSchemaCheck.Describe(Validator, OutputSchema, Json("""{"amount": "fifty"}"""));

        errors.ShouldNotBeNull();
        errors.ShouldContain("purpose");
        errors.ShouldContain("amount");
    }

    [Fact]
    public async Task Tool_with_arguments_of_the_wrong_type_is_rejected_without_running()
    {
        var recorder = new AgentRunRecorder(Guid.NewGuid(), Guid.NewGuid(), Policy(), true, DateTime.UtcNow, NullLogger.Instance);
        var ran = false;
        var invoke = recorder.Wrap("issue_refund", AgentToolKind.Task, null, RefundParameters, null, null,
            (_, _) => { ran = true; return Task.FromResult(new AgentToolResult(AgentToolCallStatus.Completed, "{}")); },
            args => AgentSchemaCheck.Describe(Validator, RefundParameters, args));

        var result = await invoke(new AgentToolInvocation("c-1", "issue_refund", Json("""{"amount": "120"}"""), 1), CancellationToken.None);

        ran.ShouldBeFalse();
        result.Status.ShouldBe(AgentToolCallStatus.Rejected);
        result.Error.ShouldNotBeNull().ShouldStartWith("invalid arguments: amount");
        recorder.Calls.ShouldHaveSingleItem().Status.ShouldBe(AgentToolCallStatus.Rejected);
    }

    [Fact]
    public async Task Output_that_does_not_match_gets_one_repair_turn()
    {
        var client = new ScriptedChatClient(
            Text("""{"amount": "fifty thousand"}"""),
            Text("""{"amount": 50000, "purpose": "car"}"""));

        var result = await Runtime(client).RunAsync(Request([]), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Completed, result.FailureReason);
        result.Output!.Value.GetProperty("amount").GetInt32().ShouldBe(50000);
        client.Calls.ShouldBe(2);
        client.LastUserText.ShouldContain("does not match the required output schema");
    }

    [Fact]
    public async Task Output_that_still_does_not_match_fails_the_run()
    {
        var client = new ScriptedChatClient(Text("""{"amount": 1}"""), Text("not json"));

        var result = await Runtime(client).RunAsync(Request([]), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.FailureReason.ShouldNotBeNull().ShouldContain("output does not match the output schema after 1 repair(s)");
        result.Output.ShouldBeNull();
        client.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Approval_call_with_invalid_arguments_is_denied_before_anyone_is_asked()
    {
        var ran = 0;
        var tool = RefundTool(() => ran++);
        var client = new ScriptedChatClient(
            Call("c-1", """{"amount": "yüz yirmi"}"""),
            Call("c-2", """{"amount": 120}"""));

        var result = await Runtime(client).RunAsync(Request([tool]), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.ApprovalRequested, result.FailureReason);
        var pending = result.PendingApprovals.ShouldHaveSingleItem();
        pending.CallId.ShouldBe("c-2");
        pending.Arguments.GetProperty("amount").GetInt32().ShouldBe(120);
        ran.ShouldBe(0);
        client.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Approval_call_that_stays_invalid_fails_the_run()
    {
        var tool = RefundTool(() => { });
        var client = new ScriptedChatClient(
            Call("c-1", """{"amount": 0}"""), Call("c-2", """{"amount": -5}"""), Call("c-3", """{}"""));

        var result = await Runtime(client).RunAsync(Request([tool]), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.FailureReason.ShouldNotBeNull().ShouldContain("tool arguments do not match the tool schema after 2 correction(s)");
        result.PendingApprovals.ShouldBeEmpty();
        client.Calls.ShouldBe(3);
    }

    private static AgentToolDefinition RefundTool(Action onRun) =>
        new("issue_refund", "Refund an order.", RefundParameters, (_, _) =>
        {
            onRun();
            return Task.FromResult(new AgentToolResult(AgentToolCallStatus.Completed, "{}"));
        }, RequiresApproval: true)
        {
            ValidateArguments = args => AgentSchemaCheck.Describe(Validator, RefundParameters, args)
        };

    private static EffectiveAgentPolicy Policy() => EffectiveAgentPolicy.Resolve(new AgentPolicy(), null, new AiAgentOptions());

    private static AgentRunRequest Request(IReadOnlyList<AgentToolDefinition> tools) => new(
        Guid.NewGuid(), "intake", "scripted",
        new AgentProviderProfile { Kind = AgentProviderKinds.OpenAiCompatible, Model = "scripted" },
        "Extract the loan request.", "50 bin TL araba kredisi.", OutputSchema, null, tools,
        Policy(), null, null, false, 40, CaptureTranscript: false);

    private static MafAgentRuntime Runtime(IChatClient client) =>
        new(new FixedProvider(client), Validator, NullLoggerFactory.Instance);

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse Call(string callId, string arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, "issue_refund",
            JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments))]));

    private sealed class FixedProvider(IChatClient client) : IChatClientProvider
    {
        public Task<IChatClient> CreateAsync(string providerName, AgentProviderProfile profile, EffectiveAgentPolicy policy, CancellationToken cancellationToken) =>
            Task.FromResult(client);
    }

    /// <summary>Answers each model call with the next scripted response.</summary>
    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int Calls { get; private set; }

        public string? LastUserText { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            if (_responses.Count == 0)
                throw new InvalidOperationException("no scripted response left");
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
