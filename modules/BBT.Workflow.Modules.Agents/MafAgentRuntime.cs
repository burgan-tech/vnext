using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Validation;
using AgentSession = Microsoft.Agents.AI.AgentSession;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Agents;

/// <summary>
/// Microsoft Agent Framework runtime (<c>maf</c>, issue #1105). One <see cref="ChatClientAgent"/> per
/// run over the provider client, wrapped as: function invocation (iteration ceiling) → OpenTelemetry
/// (<c>gen_ai.*</c> spans per model call) → <see cref="RetryingChatClient"/> → provider. Structured output
/// uses the provider's JSON-schema mode, or JSON mode with the schema in the instructions when the
/// profile says <c>structuredOutput: json</c>. Local sessions are MAF-serialized and trimmed to
/// <c>maxMessages</c>; provider sessions keep only the conversation id. No streaming, no harness.
/// </summary>
internal sealed class MafAgentRuntime(IChatClientProvider provider, IJsonSchemaValidator schemaValidator, ILoggerFactory loggerFactory) : IAgentRuntime
{
    /// <summary>Turns that deny held calls with invalid arguments before the run gives up.</summary>
    internal const int MaxArgumentCorrections = 2;

    /// <summary>Turns that send an output that does not match the schema back to the model.</summary>
    internal const int MaxOutputRepairs = 1;

    internal static readonly ActivitySource ActivitySource = new(BBT.Workflow.Logging.TelemetryConstants.ActivitySources.Agents);

    /// <inheritdoc />
    public string Key => AgentRuntimeKeys.Maf;

