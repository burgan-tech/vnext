namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for a Dapr Conversation (AI/LLM) task invocation.
/// Holds plain, serializable data only; the invoker maps it onto the Dapr.AI conversation model.
/// </summary>
public sealed class DaprConversationBinding
{
    /// <summary>
    /// The Dapr conversation component name (the configured LLM provider) to invoke.
    /// </summary>
    public required string ComponentName { get; init; }

    /// <summary>
    /// Ordered conversation messages to send to the provider.
    /// </summary>
    public required IReadOnlyList<ConversationMessageBinding> Inputs { get; init; }

    /// <summary>
    /// Optional context identifier used to continue a stateful conversation.
    /// </summary>
    public string? ContextId { get; init; }

    /// <summary>
    /// Optional sampling temperature.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// When true, requests the provider scrub PII from prompts and responses.
    /// </summary>
    public bool? ScrubPII { get; init; }

    /// <summary>
    /// Dapr component metadata forwarded with the request.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Provider-specific parameters (e.g. model, maxTokens) forwarded with the request.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

/// <summary>
/// A single conversation message within a <see cref="DaprConversationBinding"/>.
/// </summary>
public sealed record ConversationMessageBinding
{
    /// <summary>
    /// The message role: user, system, assistant, developer or tool.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// The textual content of the message.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional author name for the message.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// When true, requests PII scrubbing for this message.
    /// </summary>
    public bool? ScrubPII { get; init; }
}
