using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace BBT.Workflow.Remote;

/// <summary>
/// Builds the resilience policies shared by every remote transport.
/// </summary>
/// <remarks>
/// <para>
/// One source for two consumers. The HTTP transport applies these through the
/// <c>IHttpClientFactory</c> pipeline (<c>AddPolicyHandler</c>); the Dapr transport applies the
/// same policies programmatically around <c>DaprClient</c>, which is not an <c>HttpClient</c> and
/// has no handler pipeline to hook. Keeping both on this factory is what guarantees a mutating
/// call is attempted exactly once regardless of which transport carried it.
/// </para>
/// <para>
/// Ordering is outermost-first — Timeout, then (Read only) Retry, then Circuit Breaker — and is
/// the same whether expressed as handler registration order or as <see cref="Policy.WrapAsync{TResult}(IAsyncPolicy{TResult}[])"/>.
/// The circuit breaker is stateful: <see cref="Compose"/> creates it once per call, so callers
/// must build one composed policy per transport instance and reuse it, never per request.
/// </para>
/// </remarks>
public static class RemotePolicyFactory
{
    /// <summary>Pessimistic timeout bounding the whole attempt sequence.</summary>
    public static IAsyncPolicy<HttpResponseMessage> Timeout(RemoteOptions options) =>
        Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            TimeoutStrategy.Pessimistic);

    /// <summary>Exponential-backoff retry on transient HTTP failures and timeouts.</summary>
    public static IAsyncPolicy<HttpResponseMessage> Retry(RemoteOptions options) =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(
                retryCount: options.MaxRetryAttempts,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(
                    options.RetryDelayMilliseconds * Math.Pow(2, retryAttempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    var logger = GetLogger(context);
                    logger?.LogWarning(
                        "Retry attempt {RetryCount} for {OperationKey} after {Delay}ms. Reason: {Exception}",
                        retryCount,
                        context.OperationKey,
                        timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase ?? "Unknown");
                });

    /// <summary>Circuit breaker on consecutive transient failures.</summary>
    public static IAsyncPolicy<HttpResponseMessage> CircuitBreaker(RemoteOptions options) =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.CircuitBreakerFailureThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerTimeoutSeconds),
                onBreak: (exception, duration) =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Circuit breaker opened. Duration: {duration.TotalMilliseconds}ms. " +
                        $"Failure threshold: {options.CircuitBreakerFailureThreshold}. Break duration: {options.CircuitBreakerTimeoutSeconds}s. " +
                        $"Exception: {exception.Exception?.Message ?? exception.Result?.ReasonPhrase ?? "Unknown"}");
                },
                onReset: () => { },
                onHalfOpen: () => { });

    /// <summary>
    /// Whether the profile (and the emergency override) allow transport-level retry.
    /// </summary>
    /// <remarks>
    /// Read clients retry. Mutating clients do not — a duplicate <c>instances/start</c> or
    /// <c>internal/subflow-forward</c> is data corruption — unless
    /// <see cref="RemoteOptions.EnableRetryOnMutating"/> is switched on as an emergency reversal.
    /// </remarks>
    public static bool AllowsRetry(RemoteOptions options, RemoteServiceProfile profile) =>
        profile == RemoteServiceProfile.Read || options.EnableRetryOnMutating;

    /// <summary>
    /// The full policy stack for one transport instance, outermost first:
    /// Timeout → [Retry] → CircuitBreaker.
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> Compose(RemoteOptions options, RemoteServiceProfile profile)
    {
        var policies = new List<IAsyncPolicy<HttpResponseMessage>> { Timeout(options) };
        if (AllowsRetry(options, profile))
        {
            policies.Add(Retry(options));
        }
        policies.Add(CircuitBreaker(options));
        return Policy.WrapAsync(policies.ToArray());
    }

    private static ILogger? GetLogger(Context context) =>
        context.TryGetValue("logger", out var logger) && logger is ILogger log ? log : null;
}
