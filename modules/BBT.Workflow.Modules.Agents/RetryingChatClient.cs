using System.ClientModel;
using System.Net;
using Microsoft.Extensions.AI;

namespace BBT.Workflow.Agents;

/// <summary>
/// Retries provider calls inside one run (<c>policy.retry</c>): HTTP 429, 5xx and timeouts, with
/// exponential backoff. The caller's cancellation is never retried. <see cref="Attempts"/> counts every
/// provider call including retries.
/// </summary>
internal sealed class RetryingChatClient(IChatClient inner, EffectiveAgentPolicy policy) : DelegatingChatClient(inner)
{
    private int _attempts;

    /// <summary>Provider calls made, retries included.</summary>
    public int Attempts => _attempts;

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            Interlocked.Increment(ref _attempts);
            try
            {
                return await base.GetResponseAsync(list, options, cancellationToken);
            }
            catch (Exception ex) when (attempt < policy.RetryMaxAttempts && IsRetryable(ex, cancellationToken))
            {
                var delay = policy.RetryBackoffMs * (1 << (attempt - 1));
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 10_000)), cancellationToken);
            }
        }
    }

    private bool IsRetryable(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        var status = ex switch
        {
            ClientResultException cre => cre.Status,
            HttpRequestException hre => (int?)hre.StatusCode ?? 0,
            _ => 0
        };

        if (status == (int)HttpStatusCode.TooManyRequests)
            return policy.RetryOn.Contains("429");
        if (status >= 500)
            return policy.RetryOn.Contains("5xx");
        if (ex is TaskCanceledException or TimeoutException || (ex is HttpRequestException && status == 0))
            return policy.RetryOn.Contains("timeout");
        return false;
    }
}
