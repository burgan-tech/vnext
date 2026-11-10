using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Agents;

namespace BBT.Workflow.Agents;

/// <summary>Effective budgets of one run: environment ceiling ≥ component ≥ state.</summary>
/// <param name="MaxIterations">Model round trips.</param>
/// <param name="MaxToolCalls">Tool calls.</param>
/// <param name="MaxTokens">Total tokens.</param>
/// <param name="TimeoutSeconds">Whole run.</param>
/// <param name="ToolTimeoutSeconds">Per tool.</param>
/// <param name="ResultMaxChars">Tool result characters the model receives.</param>
/// <param name="RetryMaxAttempts">Provider attempts including the first.</param>
/// <param name="RetryBackoffMs">Base backoff.</param>
/// <param name="RetryOn">Retried failures (<c>429</c>, <c>5xx</c>, <c>timeout</c>).</param>
/// <param name="OnProviderError">After retries are exhausted.</param>
/// <param name="MaxEntries">Re-entries before onExhausted; null is unlimited.</param>
public sealed record EffectiveAgentPolicy(
    int MaxIterations,
    int MaxToolCalls,
    int MaxTokens,
    int TimeoutSeconds,
    int ToolTimeoutSeconds,
    int ResultMaxChars,
    int RetryMaxAttempts,
    int RetryBackoffMs,
    IReadOnlySet<string> RetryOn,
    AgentProviderErrorAction OnProviderError,
    int? MaxEntries)
{
    /// <summary>Resolves the effective policy: the component's values narrowed by the state's, capped by the environment.</summary>
    public static EffectiveAgentPolicy Resolve(AgentPolicy? component, AgentPolicy? state, AiAgentOptions ceilings)
    {
        static int Pick(int? c, int? s, int fallback, int ceiling)
        {
            var value = c ?? fallback;
            if (s is { } narrowed && narrowed < value)
                value = narrowed;
            return Math.Max(0, Math.Min(value, ceiling));
        }

        var retry = state?.Retry ?? component?.Retry;
        return new EffectiveAgentPolicy(
            Pick(component?.MaxIterations, state?.MaxIterations, 8, ceilings.MaxIterations),
            Pick(component?.MaxToolCalls, state?.MaxToolCalls, 10, ceilings.MaxToolCalls),
            Pick(component?.MaxTokens, state?.MaxTokens, 30_000, ceilings.MaxTokens),
            Math.Max(1, Pick(component?.TimeoutSeconds, state?.TimeoutSeconds, 90, ceilings.MaxRunSeconds)),
            Math.Max(1, Pick(component?.ToolTimeoutSeconds, state?.ToolTimeoutSeconds, 20, ceilings.MaxRunSeconds)),
            Math.Max(1, Pick(component?.ResultMaxChars, state?.ResultMaxChars, 6000, int.MaxValue)),
            Math.Clamp(retry?.MaxAttempts ?? 2, 1, 5),
            Math.Clamp(retry?.BackoffMs ?? 500, 0, 30_000),
            new HashSet<string>(retry?.On ?? ["429", "5xx", "timeout"], StringComparer.OrdinalIgnoreCase),
            state?.OnProviderError ?? component?.OnProviderError ?? AgentProviderErrorAction.Fallback,
            state?.MaxEntries ?? component?.MaxEntries);
    }
}

/// <summary>A tool call the model made.</summary>
/// <param name="CallId">The model's call id.</param>
/// <param name="Name">Tool name.</param>
/// <param name="Arguments">Arguments object.</param>
/// <param name="Iteration">Model round trip.</param>
public sealed record AgentToolInvocation(string CallId, string Name, JsonElement Arguments, int Iteration);

/// <summary>What a tool returns to the model.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Content">JSON or text content for the model (already truncated).</param>
/// <param name="Error">Failure text.</param>
public sealed record AgentToolResult(AgentToolCallStatus Status, string Content, string? Error = null)
{
    /// <summary>True when the tool completed.</summary>
    public bool IsSuccess => Status == AgentToolCallStatus.Completed;
}

