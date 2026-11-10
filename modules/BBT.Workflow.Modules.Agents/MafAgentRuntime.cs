using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Agents;
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
internal sealed class MafAgentRuntime(IChatClientProvider provider, ILoggerFactory loggerFactory) : IAgentRuntime
{
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
                Tools = request.Tools.Select(t => (AITool)new AgentToolFunction(t)).ToList()
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
            var response = await agent.RunAsync(request.UserPrompt, session, null, deadline.Token);

            var promptTokens = (int)(response.Usage?.InputTokenCount ?? 0);
            var completionTokens = (int)(response.Usage?.OutputTokenCount ?? 0);
            var iterations = Math.Max(1, response.Messages.Count(m => m.Role == ChatRole.Assistant));

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
                    ? JsonSerializer.Serialize(response.Messages, AIJsonUtilities.DefaultOptions)
                    : null
            };

            if (policy.MaxTokens > 0 && promptTokens + completionTokens > policy.MaxTokens)
            {
                return result with
                {
                    Status = AgentRunStatus.BudgetExceeded,
                    FailureReason = $"token budget exceeded ({promptTokens + completionTokens} > {policy.MaxTokens})"
                };
            }

            if (!TryParseObject(response.Text, out var output))
            {
                return result with
                {
                    Status = AgentRunStatus.Failed,
                    FailureReason = "the model did not return a JSON object matching the output schema"
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
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
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
