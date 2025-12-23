using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Factory for building ErrorContext from various error sources.
/// Normalizes exceptions, Result errors, and timeout scenarios.
/// </summary>
public sealed class ErrorContextBuilder
{
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
    private int? _errorCode;

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
    /// Explicitly sets the error code.
    /// </summary>
    public ErrorContextBuilder WithErrorCode(int errorCode)
    {
        _errorCode = errorCode;
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
    /// Builds the ErrorContext.
    /// </summary>
    public ErrorContext Build()
    {
        var (exceptionTypeName, message, details, extractedCode, isTimeoutFromException) = ExtractExceptionInfo();
        var (errorTypeName, errorMessage, errorDetails, errorCode, isTimeoutFromError) = ExtractErrorInfo();

        return new ErrorContext
        {
            Exception = _exception,
            Error = _error,
            ExceptionTypeName = exceptionTypeName ?? errorTypeName ?? "Unknown",
            ErrorCode = _errorCode ?? extractedCode ?? errorCode,
            IsTimeout = _isTimeout ?? isTimeoutFromException ?? isTimeoutFromError ?? false,
            Scope = _scope,
            TaskKey = _taskKey,
            StateKey = _stateKey,
            TransitionKey = _transitionKey,
            InstanceId = _instanceId,
            Domain = _domain,
            Flow = _flow,
            Attempt = _attempt,
            Message = message ?? errorMessage ?? "Unknown error",
            Details = details ?? errorDetails,
            OccurredAt = DateTimeOffset.UtcNow
        };
    }

    private (string? typeName, string? message, string? details, int? code, bool? isTimeout) ExtractExceptionInfo()
    {
        if (_exception == null)
            return (null, null, null, null, null);

        var typeName = _exception.GetType().Name;
        var message = _exception.Message;
        var details = _exception.StackTrace;

        int? code = _exception switch
        {
            IHasErrorCode hasCode => hasCode.ErrorCode,
            HttpRequestException httpEx => (int?)httpEx.StatusCode,
            _ => null
        };

        var isTimeout = _exception is TimeoutException ||
                        (_exception is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested) ||
                        (_exception is OperationCanceledException oce && !oce.CancellationToken.IsCancellationRequested) ||
                        _exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);

        return (typeName, message, details, code, isTimeout);
    }

    private (string? typeName, string? message, string? details, int? code, bool? isTimeout) ExtractErrorInfo()
    {
        if (_error == null)
            return (null, null, null, null, null);

        // Get error info from string representation since Error properties may vary
        var errorString = _error.ToString();
        var typeName = MapErrorStringToExceptionName(errorString);
        var isTimeout = errorString?.Contains("timeout", StringComparison.OrdinalIgnoreCase) ?? false;

        return (typeName, errorString, null, null, isTimeout);
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
    /// Creates a new builder instance.
    /// </summary>
    public static ErrorContextBuilder Create() => new();
}

