using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;
using Polly;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Creates Polly retry policies from workflow RetryPolicy definitions.
/// Supports exponential and fixed backoff strategies with optional jitter.
/// </summary>
public sealed class PollyRetryPolicyFactory : IRetryPolicyFactory
{
    private readonly ILogger<PollyRetryPolicyFactory> _logger;

    /// <summary>
    /// Initializes a new instance of the PollyRetryPolicyFactory.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public PollyRetryPolicyFactory(ILogger<PollyRetryPolicyFactory> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IAsyncPolicy<T> CreateAsyncPolicy<T>(
        RetryPolicy policy,
        Func<T, bool> shouldRetry,
        Func<DelegateResult<T>, TimeSpan, int, Context, Task>? onRetry = null)
    {
        if (policy.MaxRetries <= 0)
        {
            _logger.LogDebug("MaxRetries is 0, creating no-retry policy");
            return CreateNoRetryPolicy<T>();
        }

        var sleepDurationProvider = CreateSleepDurationProvider(policy);

        var policyBuilder = Policy<T>
            .HandleResult(shouldRetry);

        return policyBuilder.WaitAndRetryAsync(
            policy.MaxRetries,
            sleepDurationProvider,
            onRetryAsync: onRetry ?? DefaultOnRetry<T>);
    }

    /// <inheritdoc />
    public IAsyncPolicy<T> CreateAsyncPolicy<T>(
        RetryPolicy policy,
        Func<T, bool> shouldRetryOnResult,
        Func<Exception, bool> shouldRetryOnException,
        Func<DelegateResult<T>, TimeSpan, int, Context, Task>? onRetry = null)
    {
        if (policy.MaxRetries <= 0)
        {
            _logger.LogDebug("MaxRetries is 0, creating no-retry policy");
            return CreateNoRetryPolicy<T>();
        }

        var sleepDurationProvider = CreateSleepDurationProvider(policy);

        var policyBuilder = Policy<T>
            .Handle(shouldRetryOnException)
            .OrResult(shouldRetryOnResult);

        return policyBuilder.WaitAndRetryAsync(
            policy.MaxRetries,
            sleepDurationProvider,
            onRetryAsync: onRetry ?? DefaultOnRetry<T>);
    }

    /// <inheritdoc />
    public IAsyncPolicy<T> CreateNoRetryPolicy<T>()
    {
        return Policy.NoOpAsync<T>();
    }

    /// <summary>
    /// Creates a sleep duration provider based on the backoff type.
    /// </summary>
    private Func<int, TimeSpan> CreateSleepDurationProvider(RetryPolicy policy)
    {
        return policy.BackoffType switch
        {
            BackoffType.Exponential => attempt =>
            {
                var delay = CalculateExponentialDelay(policy.InitialDelay, attempt, policy.BackoffMultiplier);
                delay = ApplyJitterIfEnabled(delay, policy.UseJitter);
                return ClampDelay(delay, policy.MaxDelay);
            },
            BackoffType.Fixed or _ => _ =>
            {
                var delay = ApplyJitterIfEnabled(policy.InitialDelay, policy.UseJitter);
                return ClampDelay(delay, policy.MaxDelay);
            }
        };
    }

    /// <summary>
    /// Calculates exponential backoff delay.
    /// </summary>
    private static TimeSpan CalculateExponentialDelay(TimeSpan baseDelay, int attempt, double multiplier)
    {
        var factor = Math.Pow(multiplier, attempt - 1);
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    /// <summary>
    /// Applies jitter to the delay to prevent thundering herd.
    /// Uses +/- 25% jitter when enabled.
    /// </summary>
    private static TimeSpan ApplyJitterIfEnabled(TimeSpan delay, bool useJitter)
    {
        if (!useJitter)
            return delay;

        const double jitterFactor = 0.25;
        var jitter = delay.TotalMilliseconds * jitterFactor * (Random.Shared.NextDouble() * 2 - 1);
        var adjustedDelay = delay.TotalMilliseconds + jitter;
        return TimeSpan.FromMilliseconds(Math.Max(0, adjustedDelay));
    }

    /// <summary>
    /// Clamps the delay to the maximum allowed.
    /// </summary>
    private static TimeSpan ClampDelay(TimeSpan delay, TimeSpan maxDelay)
    {
        return delay > maxDelay ? maxDelay : delay;
    }

    /// <summary>
    /// Default no-op retry callback.
    /// </summary>
    private static Task DefaultOnRetry<T>(DelegateResult<T> outcome, TimeSpan delay, int attempt, Context context)
    {
        return Task.CompletedTask;
    }
}
