using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Factory for building ErrorContext from various error sources.
/// Normalizes exceptions, Result errors, and timeout scenarios.
/// </summary>
public sealed class ErrorContextBuilder
{
    // Lazy singleton for default normalizer - thread-safe, initialized only once on first use
    private static readonly Lazy<IErrorNormalizer> DefaultNormalizerLazy = new(() => new ErrorNormalizer());
    
    private readonly IErrorNormalizer _normalizer;
    
    private Exception? _exception;
    private Error? _error;
    private ErrorBoundaryScope _scope = ErrorBoundaryScope.Task;
    private string? _taskKey;
    private string? _stateKey;
    private string? _transitionKey;
    private Guid _instanceId;
    private string _domain = string.Empty;
    private string _flow = string.Empty;
    private int _attempt;
    private bool? _isTimeout;
    private string? _errorCode;
    private int? _statusCode;
    private string? _exceptionTypeName;
    private NormalizedError? _normalizedError;
    private StandardTaskResponse? _standardResponse;
    private TaskInvocationResult? _taskInvocationResult;

    /// <summary>
    /// Initializes a new instance of ErrorContextBuilder.
    /// </summary>
    /// <param name="normalizer">Optional error normalizer. If null, uses default singleton instance.</param>
    private ErrorContextBuilder(IErrorNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? DefaultNormalizerLazy.Value;
    }

    /// <summary>
    /// Sets the exception that caused the error.
    /// </summary>
    public ErrorContextBuilder WithException(Exception exception)
    {
        _exception = exception;
        return this;
    }

    /// <summary>
    /// Sets the Result error.
    /// </summary>
    public ErrorContextBuilder WithError(Error error)
    {
        _error = error;
        return this;
    }

    /// <summary>
    /// Sets the error boundary scope.
    /// </summary>
    public ErrorContextBuilder WithScope(ErrorBoundaryScope scope)
    {
        _scope = scope;
        return this;
    }

    /// <summary>
    /// Sets the task key where the error occurred.
    /// </summary>
    public ErrorContextBuilder WithTask(string taskKey)
    {
        _taskKey = taskKey;
        return this;
    }

    /// <summary>
    /// Sets the task key from a WorkflowTask.
    /// </summary>
    public ErrorContextBuilder WithTask(WorkflowTask task)
    {
        _taskKey = task.Key;
        return this;
    }

    /// <summary>
    /// Sets the task key from an OnExecuteTask configuration.
    /// </summary>
    public ErrorContextBuilder WithOnExecuteTask(OnExecuteTask onExecuteTask)
    {
        _taskKey = onExecuteTask.Task.Key;
        return this;
    }

    /// <summary>
    /// Sets the state key where the error occurred.
    /// </summary>
    public ErrorContextBuilder WithState(string stateKey)
    {
        _stateKey = stateKey;
        return this;
    }

    /// <summary>
    /// Sets the state key from a State.
    /// </summary>
    public ErrorContextBuilder WithState(State state)
    {
        _stateKey = state.Key;
        return this;
    }

    /// <summary>
    /// Sets the transition key.
    /// </summary>
    public ErrorContextBuilder WithTransition(string transitionKey)
    {
        _transitionKey = transitionKey;
        return this;
    }

    /// <summary>
    /// Sets the transition key from a Transition.
    /// </summary>
    public ErrorContextBuilder WithTransition(Transition transition)
    {
        _transitionKey = transition.Key;
        return this;
    }

    /// <summary>
    /// Sets the instance information.
    /// </summary>
    public ErrorContextBuilder WithInstance(Instance instance)
    {
        _instanceId = instance.Id;
        return this;
    }

    /// <summary>
    /// Sets the instance ID.
    /// </summary>
    public ErrorContextBuilder WithInstanceId(Guid instanceId)
    {
        _instanceId = instanceId;
        return this;
    }

    /// <summary>
    /// Sets the domain.
    /// </summary>
    public ErrorContextBuilder WithDomain(string domain)
    {
        _domain = domain;
        return this;
    }

    /// <summary>
    /// Sets the workflow key.
    /// </summary>
    public ErrorContextBuilder WithFlow(string flow)
    {
        _flow = flow;
        return this;
    }

    /// <summary>
    /// Sets the retry attempt number.
    /// </summary>
    public ErrorContextBuilder WithAttempt(int attempt)
    {
        _attempt = attempt;
        return this;
    }

    /// <summary>
    /// Explicitly marks this as a timeout error.
    /// </summary>
    public ErrorContextBuilder AsTimeout()
    {
        _isTimeout = true;
        return this;
    }

    /// <summary>
    /// Explicitly sets the error code (domain error code as string).
    /// </summary>
    public ErrorContextBuilder WithErrorCode(string errorCode)
    {
        _errorCode = errorCode;
        return this;
    }