    /// <inheritdoc />
    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var policy = request.Policy;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(policy.TimeoutSeconds));

        RetryingChatClient? retrying = null;
        try
        {
            var baseClient = await provider.CreateAsync(request.ProviderName, request.Profile, policy, deadline.Token);
            retrying = new RetryingChatClient(baseClient, policy);
            var client = new ChatClientBuilder(retrying)
                .UseFunctionInvocation(loggerFactory, f =>
                {
                    f.MaximumIterationsPerRequest = Math.Max(1, policy.MaxIterations);
                    f.MaximumConsecutiveErrorsPerRequest = 2;
                    f.IncludeDetailedErrors = false;
                })
                .UseOpenTelemetry(loggerFactory, ActivitySource.Name)
                .Build();

            var jsonMode = string.Equals(request.Profile.StructuredOutput, "json", StringComparison.OrdinalIgnoreCase);
            var instructions = jsonMode
                ? $"{request.Instructions}\n\nRespond only with a JSON object that matches this JSON schema:\n{request.OutputSchema.GetRawText()}"
                : request.Instructions;

            var chatOptions = new ChatOptions
            {
                Instructions = instructions,
                Temperature = (float?)request.Model?.Temperature,
                TopP = (float?)request.Model?.TopP,
                MaxOutputTokens = request.Model?.MaxOutputTokens,
                ResponseFormat = jsonMode
                    ? ChatResponseFormat.Json
                    : ChatResponseFormat.ForJsonSchema(request.OutputSchema, "agent_output", "Structured output of the agent"),
                Tools = request.Tools
                    .Select(t => t.RequiresApproval ? new ApprovalRequiredAIFunction(new AgentToolFunction(t)) : (AITool)new AgentToolFunction(t))
                    .ToList()
            };
            if (request.Tools.Count == 0)
                chatOptions.Tools = null;

            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Name = request.AgentKey,
                ChatOptions = chatOptions,
                UseProvidedChatClientAsIs = true
            }, loggerFactory);

            var session = await LoadSessionAsync(agent, request, deadline.Token);
            var input = request.ApprovalAnswers is { Count: > 0 } answers
                ? [new ChatMessage(ChatRole.User, answers.Select(a => (AIContent)new ToolApprovalRequestContent(
                        a.Pending.RequestId,
                        new FunctionCallContent(a.Pending.CallId, a.Pending.ToolName, ToArguments(a.Pending.Arguments)))
                    .CreateResponse(a.Approved, a.Reason)).ToList())]
                : (request.SeedMessages ?? [])
                    .Select(m => new ChatMessage(new ChatRole(m.Role), m.Content))
                    .Append(new ChatMessage(ChatRole.User, request.UserPrompt))
                    .ToList();

            var sent = new List<ChatMessage>(input);
            var received = new List<ChatMessage>();
            int promptTokens = 0, completionTokens = 0, iterations = 0;
            int argumentCorrections = 0, outputRepairs = 0;
            AgentResponse? response = null;
            List<ToolApprovalRequestContent> held = [];
            JsonElement output = default;
            string? outputErrors = null;
            string? argumentErrors = null;

            // One agent turn, plus correction turns on the same session: a call held for approval whose
            // arguments do not match the tool schema is denied with the errors (nobody approves a call that
            // would be rejected), and an output that does not match the output schema is sent back once.
            while (true)
            {
                response = await agent.RunAsync(input, session, null, deadline.Token);
                received.AddRange(response.Messages);
                promptTokens += (int)(response.Usage?.InputTokenCount ?? 0);
                completionTokens += (int)(response.Usage?.OutputTokenCount ?? 0);
                iterations += Math.Max(1, response.Messages.Count(m => m.Role == ChatRole.Assistant));

                held = response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
                if (held.Count > 0)
                {
                    var invalid = held
                        .Where(r => r.RequiresConfirmation)
                        .Select(r => (Request: r, Errors: ArgumentErrors(request.Tools, r)))
                        .Where(x => x.Errors is not null)
                        .ToDictionary(x => x.Request.RequestId, x => x.Errors!, StringComparer.Ordinal);
                    if (invalid.Count == 0)
                    {
                        argumentErrors = null;
                        break;
                    }

                    argumentErrors = string.Join("; ", invalid.Select(kv => $"{NameOf(held.First(r => r.RequestId == kv.Key))}: {kv.Value}"));
                    if (argumentCorrections >= MaxArgumentCorrections)
                        break;
                    argumentCorrections++;
                    input = [new ChatMessage(ChatRole.User, held.Select(r => CorrectionAnswer(r, invalid)).ToList())];
                    sent.AddRange(input);
                    continue;
                }

                argumentErrors = null;
                outputErrors = TryParseObject(response.Text, out output)
                    ? AgentSchemaCheck.Describe(schemaValidator, request.OutputSchema, output)
                    : "the answer is not a JSON object";
                if (outputErrors is null || outputRepairs >= MaxOutputRepairs)
                    break;

                outputRepairs++;
                input = [new ChatMessage(ChatRole.User,
                    $"Your answer does not match the required output schema: {outputErrors}. " +
                    "Answer again with only a JSON object that matches the schema. Use null for a value you do not know " +
                    "when the schema allows it; do not invent values.")];
                sent.AddRange(input);
            }

            var (sessionBytes, messageCount, trimmed, conversationId) =
                await SaveSessionAsync(agent, session, request, retrying, deadline.Token);

            var result = new AgentRunResult
            {
                Status = AgentRunStatus.Completed,
                Iterations = iterations,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                Model = response.Messages.LastOrDefault()?.AdditionalProperties?.TryGetValue("model", out var m) == true ? m?.ToString() : request.Profile.Model,
                ProviderResponseId = response.ResponseId,
                FinishReason = response.FinishReason?.Value,
                Attempts = (short)Math.Max(1, retrying.Attempts),
                Session = sessionBytes,
                SessionMessageCount = messageCount,
                SessionMessagesTrimmed = trimmed,
                ProviderConversationId = conversationId,
                Transcript = request.CaptureTranscript
                    ? JsonSerializer.Serialize(request.FullTranscript ? sent.Concat(received).ToList() : received, AIJsonUtilities.DefaultOptions)
                    : null
            };

            if (argumentErrors is not null)
            {
                return result with
                {
                    Status = AgentRunStatus.Failed,
                    FailureReason = Truncate($"tool arguments do not match the tool schema after {argumentCorrections} correction(s): {argumentErrors}", 1000)
                };
            }

            if (held.Count > 0)
            {
                var pending = held
                    .Select(r => new AgentPendingApproval(
                        r.RequestId,
                        r.ToolCall.CallId,
                        NameOf(r),
                        JsonSerializer.SerializeToElement((r.ToolCall as FunctionCallContent)?.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions),
                        r.RequiresConfirmation))
                    .ToList();
                // The conversation stops before any held call runs; the session (saved above) resumes it.
                return result with
                {
                    Status = AgentRunStatus.ApprovalRequested,
                    FailureReason = $"{pending.Count(p => p.RequiresConfirmation)} tool call(s) wait for approval",
                    PendingApprovals = pending
                };
            }

            if (policy.MaxTokens > 0 && promptTokens + completionTokens > policy.MaxTokens)
            {
                return result with
                {
                    Status = AgentRunStatus.BudgetExceeded,
                    FailureReason = $"token budget exceeded ({promptTokens + completionTokens} > {policy.MaxTokens})"
                };
            }

            if (outputErrors is not null)
            {
                return result with
                {
                    Status = AgentRunStatus.Failed,
                    FailureReason = Truncate($"the output does not match the output schema after {outputRepairs} repair(s): {outputErrors}", 1000)
                };
            }

            return result with { Output = output };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AgentRunResult.Failed(AgentRunStatus.Timeout, $"run exceeded {policy.TimeoutSeconds}s", (short)(retrying?.Attempts ?? 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentRunResult.Failed(AgentRunStatus.Failed, $"provider error: {Truncate(ex.Message, 300)}", (short)(retrying?.Attempts ?? 0));
        }
    }

    private static async Task<AgentSession> LoadSessionAsync(ChatClientAgent agent, AgentRunRequest request, CancellationToken cancellationToken)
    {
        if (request.UseProviderSession && !string.IsNullOrEmpty(request.ProviderConversationId))
            return await agent.CreateSessionAsync(request.ProviderConversationId, cancellationToken);

        if (!request.UseProviderSession && request.Session is { Length: > 0 } bytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                return await agent.DeserializeSessionAsync(doc.RootElement.Clone(), null, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) // skipcq: CS-R1008 — filtered to the three failures of an unreadable session
            {
                // An unreadable session starts over; the step logs the reset from the message count.
            }
        }

        return await agent.CreateSessionAsync(cancellationToken);
    }

    private static async Task<(byte[]? Bytes, int Count, int Trimmed, string? ConversationId)> SaveSessionAsync(
        ChatClientAgent agent, AgentSession session, AgentRunRequest request, IChatClient summarizer, CancellationToken cancellationToken)
    {
        if (request.UseProviderSession)
            return (null, 0, 0, (session as ChatClientAgentSession)?.ConversationId);

        var trimmed = 0;
        var count = 0;
        if (session.TryGetInMemoryChatHistory(out var history) && history is not null)
        {
            if (request.Compaction == AgentMemoryCompaction.Summarize && request.MaxMessages > 0 && history.Count > request.MaxMessages)
            {
                // One provider call replaces the oldest messages with a summary message; the reducer keeps
                // tool exchanges intact at the boundary.
                var before = history.Count;
                var reduced = (await new SummarizingChatReducer(summarizer, request.MaxMessages, 0)
                    .ReduceAsync(history, cancellationToken)).ToList();
                trimmed = Math.Max(0, before - reduced.Count);
                history = reduced;
                session.SetInMemoryChatHistory(history);
            }
            else if (request.MaxMessages > 0 && history.Count > request.MaxMessages)
            {
                var keep = history.Skip(history.Count - request.MaxMessages).ToList();
                // Never start the kept window with a tool result or a dangling tool call.
                while (keep.Count > 0 && (keep[0].Role == ChatRole.Tool ||
                                          keep[0].Contents.OfType<FunctionCallContent>().Any()))
                {
                    keep.RemoveAt(0);
                }

                trimmed = history.Count - keep.Count;
                history = keep;
                session.SetInMemoryChatHistory(history);
            }

            count = history.Count;
        }

        var element = await agent.SerializeSessionAsync(session, null, cancellationToken);
        return (Encoding.UTF8.GetBytes(element.GetRawText()), count, trimmed, null);
    }

    private static string NameOf(ToolApprovalRequestContent request) =>
        (request.ToolCall as FunctionCallContent)?.Name ?? string.Empty;

    /// <summary>
    /// The answer to a held call in a correction turn: a call with invalid arguments is denied with its errors, another
    /// call that needs confirmation is denied so the model calls it again, and a call that needs none runs.
    /// </summary>
    private static AIContent CorrectionAnswer(ToolApprovalRequestContent request, IReadOnlyDictionary<string, string> invalid)
    {
        if (invalid.TryGetValue(request.RequestId, out var errors))
            return request.CreateResponse(false, $"invalid arguments, the call did not run: {errors}. Call the tool again with arguments that match its schema.");
        if (request.RequiresConfirmation)
            return request.CreateResponse(false, "not run: another call in this turn had invalid arguments. Call the tool again if you still need it.");
        return request.CreateResponse(true);
    }

    /// <summary>The argument errors of a held call, or null when they match (or the tool has no check).</summary>
    private static string? ArgumentErrors(IReadOnlyList<AgentToolDefinition> tools, ToolApprovalRequestContent request)
    {
        var tool = tools.FirstOrDefault(t => t.Name == NameOf(request));
        if (tool?.ValidateArguments is not { } validate)
            return null;
        var arguments = JsonSerializer.SerializeToElement(
            (request.ToolCall as FunctionCallContent)?.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions);
        return validate(arguments);
    }

    private static IDictionary<string, object?> ToArguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
            : new Dictionary<string, object?>();

    private static bool TryParseObject(string? text, out JsonElement output)
    {
        output = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine > 0 && lastFence > firstNewLine)
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            output = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
