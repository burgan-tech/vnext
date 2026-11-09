using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;

namespace BBT.Workflow.Agents;

/// <summary>Creates the provider chat client of a run.</summary>
internal interface IChatClientProvider
{
    /// <summary>Returns the chat client for a provider profile.</summary>
    Task<IChatClient> CreateAsync(string providerName, AgentProviderProfile profile, EffectiveAgentPolicy policy, CancellationToken cancellationToken);
}

/// <summary>
/// Provider factory: <c>openai-compatible</c> endpoints through the OpenAI SDK (SDK retries disabled:
/// <see cref="RetryingChatClient"/> owns retry; network timeout = run deadline), <c>local</c> through
/// <see cref="LocalChatClient"/>. Clients are reused per profile, key and timeout.
/// </summary>
internal sealed class ChatClientProvider(ISecretResolver secretResolver) : IChatClientProvider
{
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<IChatClient> CreateAsync(string providerName, AgentProviderProfile profile, EffectiveAgentPolicy policy, CancellationToken cancellationToken)
    {
        if (profile.Kind == AgentProviderKinds.Local)
            return new LocalChatClient(profile.Model ?? "local");

        var apiKey = await secretResolver.ResolveAsync(profile.ApiKeySecretRef, cancellationToken) ?? "unused";
        var cacheKey = $"{providerName}|{profile.Endpoint}|{profile.Model}|{policy.TimeoutSeconds}|{apiKey.GetHashCode()}";

        return _clients.GetOrAdd(cacheKey, _ =>
        {
            var options = new OpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(policy.TimeoutSeconds),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
            };
            if (!string.IsNullOrWhiteSpace(profile.Endpoint))
                options.Endpoint = new Uri(profile.Endpoint);

            var chat = new OpenAI.Chat.ChatClient(profile.Model, new ApiKeyCredential(apiKey), options);
            return chat.AsIChatClient();
        });
    }
}
