using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Resilience;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

using BackoffType = BBT.Workflow.Definitions.BackoffType;
using ErrorBoundaryRetryPolicy = BBT.Workflow.Definitions.RetryPolicy;

namespace BBT.Workflow.Tasks.Resilience;

/// <summary>
/// Factory for creating Polly resilience pipelines for task execution.
/// Integrates with ErrorBoundary RetryPolicy and supports StatusCode-based retries.
/// </summary>
public sealed class TaskResiliencePipelineFactory : ITaskResiliencePipelineFactory
{
    private readonly TaskRetryOptions _defaultOptions;
    private readonly IWorkflowMetrics _workflowMetrics;
    private readonly ILogger<TaskResiliencePipelineFactory> _logger;

    /// <summary>
    /// Initializes a new instance of TaskResiliencePipelineFactory.
    /// </summary>
    public TaskResiliencePipelineFactory(
        IOptions<TaskRetryOptions> options,
        IWorkflowMetrics workflowMetrics,
        ILogger<TaskResiliencePipelineFactory> logger)
    {
        _defaultOptions = options.Value;
        _workflowMetrics = workflowMetrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public ResiliencePipeline<Result<TaskInvocationResult>> CreatePipeline(
        string taskKey,
        ErrorBoundaryRetryPolicy? errorBoundaryRetryPolicy = null)
    {
        // If no retry policy provided, return no-op pipeline (no retries)
        if (errorBoundaryRetryPolicy == null)
        {
            return new ResiliencePipelineBuilder<Result<TaskInvocationResult>>().Build();
        }

        var effectiveOptions = MergeOptions(errorBoundaryRetryPolicy);
        
        // NO default retryable codes - developer must explicitly define them in ErrorBoundary
        var retryableStatusCodes = new HashSet<int>();
        var retryableErrorCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new ResiliencePipelineBuilder<Result<TaskInvocationResult>>()
            .AddRetry(new RetryStrategyOptions<Result<TaskInvocationResult>>
            {
                MaxRetryAttempts = effectiveOptions.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(effectiveOptions.RetryDelayMilliseconds),
                MaxDelay = TimeSpan.FromMilliseconds(effectiveOptions.MaxRetryDelayMilliseconds),
                BackoffType = ParseBackoffType(effectiveOptions.BackoffType),
                UseJitter = effectiveOptions.UseJitter,
                ShouldHandle = new PredicateBuilder<Result<TaskInvocationResult>>()
                    .HandleResult(result => ShouldRetry(result, retryableStatusCodes, retryableErrorCodes)),
                OnRetry = args =>
                {
                    LogRetryAttempt(taskKey, args, effectiveOptions.MaxRetryAttempts);
                    RecordRetryMetric(taskKey);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    public ResiliencePipeline<Result> CreateResultPipeline(
        string taskKey,
        ErrorBoundaryRetryPolicy? errorBoundaryRetryPolicy = null)
    {
        // If no retry policy provided, return no-op pipeline (no retries)
        if (errorBoundaryRetryPolicy == null)
        {
            return new ResiliencePipelineBuilder<Result>().Build();
        }

        var effectiveOptions = MergeOptions(errorBoundaryRetryPolicy);
        
        // NO default retryable codes - developer must explicitly define them in ErrorBoundary
        var retryableErrorCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new ResiliencePipelineBuilder<Result>()
            .AddRetry(new RetryStrategyOptions<Result>
            {
                MaxRetryAttempts = effectiveOptions.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(effectiveOptions.RetryDelayMilliseconds),
                MaxDelay = TimeSpan.FromMilliseconds(effectiveOptions.MaxRetryDelayMilliseconds),
                BackoffType = ParseBackoffType(effectiveOptions.BackoffType),
                UseJitter = effectiveOptions.UseJitter,
                ShouldHandle = new PredicateBuilder<Result>()
                    .HandleResult(result => ShouldRetryResult(result, retryableErrorCodes)),
                OnRetry = args =>
                {
                    LogRetryAttemptResult(taskKey, args, effectiveOptions.MaxRetryAttempts);
                    RecordRetryMetric(taskKey);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    public ResiliencePipeline<Result<StandardTaskResponse>> CreateStandardResponsePipeline(
        string taskKey,
        ErrorBoundaryRetryPolicy? errorBoundaryRetryPolicy = null)
    {
        // If no retry policy provided, return no-op pipeline (no retries)
        if (errorBoundaryRetryPolicy == null)
        {
            return new ResiliencePipelineBuilder<Result<StandardTaskResponse>>().Build();
        }

        var effectiveOptions = MergeOptions(errorBoundaryRetryPolicy);
        
        // NO default retryable codes - developer must explicitly define them in ErrorBoundary
        var retryableStatusCodes = new HashSet<int>();
        var retryableErrorCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new ResiliencePipelineBuilder<Result<StandardTaskResponse>>()
            .AddRetry(new RetryStrategyOptions<Result<StandardTaskResponse>>
            {
                MaxRetryAttempts = effectiveOptions.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(effectiveOptions.RetryDelayMilliseconds),
                MaxDelay = TimeSpan.FromMilliseconds(effectiveOptions.MaxRetryDelayMilliseconds),
                BackoffType = ParseBackoffType(effectiveOptions.BackoffType),
                UseJitter = effectiveOptions.UseJitter,
                ShouldHandle = new PredicateBuilder<Result<StandardTaskResponse>>()
                    .HandleResult(result => ShouldRetryStandardResponse(result, retryableStatusCodes, retryableErrorCodes)),
                OnRetry = args =>
                {
                    LogRetryAttemptStandardResponse(taskKey, args, effectiveOptions.MaxRetryAttempts);
                    RecordRetryMetric(taskKey);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    /// <summary>
    /// Creates a retry pipeline based ONLY on ErrorHandlerRule definition.
    /// NO technical defaults - developer must explicitly define what should be retried.
    /// </summary>
    public ResiliencePipeline<Result<StandardTaskResponse>> CreateStandardResponsePipelineFromRule(
        string taskKey,
        ErrorHandlerRule? retryRule)
    {
        // If no retry rule, return a no-op pipeline (no retries)
        if (retryRule == null || retryRule.RetryPolicy == null)
        {
            return new ResiliencePipelineBuilder<Result<StandardTaskResponse>>().Build();
        }

        var policy = retryRule.RetryPolicy;
        
        // Extract retryable codes from the rule (ONLY from user definition)
        var retryableCodes = ExtractRetryableCodesFromRule(retryRule);
        var retryableErrorTypes = ExtractRetryableTypesFromRule(retryRule);

        _logger.LogDebug(
            "Creating retry pipeline for task {TaskKey} with codes: [{Codes}], types: [{Types}]",
            taskKey,
            string.Join(", ", retryableCodes),
            string.Join(", ", retryableErrorTypes));

        return new ResiliencePipelineBuilder<Result<StandardTaskResponse>>()
            .AddRetry(new RetryStrategyOptions<Result<StandardTaskResponse>>
            {
                MaxRetryAttempts = policy.MaxRetries,
                Delay = policy.InitialDelay,
                MaxDelay = policy.MaxDelay,
                BackoffType = ParseBackoffType(MapBackoffType(policy.BackoffType)),
                UseJitter = policy.UseJitter,
                ShouldHandle = new PredicateBuilder<Result<StandardTaskResponse>>()
                    .HandleResult(result => ShouldRetryFromRule(result, retryableCodes, retryableErrorTypes)),
                OnRetry = args =>
                {
                    LogRetryAttemptStandardResponse(taskKey, args, policy.MaxRetries);
                    RecordRetryMetric(taskKey);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Extracts retryable codes from an ErrorHandlerRule.
    /// Converts all codes to strings for unified matching.
    /// </summary>
    private static HashSet<string> ExtractRetryableCodesFromRule(ErrorHandlerRule rule)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        if (rule.ErrorCodes == null || rule.ErrorCodes.Count == 0)
            return codes;

        // Check for wildcard
        if (rule.ErrorCodes.Contains("*"))
        {
            // Wildcard means retry for any code - we use a special marker
            codes.Add("*");
            return codes;
        }

        foreach (var code in rule.ErrorCodes)
        {
            if (!string.IsNullOrWhiteSpace(code))
                codes.Add(code);
        }

        return codes;
    }

    /// <summary>
    /// Extracts retryable error types from an ErrorHandlerRule.
    /// </summary>
    private static HashSet<string> ExtractRetryableTypesFromRule(ErrorHandlerRule rule)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        if (rule.ErrorTypes == null || rule.ErrorTypes.Count == 0)
            return types;

        // Check for wildcard
        if (rule.ErrorTypes.Contains("*"))
        {
            types.Add("*");
            return types;
        }

        foreach (var type in rule.ErrorTypes)
        {
            if (!string.IsNullOrWhiteSpace(type))
                types.Add(type);
        }

        return types;
    }

    /// <summary>
    /// Determines if a StandardTaskResponse should trigger a retry based on ErrorHandlerRule.
    /// Only retries for codes/types explicitly defined in the rule.
    /// </summary>
    private static bool ShouldRetryFromRule(
        Result<StandardTaskResponse> result,
        HashSet<string> retryableCodes,
        HashSet<string> retryableTypes)
    {
        // If no codes or types specified, don't retry
        if (retryableCodes.Count == 0 && retryableTypes.Count == 0)
            return false;

        // Check for wildcard - retry everything
        var hasWildcardCode = retryableCodes.Contains("*");
        var hasWildcardType = retryableTypes.Contains("*");

        // Check Result failure
        if (!result.IsSuccess)
        {
            var errorCode = result.Error.Code;
            // Map error code prefix to type (e.g., "Task:400007" -> "TaskError")
            var errorType = ExtractErrorTypeFromCode(errorCode);

            // Check type match
            if (hasWildcardType || MatchesType(errorType, retryableTypes))
                return true;

            // Check code match
            if (hasWildcardCode || (!string.IsNullOrEmpty(errorCode) && retryableCodes.Contains(errorCode)))
                return true;

            return false;
        }

        // Result is success, check StandardTaskResponse
        var response = result.Value;
        if (response == null)
            return false;

        // Check StatusCode
        if (response.StatusCode.HasValue)
        {
            var statusCodeStr = response.StatusCode.Value.ToString();
            if (hasWildcardCode || retryableCodes.Contains(statusCodeStr))
                return true;
        }

        // Check error code from response
        if (response.Error.HasValue)
        {
            var errorCode = response.Error.Value.Code;
            if (hasWildcardCode || (!string.IsNullOrEmpty(errorCode) && retryableCodes.Contains(errorCode)))
                return true;
        }

        // Check error core (domain error code)
        if (!string.IsNullOrEmpty(response.ErrorCode))
        {
            if (hasWildcardCode || retryableCodes.Contains(response.ErrorCode))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if an error type matches any of the retryable types.
    /// </summary>
    private static bool MatchesType(string errorType, HashSet<string> retryableTypes)
    {
        if (retryableTypes.Count == 0)
            return false;

        return retryableTypes.Any(t =>
            string.Equals(t, errorType, StringComparison.OrdinalIgnoreCase) ||
            errorType.EndsWith(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts an error type from an error code.
    /// For codes like "Task:400007", returns "TaskError".
    /// For codes like "Dapr.Invocation", returns "DaprError".
    /// </summary>
    private static string ExtractErrorTypeFromCode(string? errorCode)
    {
        if (string.IsNullOrEmpty(errorCode))
            return "Exception";

        // Handle prefixed codes like "Task:400007"
        var colonIndex = errorCode.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = errorCode[..colonIndex];
            return $"{prefix}Error";
        }

        // Handle dotted codes like "Dapr.Invocation"
        var dotIndex = errorCode.IndexOf('.');
        if (dotIndex > 0)
        {
            var prefix = errorCode[..dotIndex];
            return $"{prefix}Error";
        }

        return "Exception";
    }

    /// <summary>
    /// Merges ErrorBoundary RetryPolicy with default timing/behavior options.
    /// NOTE: Does NOT include default retryable codes - those must come from ErrorBoundary.
    /// </summary>
    private TaskRetryOptions MergeOptions(ErrorBoundaryRetryPolicy? errorBoundaryPolicy)
    {
        if (errorBoundaryPolicy == null)
            return _defaultOptions;

        return new TaskRetryOptions
        {
            MaxRetryAttempts = errorBoundaryPolicy.MaxRetries,
            RetryDelayMilliseconds = (int)errorBoundaryPolicy.InitialDelay.TotalMilliseconds,
            MaxRetryDelayMilliseconds = (int)errorBoundaryPolicy.MaxDelay.TotalMilliseconds,
            BackoffType = MapBackoffType(errorBoundaryPolicy.BackoffType),
            BackoffMultiplier = errorBoundaryPolicy.BackoffMultiplier,
            UseJitter = errorBoundaryPolicy.UseJitter,
            // NO default retryable codes - these must come from ErrorBoundary definition
            RetryableStatusCodes = [],
            RetryableErrorCodes = []
        };
    }

    /// <summary>
    /// Determines if the result should trigger a retry.
    /// ONLY checks user-defined retryable codes (NO technical defaults).
    /// </summary>
    private static bool ShouldRetry(
        Result<TaskInvocationResult> result,
        HashSet<int> retryableStatusCodes,
        HashSet<string> retryableErrorCodes)
    {
        // If no retryable codes defined, never retry
        if (retryableStatusCodes.Count == 0 && retryableErrorCodes.Count == 0)
            return false;

        // Check Result failure
        if (!result.IsSuccess)
        {
            var errorCode = result.Error.Code;
            if (!string.IsNullOrEmpty(errorCode) && retryableErrorCodes.Contains(errorCode))
                return true;

            return false;
        }

        // Result is success, check TaskInvocationResult
        var invocationResult = result.Value;
        if (invocationResult == null)
            return false;

        // Check StatusCode for retryable status
        if (invocationResult.StatusCode.HasValue && 
            retryableStatusCodes.Contains(invocationResult.StatusCode.Value))
        {
            return true;
        }

        // Check error code
        if (!string.IsNullOrEmpty(invocationResult.ErrorCode) &&
            retryableErrorCodes.Contains(invocationResult.ErrorCode))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines if a Result should trigger a retry.
    /// ONLY checks user-defined retryable codes (NO technical defaults).
    /// </summary>
    private static bool ShouldRetryResult(Result result, HashSet<string> retryableErrorCodes)
    {
        // If no retryable codes defined, never retry
        if (retryableErrorCodes.Count == 0)
            return false;

        if (result.IsSuccess)
            return false;

        var errorCode = result.Error.Code;
        if (!string.IsNullOrEmpty(errorCode) && retryableErrorCodes.Contains(errorCode))
            return true;

        return false;
    }

    /// <summary>
    /// Determines if a StandardTaskResponse should trigger a retry.
    /// ONLY checks user-defined retryable codes (NO technical defaults).
    /// </summary>
    private static bool ShouldRetryStandardResponse(
        Result<StandardTaskResponse> result,
        HashSet<int> retryableStatusCodes,
        HashSet<string> retryableErrorCodes)
    {
        // If no retryable codes defined, never retry
        if (retryableStatusCodes.Count == 0 && retryableErrorCodes.Count == 0)
            return false;

        // Check Result failure (Dapr communication errors, mapping errors, etc.)
        if (!result.IsSuccess)
        {
            var errorCode = result.Error.Code;
            if (!string.IsNullOrEmpty(errorCode) && retryableErrorCodes.Contains(errorCode))
                return true;

            return false;
        }

        // Result is success, check StandardTaskResponse
        var response = result.Value;
        if (response == null)
            return false;

        // Check if StatusCode matches configured retryable codes
        if (response.StatusCode.HasValue && 
            retryableStatusCodes.Contains(response.StatusCode.Value))
        {
            return true;
        }

        // Check if response has explicit error with retryable code
        if (response.HasExplicitError())
        {
            var errorCode = response.Error!.Value.Code;
            if (!string.IsNullOrEmpty(errorCode) && retryableErrorCodes.Contains(errorCode))
                return true;
        }

        // Check ErrorCode property
        if (!string.IsNullOrEmpty(response.ErrorCode) && 
            retryableErrorCodes.Contains(response.ErrorCode))
        {
            return true;
        }

        return false;
    }


    /// <summary>
    /// Logs a retry attempt for task invocation.
    /// </summary>
    private void LogRetryAttempt(
        string taskKey,
        OnRetryArguments<Result<TaskInvocationResult>> args,
        int maxAttempts)
    {
        var (errorInfo, statusCode) = ExtractErrorInfo(args.Outcome);
        _logger.LogWarning(
            "Task {TaskKey} retry attempt {AttemptNumber}/{MaxAttempts} after {Delay}ms. " +
            "StatusCode: {StatusCode}, Error: {Error}",
            taskKey,
            args.AttemptNumber,
            maxAttempts,
            args.Duration.TotalMilliseconds,
            statusCode,
            errorInfo);
    }

    /// <summary>
    /// Logs a retry attempt for Result operations.
    /// </summary>
    private void LogRetryAttemptResult(
        string taskKey,
        OnRetryArguments<Result> args,
        int maxAttempts)
    {
        var errorInfo = args.Outcome.Exception?.Message ?? 
            (!args.Outcome.Result.IsSuccess ? args.Outcome.Result.Error.Message : "Unknown");
        _logger.LogWarning(
            "Task {TaskKey} retry attempt {AttemptNumber}/{MaxAttempts} after {Delay}ms. Error: {Error}",
            taskKey,
            args.AttemptNumber,
            maxAttempts,
            args.Duration.TotalMilliseconds,
            errorInfo);
    }

    /// <summary>
    /// Logs a retry attempt for StandardTaskResponse operations.
    /// </summary>
    private void LogRetryAttemptStandardResponse(
        string taskKey,
        OnRetryArguments<Result<StandardTaskResponse>> args,
        int maxAttempts)
    {
        var (errorInfo, statusCode) = ExtractStandardResponseErrorInfo(args.Outcome);
        _logger.LogWarning(
            "Task {TaskKey} retry attempt {AttemptNumber}/{MaxAttempts} after {Delay}ms. " +
            "StatusCode: {StatusCode}, Error: {Error}",
            taskKey,
            args.AttemptNumber,
            maxAttempts,
            args.Duration.TotalMilliseconds,
            statusCode,
            errorInfo);
    }

    /// <summary>
    /// Extracts error information from StandardTaskResponse outcome.
    /// </summary>
    private static (string ErrorInfo, int? StatusCode) ExtractStandardResponseErrorInfo(
        Outcome<Result<StandardTaskResponse>> outcome)
    {
        if (outcome.Exception != null)
            return (outcome.Exception.Message, null);

        var result = outcome.Result;
        if (!result.IsSuccess)
            return (result.Error.Message ?? "Result failure", null);

        var response = result.Value;
        if (response == null)
            return ("Null response", null);

        var errorInfo = response.ErrorMessage ?? 
            (response.Error.HasValue ? response.Error.Value.Message : null) ??
            (response.IsSuccess ? "Success" : "Response failed");

        return (errorInfo, response.StatusCode);
    }

    /// <summary>
    /// Records retry metrics.
    /// </summary>
    private void RecordRetryMetric(string taskKey)
    {
        // Record task retry metric
        _workflowMetrics.RecordTaskFailed("retry", taskKey, 0);
    }

    /// <summary>
    /// Extracts error information from the outcome.
    /// </summary>
    private static (string ErrorInfo, int? StatusCode) ExtractErrorInfo(
        Outcome<Result<TaskInvocationResult>> outcome)
    {
        if (outcome.Exception != null)
            return (outcome.Exception.Message, null);

        var result = outcome.Result;
        if (!result.IsSuccess)
            return (result.Error.Message ?? "Result failure", null);

        var invocationResult = result.Value;
        if (invocationResult == null)
            return ("Null result", null);

        return (
            invocationResult.ErrorMessage ?? (invocationResult.IsSuccess ? "Success" : "Invocation failed"),
            invocationResult.StatusCode
        );
    }

    /// <summary>
    /// Maps ErrorBoundary BackoffType to string.
    /// </summary>
    private static string MapBackoffType(BackoffType backoffType)
    {
        return backoffType switch
        {
            BackoffType.Fixed => "Fixed",
            BackoffType.Exponential => "Exponential",
            _ => "Exponential"
        };
    }

    /// <summary>
    /// Parses backoff type string to Polly DelayBackoffType.
    /// </summary>
    private static DelayBackoffType ParseBackoffType(string backoffType)
    {
        return backoffType.ToLowerInvariant() switch
        {
            "exponential" => DelayBackoffType.Exponential,
            "fixed" or "constant" => DelayBackoffType.Constant,
            _ => DelayBackoffType.Exponential
        };
    }
}
