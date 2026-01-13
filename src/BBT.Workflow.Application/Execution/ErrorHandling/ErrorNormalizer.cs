using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Default implementation of error normalization.
/// Converts errors from various sources into NormalizedError for consistent handling.
/// </summary>
public sealed class ErrorNormalizer : IErrorNormalizer
{
    /// <summary>
    /// HTTP status codes that are considered transient/retryable.
    /// </summary>
    private static readonly HashSet<int> TransientStatusCodes =
    [
        408, // Request Timeout
        429, // Too Many Requests
        500, // Internal Server Error
        502, // Bad Gateway
        503, // Service Unavailable
        504  // Gateway Timeout
    ];

    /// <summary>
    /// Exception type names that indicate transient failures.
    /// </summary>
    private static readonly HashSet<string> TransientExceptionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TimeoutException",
        "TaskCanceledException",
        "OperationCanceledException",
        "HttpRequestException",
        "SocketException",
        "IOException"
    };

    /// <inheritdoc />
    public NormalizedError Normalize(Exception exception)
    {
        var exceptionType = exception.GetType().Name;
        var isTransient = IsTransientException(exception);
        var errorCode = ExtractErrorCode(exception);

        return new NormalizedError
        {
            Code = errorCode ?? $"Exception:{exceptionType}",
            Layer = DetermineLayer(exception),
            ExceptionType = exceptionType,
            StatusCode = ExtractStatusCode(exception),
            Message = exception.Message,
            Source = ErrorSource.Exception,
            IsTransient = isTransient,
            OriginalCode = errorCode
        };
    }

    /// <inheritdoc />
    public NormalizedError Normalize(Error error)
    {
        // Try to extract status code from error code if it looks like HTTP status
        int? statusCode = null;
        if (int.TryParse(error.Code, out var code) && code is >= 100 and < 600)
        {
            statusCode = code;
        }

        return new NormalizedError
        {
            Code = error.Code ?? "Result:Failure",
            Layer = ErrorLayer.Pipeline,
            ExceptionType = null,
            StatusCode = statusCode,
            Message = error.Message ?? "Unknown error",
            Source = ErrorSource.ResultFailure,
            IsTransient = statusCode.HasValue && TransientStatusCodes.Contains(statusCode.Value),
            OriginalCode = error.Code
        };
    }

    /// <inheritdoc />
    public NormalizedError NormalizeHttpError(int statusCode, string? errorCode, string? message)
    {
        var isTransient = TransientStatusCodes.Contains(statusCode);

        return new NormalizedError
        {
            Code = errorCode ?? $"Http:{statusCode}",
            Layer = ErrorLayer.Task,
            ExceptionType = null,
            StatusCode = statusCode,
            Message = message ?? $"HTTP {statusCode}",
            Source = ErrorSource.ResponseStatusCode,
            IsTransient = isTransient,
            OriginalCode = errorCode
        };
    }

    /// <inheritdoc />
    public NormalizedError NormalizeTaskResult(TaskInvocationResult result, string taskType)
    {
        var statusCode = result.StatusCode;
        var errorCode = BuildTaskErrorCode(taskType, statusCode, result.ErrorMessage);
        var exceptionType = ExtractExceptionTypeFromMetadata(result.Metadata);
        var isTransient = statusCode.HasValue && TransientStatusCodes.Contains(statusCode.Value);

        // Check metadata for transient indicators
        if (!isTransient &&
            result.Metadata != null &&
            result.Metadata.TryGetValue("IsTransient", out var transientValue) &&
            transientValue is bool isTransientMeta)
        {
            isTransient = isTransientMeta;
        }

        return new NormalizedError
        {
            Code = errorCode,
            Layer = ErrorLayer.Task,
            ExceptionType = exceptionType,
            StatusCode = statusCode,
            Message = result.ErrorMessage ?? "Task execution failed",
            Source = DetermineTaskErrorSource(result),
            IsTransient = isTransient,
            OriginalCode = ExtractOriginalCode(result.Metadata)
        };
    }

    /// <inheritdoc />
    public NormalizedError NormalizeTaskResponse(StandardTaskResponse response, string taskKey, string taskType)
    {
        var statusCode = response.StatusCode;
        var errorCode = $"Task:{taskType}:{taskKey}";
        
        if (statusCode.HasValue)
        {
            errorCode = $"{errorCode}:{statusCode.Value}";
        }

        var exceptionType = ExtractExceptionTypeFromMetadata(response.Metadata);
        var isTransient = statusCode.HasValue && TransientStatusCodes.Contains(statusCode.Value);

        // Check for transient exception type
        if (!isTransient && !string.IsNullOrEmpty(exceptionType) && TransientExceptionTypes.Contains(exceptionType))
        {
            isTransient = true;
        }

        // Check metadata for transient indicators
        if (!isTransient &&
            response.Metadata != null &&
            response.Metadata.TryGetValue("IsTransient", out var transientMeta) &&
            transientMeta is bool isTransientFromMeta)
        {
            isTransient = isTransientFromMeta;
        }

        return new NormalizedError
        {
            Code = errorCode,
            Layer = ErrorLayer.Task,
            ExceptionType = exceptionType,
            StatusCode = statusCode,
            Message = response.ErrorMessage ?? "Task execution failed",
            Source = DetermineResponseErrorSource(response),
            IsTransient = isTransient,
            OriginalCode = ExtractOriginalCode(response.Metadata)
        };
    }

    private static ErrorSource DetermineResponseErrorSource(StandardTaskResponse response)
    {
        if (!response.IsSuccess)
            return ErrorSource.TaskInvocationFailure;

        if (response.StatusCode.HasValue && response.StatusCode.Value >= 400)
            return ErrorSource.ResponseStatusCode;

        return ErrorSource.ResponseError;
    }

    private static string BuildTaskErrorCode(string taskType, int? statusCode, string? errorMessage)
    {
        var parts = new List<string> { "Task", taskType };

        if (statusCode.HasValue)
        {
            parts.Add(statusCode.Value.ToString());
        }
        else if (!string.IsNullOrEmpty(errorMessage))
        {
            var category = NormalizeErrorCategory(errorMessage);
            if (!string.IsNullOrEmpty(category))
            {
                parts.Add(category);
            }
        }

        return string.Join(":", parts);
    }

    private static string NormalizeErrorCategory(string errorMessage)
    {
        var lowerMessage = errorMessage.ToLowerInvariant();

        if (lowerMessage.Contains("timeout"))
            return "Timeout";
        if (lowerMessage.Contains("connection") || lowerMessage.Contains("network"))
            return "Connection";
        if (lowerMessage.Contains("unauthorized") || lowerMessage.Contains("401"))
            return "Unauthorized";
        if (lowerMessage.Contains("forbidden") || lowerMessage.Contains("403"))
            return "Forbidden";
        if (lowerMessage.Contains("not found") || lowerMessage.Contains("404"))
            return "NotFound";
        if (lowerMessage.Contains("validation") || lowerMessage.Contains("invalid"))
            return "Validation";
        if (lowerMessage.Contains("cancelled") || lowerMessage.Contains("canceled"))
            return "Cancelled";

        return "Unknown";
    }

    private static string? ExtractExceptionTypeFromMetadata(Dictionary<string, object>? metadata)
    {
        if (metadata == null)
            return null;

        if (metadata.TryGetValue("ExceptionType", out var exType))
            return exType.ToString();

        return null;
    }

    private static string? ExtractOriginalCode(Dictionary<string, object>? metadata)
    {
        if (metadata == null)
            return null;

        if (metadata.TryGetValue("ErrorCode", out var code))
            return code.ToString();

        return null;
    }

    private static ErrorSource DetermineTaskErrorSource(TaskInvocationResult result)
    {
        if (!result.IsSuccess)
            return ErrorSource.TaskInvocationFailure;

        if (result.StatusCode is >= 400)
            return ErrorSource.ResponseStatusCode;

        return ErrorSource.ResponseError;
    }

    private static bool IsTransientException(Exception exception)
    {
        var typeName = exception.GetType().Name;

        if (TransientExceptionTypes.Contains(typeName))
            return true;

        // Check inner exception
        if (exception.InnerException != null && IsTransientException(exception.InnerException))
            return true;

        // Check for transient keywords in message
        var message = exception.Message;
        return message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private static ErrorLayer DetermineLayer(Exception exception)
    {
        var typeName = exception.GetType().Name;

        // Transport layer exceptions
        if (typeName.Contains("Http") ||
            typeName.Contains("Socket") ||
            typeName.Contains("Network") ||
            typeName.Contains("Dapr"))
        {
            return ErrorLayer.Transport;
        }

        return ErrorLayer.Pipeline;
    }

    private static string? ExtractErrorCode(Exception exception)
    {
        if (exception is IHasErrorCode hasErrorCode)
        {
            return hasErrorCode.ErrorCode.ToString();
        }

        return null;
    }

    private static int? ExtractStatusCode(Exception exception)
    {
        if (exception is HttpRequestException httpEx)
        {
            return (int?)httpEx.StatusCode;
        }

        // Try to extract from HttpRequestException if available
        if (exception.Data.Contains("StatusCode") && 
            exception.Data["StatusCode"] is int statusCode)
        {
            return statusCode;
        }

        return null;
    }
}