    /// <summary>
    /// Sets the HTTP status code from the response.
    /// Used for StatusCode-based error boundary matching.
    /// </summary>
    public ErrorContextBuilder WithStatusCode(int statusCode)
    {
        _statusCode = statusCode;
        return this;
    }

    /// <summary>
    /// Populates builder from a TaskInvocationResult failure.
    /// Extracts ErrorCode, ExceptionTypeName, StatusCode and error message.
    /// Creates a NormalizedError for consistent boundary matching.
    /// </summary>
    public ErrorContextBuilder WithTaskInvocationResult(TaskInvocationResult result)
    {
        _taskInvocationResult = result;
        
        if (!string.IsNullOrEmpty(result.ErrorCode))
            _errorCode = result.ErrorCode;

        if (!string.IsNullOrEmpty(result.ExceptionTypeName))
            _exceptionTypeName = result.ExceptionTypeName;

        if (result.StatusCode.HasValue)
            _statusCode = result.StatusCode;

        // Create normalized error from TaskInvocationResult
        if (!result.IsSuccess || result.StatusCode >= 400)
        {
            _normalizedError = _normalizer.FromTaskInvocationResult(result);
        }

        return this;
    }

    /// <summary>
    /// Populates builder from a StandardTaskResponse failure.
    /// Extracts ErrorCode, ExceptionTypeName, StatusCode and creates NormalizedError.
    /// </summary>
    public ErrorContextBuilder WithStandardResponse(StandardTaskResponse response)
    {
        _standardResponse = response;
        
        if (!string.IsNullOrEmpty(response.ErrorCode))
            _errorCode = response.ErrorCode;

        if (!string.IsNullOrEmpty(response.ExceptionTypeName))
            _exceptionTypeName = response.ExceptionTypeName;

        if (response.StatusCode.HasValue)
            _statusCode = response.StatusCode;

        // Create normalized error from StandardTaskResponse
        if (!response.IsSuccess || response.Error.HasValue || response.StatusCode >= 400)
        {
            _normalizedError = _normalizer.FromStandardResponse(response);
        }

        return this;
    }

    /// <summary>
    /// Explicitly sets the normalized error.
    /// </summary>
    public ErrorContextBuilder WithNormalizedError(NormalizedError normalizedError)
    {
        _normalizedError = normalizedError;
        return this;
    }

    /// <summary>
    /// Populates builder from TransitionExecutionContext.
    /// </summary>
    public ErrorContextBuilder FromContext(TransitionExecutionContext context)
    {
        _instanceId = context.InstanceId;
        _domain = context.Domain;
        _flow = context.WorkflowKey;
        _transitionKey = context.TransitionKey;
        _stateKey = context.Current?.Key;
        return this;
    }

    /// <summary>
    /// Builds the ErrorContext with normalized error for consistent boundary matching.
    /// </summary>
    public ErrorContext Build()
    {
        var (exceptionTypeName, message, details, extractedCode, isTimeoutFromException) = ExtractExceptionInfo();
        var (errorTypeName, errorMessage, errorDetails, errorCode, isTimeoutFromError) = ExtractErrorInfo();

        // Build or use existing normalized error
        var normalizedError = _normalizedError ?? BuildNormalizedError(
            exceptionTypeName ?? errorTypeName,
            extractedCode ?? errorCode);

        var finalExceptionTypeName = _exceptionTypeName ?? exceptionTypeName ?? errorTypeName ?? 
            normalizedError?.ExceptionType ?? "Unknown";
        var finalErrorCode = _errorCode ?? extractedCode ?? errorCode ?? normalizedError?.Code;
        var finalStatusCode = _statusCode ?? normalizedError?.StatusCode;
        var finalIsTimeout = _isTimeout ?? isTimeoutFromException ?? isTimeoutFromError ?? 
            (normalizedError?.IsTransient == true && 
             normalizedError.Code.Contains("Timeout", StringComparison.OrdinalIgnoreCase));

        return new ErrorContext
        {
            Exception = _exception,
            Error = _error,
            ExceptionTypeName = finalExceptionTypeName,
            ErrorCode = finalErrorCode,
            StatusCode = finalStatusCode,
            IsTimeout = finalIsTimeout,
            Scope = _scope,
            TaskKey = _taskKey,
            StateKey = _stateKey,
            TransitionKey = _transitionKey,
            InstanceId = _instanceId,
            Domain = _domain,
            Flow = _flow,
            Attempt = _attempt,
            Message = message ?? errorMessage ?? normalizedError?.Message ?? "Unknown error",
            Details = details ?? errorDetails,
            OccurredAt = DateTimeOffset.UtcNow,
            NormalizedError = normalizedError
        };
    }