/// <summary>A tool offered to the model.</summary>
/// <param name="Name">Name the model sees.</param>
/// <param name="Description">Description for the model.</param>
/// <param name="ParametersSchema">JSON schema of the arguments.</param>
/// <param name="InvokeAsync">Runs the tool; journaling and budgets are applied by the caller's wrapper.</param>
public sealed record AgentToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> InvokeAsync);

/// <summary>One agent run.</summary>
/// <param name="RunId">Run id.</param>
/// <param name="AgentKey">Agent key (telemetry).</param>
/// <param name="ProviderName">Provider profile name.</param>
/// <param name="Profile">Provider profile.</param>
/// <param name="Instructions">System prompt.</param>
/// <param name="UserPrompt">User prompt of this entry.</param>
/// <param name="OutputSchema">Structured output schema (decision members already added).</param>
/// <param name="Model">Sampling settings.</param>
/// <param name="Tools">Tools offered.</param>
/// <param name="Policy">Budgets.</param>
/// <param name="Session">Session to resume (serialized, local storage).</param>
/// <param name="ProviderConversationId">Provider conversation to resume (provider storage).</param>
/// <param name="UseProviderSession">Whether the provider keeps the conversation.</param>
/// <param name="MaxMessages">Messages kept in a local session.</param>
/// <param name="CaptureTranscript">Whether to return the transcript.</param>
/// <param name="Compaction">How a local session is kept within <paramref name="MaxMessages"/>.</param>
public sealed record AgentRunRequest(
    Guid RunId,
    string AgentKey,
    string ProviderName,
    AgentProviderProfile Profile,
    string Instructions,
    string UserPrompt,
    JsonElement OutputSchema,
    AgentModelSettings? Model,
    IReadOnlyList<AgentToolDefinition> Tools,
    EffectiveAgentPolicy Policy,
    byte[]? Session,
    string? ProviderConversationId,
    bool UseProviderSession,
    int MaxMessages,
    bool CaptureTranscript,
    AgentMemoryCompaction Compaction = AgentMemoryCompaction.Trim);

/// <summary>Outcome of one agent run.</summary>
public sealed record AgentRunResult
{
    /// <summary>Run status.</summary>
    public AgentRunStatus Status { get; init; }

    /// <summary>Structured output (parsed).</summary>
    public JsonElement? Output { get; init; }

    /// <summary>Why the run did not complete.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Model round trips.</summary>
    public int Iterations { get; init; }

    /// <summary>Input tokens.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Output tokens.</summary>
    public int CompletionTokens { get; init; }

    /// <summary>Model the provider reported.</summary>
    public string? Model { get; init; }

    /// <summary>Provider response id.</summary>
    public string? ProviderResponseId { get; init; }

    /// <summary>Finish reason.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Provider attempts.</summary>
    public short Attempts { get; init; } = 1;

    /// <summary>Updated serialized session (local storage).</summary>
    public byte[]? Session { get; init; }

    /// <summary>Messages in <see cref="Session"/>.</summary>
    public int SessionMessageCount { get; init; }

    /// <summary>Messages trimmed from the session.</summary>
    public int SessionMessagesTrimmed { get; init; }

    /// <summary>Provider conversation id (provider storage).</summary>
    public string? ProviderConversationId { get; init; }

    /// <summary>Transcript JSON, when requested.</summary>
    public string? Transcript { get; init; }

    /// <summary>A failed run.</summary>
    public static AgentRunResult Failed(AgentRunStatus status, string reason, short attempts = 0) =>
        new() { Status = status, FailureReason = reason, Attempts = attempts };
}

/// <summary>An agent runtime (keyed by <see cref="Agent.Runtime"/>).</summary>
public interface IAgentRuntime
{
    /// <summary>Runtime key.</summary>
    string Key { get; }

    /// <summary>Runs the agent to a final answer or a stop.</summary>
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

/// <summary>Runtime used when the agents module is not loaded: every run fails, the state takes its fallback.</summary>
public sealed class UnavailableAgentRuntime : IAgentRuntime
{
    /// <inheritdoc />
    public string Key => AgentRuntimeKeys.Maf;

    /// <inheritdoc />
    public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(AgentRunResult.Failed(AgentRunStatus.Failed, "Agent runtime is not available on this host (agents module not loaded)."));
}
