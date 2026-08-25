using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace BBT.Workflow.Application.Resilience;

/// <summary>
/// Factory for creating Result-based resilience pipelines.
/// Provides retry logic for operations that return Result&lt;T&gt;.
/// </summary>
public sealed class ResultResiliencePipelineFactory : IResultResiliencePipelineFactory
{
    private readonly ResultRetryOptions _defaultOptions;
    private readonly ILogger<ResultResiliencePipelineFactory> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResultResiliencePipelineFactory"/> class.
    /// </summary>
    /// <param name="options">The default retry options.</param>
    /// <param name="logger">The logger instance.</param>
    public ResultResiliencePipelineFactory(
        IOptions<ResultRetryOptions> options,
        ILogger<ResultResiliencePipelineFactory> logger)
    {
        _defaultOptions = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ResiliencePipeline<Result<T>> CreatePipeline<T>(
        string operationName,
        ResultRetryOptions? overrideOptions = null)
    {
        var options = overrideOptions ?? _defaultOptions;
        var retryErrorCodes = options.RetryOnErrorCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ResiliencePipelineBuilder<Result<T>>()
            .AddRetry(new RetryStrategyOptions<Result<T>>
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds),
                BackoffType = ParseBackoffType(options.BackoffType),
                UseJitter = options.UseJitter,
                ShouldHandle = new PredicateBuilder<Result<T>>()
                    .HandleResult(result => ShouldRetry(result, retryErrorCodes)),
                OnRetry = args =>
                {
                    var (errorCode, errorMessage) = ExtractErrorInfo(args.Outcome);
                    _logger.LogWarning(
                        "{OperationName} retry attempt {AttemptNumber}/{MaxAttempts} after {Delay}ms. " +
                        "ErrorCode: {ErrorCode}. Reason: {Reason}",
                        operationName,
                        args.AttemptNumber,
                        options.MaxRetryAttempts,
                        args.RetryDelay.TotalMilliseconds,
                        errorCode,
                        errorMessage);

                    // Mark the retry on the active span (Task.Execute / Trigger.Local): the
                    // Polly delay is otherwise an unexplained hole in the trace timeline — the
                    // "dead wait" seen when a DirectTrigger spins on a Busy target instance.
                    Activity.Current?.AddEvent(new ActivityEvent("result.retry", tags: new ActivityTagsCollection
                    {
                        { "operation", operationName },
                        { "retry.attempt", args.AttemptNumber },
                        { "retry.max", options.MaxRetryAttempts },
                        { "retry.delay_ms", (long)args.RetryDelay.TotalMilliseconds },
                        { "error.code", errorCode },
                        { "error.message", errorMessage }
                    }));

                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    public ResiliencePipeline<Result> CreatePipeline(
        string operationName,
        ResultRetryOptions? overrideOptions = null)
    {
        var options = overrideOptions ?? _defaultOptions;
        var retryErrorCodes = options.RetryOnErrorCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ResiliencePipelineBuilder<Result>()
            .AddRetry(new RetryStrategyOptions<Result>
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds),
                BackoffType = ParseBackoffType(options.BackoffType),
                UseJitter = options.UseJitter,
                ShouldHandle = new PredicateBuilder<Result>()
                    .HandleResult(result => ShouldRetryNonGeneric(result, retryErrorCodes)),
                OnRetry = args =>
                {
                    var (errorCode, errorMessage) = ExtractErrorInfoNonGeneric(args.Outcome);
                    _logger.LogWarning(
                        "{OperationName} retry attempt {AttemptNumber}/{MaxAttempts} after {Delay}ms. " +
                        "ErrorCode: {ErrorCode}. Reason: {Reason}",
                        operationName,
                        args.AttemptNumber,
                        options.MaxRetryAttempts,
                        args.RetryDelay.TotalMilliseconds,
                        errorCode,
                        errorMessage);

                    // Mark the retry on the active span (Task.Execute / Trigger.Local): the
                    // Polly delay is otherwise an unexplained hole in the trace timeline — the
                    // "dead wait" seen when a DirectTrigger spins on a Busy target instance.
                    Activity.Current?.AddEvent(new ActivityEvent("result.retry", tags: new ActivityTagsCollection
                    {
                        { "operation", operationName },
                        { "retry.attempt", args.AttemptNumber },
                        { "retry.max", options.MaxRetryAttempts },
                        { "retry.delay_ms", (long)args.RetryDelay.TotalMilliseconds },
                        { "error.code", errorCode },
                        { "error.message", errorMessage }
                    }));

                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    private static bool ShouldRetry<T>(Result<T> result, HashSet<string> retryErrorCodes)
    {
        if (result.IsSuccess) return false;
        var errorCode = result.Error.Code;
        return !string.IsNullOrEmpty(errorCode) && retryErrorCodes.Contains(errorCode);
    }

    private static bool ShouldRetryNonGeneric(Result result, HashSet<string> retryErrorCodes)
    {
        if (result.IsSuccess) return false;
        var errorCode = result.Error.Code;
        return !string.IsNullOrEmpty(errorCode) && retryErrorCodes.Contains(errorCode);
    }

    private static (string ErrorCode, string ErrorMessage) ExtractErrorInfo<T>(Outcome<Result<T>> outcome)
    {
        if (outcome.Exception != null)
        {
            return ("Exception", outcome.Exception.Message);
        }

        var result = outcome.Result;
        if (result.IsSuccess)
        {
            return ("None", "Success");
        }

        return (result.Error.Code ?? "Unknown", result.Error.Message ?? "Transient failure");
    }

    private static (string ErrorCode, string ErrorMessage) ExtractErrorInfoNonGeneric(Outcome<Result> outcome)
    {
        if (outcome.Exception != null)
        {
            return ("Exception", outcome.Exception.Message);
        }

        var result = outcome.Result;
        if (result.IsSuccess)
        {
            return ("None", "Success");
        }

        return (result.Error.Code ?? "Unknown", result.Error.Message ?? "Transient failure");
    }

    private static DelayBackoffType ParseBackoffType(string backoffType)
    {
        return backoffType.ToLowerInvariant() switch
        {
            "linear" => DelayBackoffType.Linear,
            "exponential" => DelayBackoffType.Exponential,
            _ => DelayBackoffType.Constant
        };
    }
}
