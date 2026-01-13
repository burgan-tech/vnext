using BBT.Workflow.Definitions;
using Polly;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Factory for creating Polly retry policies from workflow RetryPolicy definitions.
/// Single responsibility: convert RetryPolicy configuration to executable Polly policies.
/// </summary>
public interface IRetryPolicyFactory
{
    /// <summary>
    /// Creates an async Polly policy from a RetryPolicy definition.
    /// </summary>
    /// <typeparam name="T">The result type of the operation to retry.</typeparam>
    /// <param name="policy">The retry policy configuration.</param>
    /// <param name="shouldRetry">Predicate to determine if result should trigger retry.</param>
    /// <param name="onRetry">Optional callback invoked on each retry attempt.</param>
    /// <returns>A configured Polly async policy.</returns>
    IAsyncPolicy<T> CreateAsyncPolicy<T>(
        RetryPolicy policy,
        Func<T, bool> shouldRetry,
        Func<DelegateResult<T>, TimeSpan, int, Context, Task>? onRetry = null);

    /// <summary>
    /// Creates an async Polly policy from a RetryPolicy definition with exception handling.
    /// </summary>
    /// <typeparam name="T">The result type of the operation to retry.</typeparam>
    /// <param name="policy">The retry policy configuration.</param>
    /// <param name="shouldRetryOnResult">Predicate to determine if result should trigger retry.</param>
    /// <param name="shouldRetryOnException">Predicate to determine if exception should trigger retry.</param>
    /// <param name="onRetry">Optional callback invoked on each retry attempt.</param>
    /// <returns>A configured Polly async policy.</returns>
    IAsyncPolicy<T> CreateAsyncPolicy<T>(
        RetryPolicy policy,
        Func<T, bool> shouldRetryOnResult,
        Func<Exception, bool> shouldRetryOnException,
        Func<DelegateResult<T>, TimeSpan, int, Context, Task>? onRetry = null);

    /// <summary>
    /// Creates a default retry policy with no retries (single attempt).
    /// Used when no retry policy is configured.
    /// </summary>
    /// <typeparam name="T">The result type of the operation.</typeparam>
    /// <returns>A no-op policy that executes the operation once.</returns>
    IAsyncPolicy<T> CreateNoRetryPolicy<T>();
}