    /// <summary>
    /// Builds a normalized error from available information.
    /// </summary>
    private NormalizedError? BuildNormalizedError(string? exceptionTypeName, string? errorCode)
    {
        // If we have exception, normalize from exception
        if (_exception != null)
        {
            return _normalizer.FromException(_exception, MapScopeToLayer(_scope));
        }
        
        // If we have error, normalize from error
        if (_error != null)
        {
            return _normalizer.FromResult(_error.Value);
        }
        
        // If we only have codes/status, build minimal normalized error
        if (!string.IsNullOrEmpty(errorCode) || _statusCode.HasValue)
        {
            var layer = MapScopeToLayer(_scope);
            var code = errorCode ?? $"{layer}:Unknown:{_statusCode ?? 0}";
            
            return new NormalizedError
            {
                Code = code,
                Layer = layer,
                ExceptionType = exceptionTypeName,
                StatusCode = _statusCode,
                Message = "Error from context",
                Source = ErrorSource.ResultFailure,
                IsTransient = IsTransientCode(errorCode) || IsTransientStatusCode(_statusCode)
            };
        }
        
        return null;
    }

    /// <summary>
    /// Maps ErrorBoundaryScope to ErrorLayer.
    /// </summary>
    private static ErrorLayer MapScopeToLayer(ErrorBoundaryScope scope)
    {
        return scope switch
        {
            ErrorBoundaryScope.Task => ErrorLayer.Task,
            ErrorBoundaryScope.State => ErrorLayer.Pipeline,
            ErrorBoundaryScope.Global => ErrorLayer.Pipeline,
            ErrorBoundaryScope.SubFlow => ErrorLayer.Pipeline,
            _ => ErrorLayer.Pipeline
        };
    }

    /// <summary>
    /// Checks if a status code indicates a transient error.
    /// </summary>
    private static bool IsTransientStatusCode(int? statusCode)
    {
        return statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    }

    /// <summary>
    /// Checks if an error code indicates a transient error.
    /// </summary>
    private static bool IsTransientCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        return code.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
               code.Contains("503") || code.Contains("502") || code.Contains("429");
    }

    private (string? typeName, string? message, string? details, string? code, bool? isTimeout) ExtractExceptionInfo()
    {
        if (_exception == null)
            return (null, null, null, null, null);

        var typeName = _exception.GetType().Name;
        var message = _exception.Message;
        var details = _exception.StackTrace;

        string? code = _exception switch
        {
            IHasErrorCode hasCode => hasCode.ErrorCode.ToString(),
            HttpRequestException httpEx => httpEx.StatusCode?.ToString(),
            _ => null
        };

        var isTimeout = _exception is TimeoutException ||
                        (_exception is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested) ||
                        (_exception is OperationCanceledException oce && !oce.CancellationToken.IsCancellationRequested) ||
                        _exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);

        return (typeName, message, details, code, isTimeout);
    }

    private (string? typeName, string? message, string? details, string? code, bool? isTimeout) ExtractErrorInfo()
    {
        if (_error == null)
            return (null, null, null, null, null);

        // Get error info from string representation since Error properties may vary
        var errorString = _error.ToString();
        var typeName = MapErrorStringToExceptionName(errorString);
        var isTimeout = errorString?.Contains("timeout", StringComparison.OrdinalIgnoreCase) ?? false;
        
        // Extract error code from Error (Error is a struct)
        var code = _error.Value.Code;

        return (typeName, errorString, null, code, isTimeout);
    }

    private static string MapErrorStringToExceptionName(string? errorString)
    {
        if (string.IsNullOrEmpty(errorString)) return "Exception";

        return errorString.ToLowerInvariant() switch
        {
            var c when c.Contains("validation") => "ValidationException",
            var c when c.Contains("notfound") || c.Contains("not_found") => "NotFoundException",
            var c when c.Contains("conflict") => "ConflictException",
            var c when c.Contains("forbidden") => "ForbiddenException",
            var c when c.Contains("unauthorized") => "UnauthorizedException",
            var c when c.Contains("timeout") => "TimeoutException",
            _ => "FailureException"
        };
    }

    /// <summary>
    /// Creates a new builder instance with default normalizer.
    /// </summary>
    /// <returns>A new ErrorContextBuilder instance.</returns>
    public static ErrorContextBuilder Create() => new();

    /// <summary>
    /// Creates a new builder instance with a custom normalizer.
    /// Useful for testing or custom error normalization strategies.
    /// </summary>
    /// <param name="normalizer">Custom error normalizer instance.</param>
    /// <returns>A new ErrorContextBuilder instance.</returns>
    public static ErrorContextBuilder Create(IErrorNormalizer normalizer) => new(normalizer);
}

