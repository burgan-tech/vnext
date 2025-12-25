using BBT.Aether.Results;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Implementation of IErrorNormalizer for consistent error representation.
/// Normalizes errors from Result, TaskInvocationResult, and StandardTaskResponse.
/// </summary>
public sealed class ErrorNormalizer : IErrorNormalizer
{
    /// <summary>
    /// HTTP status codes that are typically transient and retryable.
    /// </summary>
    private static readonly HashSet<int> TransientStatusCodes = [408, 429, 500, 502, 503, 504];
    
    /// <summary>
    /// Error code patterns that indicate transient errors.
    /// </summary>
    private static readonly string[] TransientPatterns = 
        ["Timeout", "Cancelled", "503", "502", "429", "Connection", "Unavailable"];

    /// <inheritdoc />
    public NormalizedError FromResult(Error error)
    {
        var originalCode = error.Code;
        var layer = DetermineLayerFromCode(originalCode);
        var normalizedCode = NormalizeCode(originalCode, layer);
        
        return new NormalizedError
        {
            Code = normalizedCode,
            OriginalCode = originalCode != normalizedCode ? originalCode : null,
            Layer = layer,
            Message = error.Message ?? "Unknown error",
            Source = ErrorSource.ResultFailure,
            IsTransient = IsTransientCode(originalCode)
        };
    }

    /// <inheritdoc />
    public NormalizedError FromTaskInvocationResult(TaskInvocationResult result)
    {
        var statusCode = result.StatusCode;
        var originalCode = result.ErrorCode;
        
        // Build normalized code: Task:{Category}:{StatusCode or Detail}
        var normalizedCode = BuildTaskErrorCode(originalCode, statusCode, result.ExceptionTypeName);
        
        return new NormalizedError
        {
            Code = normalizedCode,
            OriginalCode = originalCode != normalizedCode ? originalCode : null,
            Layer = ErrorLayer.Task,
            ExceptionType = result.ExceptionTypeName,
            StatusCode = statusCode,
            Message = result.ErrorMessage ?? "Task execution failed",
            Source = ErrorSource.TaskInvocationFailure,
            IsTransient = IsTransientStatusCode(statusCode) || IsTransientCode(originalCode)
        };
    }

    /// <inheritdoc />
    public NormalizedError FromStandardResponse(StandardTaskResponse response)
    {
        var statusCode = response.StatusCode;
        var originalCode = response.ErrorCode ?? response.Error?.Code;
        var exceptionType = response.ExceptionTypeName;
        
        // Build normalized code
        var normalizedCode = BuildTaskErrorCode(originalCode, statusCode, exceptionType);
        
        var source = response.Error.HasValue 
            ? ErrorSource.ResponseError 
            : ErrorSource.ResponseStatusCode;
        
        return new NormalizedError
        {
            Code = normalizedCode,
            OriginalCode = originalCode != normalizedCode ? originalCode : null,
            Layer = ErrorLayer.Task,
            ExceptionType = exceptionType,
            StatusCode = statusCode,
            Message = response.ErrorMessage ?? response.Error?.Message ?? "Response failed",
            Source = source,
            IsTransient = IsTransientStatusCode(statusCode) || IsTransientCode(originalCode)
        };
    }

    /// <inheritdoc />
    public NormalizedError FromException(Exception exception, ErrorLayer layer = ErrorLayer.Pipeline)
    {
        var exceptionType = exception.GetType().Name;
        var isTimeout = exception is TimeoutException ||
                       (exception is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested) ||
                       (exception is OperationCanceledException oce && !oce.CancellationToken.IsCancellationRequested);
        
        var statusCode = exception is HttpRequestException httpEx ? (int?)httpEx.StatusCode : null;
        
        // Build code based on layer and exception type
        var category = GetCategoryFromExceptionType(exceptionType);
        var detail = isTimeout ? "Timeout" : exceptionType;
        var normalizedCode = $"{layer}:{category}:{detail}";
        
        return new NormalizedError
        {
            Code = normalizedCode,
            Layer = layer,
            ExceptionType = exceptionType,
            StatusCode = statusCode,
            Message = exception.Message,
            Source = ErrorSource.Exception,
            IsTransient = isTimeout || IsTransientStatusCode(statusCode)
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<NormalizedError> ExtractErrors(Result<StandardTaskResponse> result)
    {
        var errors = new List<NormalizedError>();
        
        // Level 1: Result failure (highest priority)
        if (!result.IsSuccess)
        {
            errors.Add(FromResult(result.Error));
            return errors;
        }
        
        var response = result.Value;
        if (response == null) return errors;
        
        // Level 2: Response has explicit error
        if (response.Error.HasValue)
        {
            errors.Add(FromStandardResponse(response));
            return errors;
        }
        
        // Level 3: Response IsSuccess = false or non-2xx StatusCode
        if (!response.IsSuccess || IsErrorStatusCode(response.StatusCode))
        {
            errors.Add(FromStandardResponse(response));
        }
        
        return errors;
    }

    /// <inheritdoc />
    public NormalizedError? GetPrimaryError(Result<StandardTaskResponse> result)
    {
        var errors = ExtractErrors(result);
        return errors.Count > 0 ? errors[0] : null;
    }

    /// <summary>
    /// Determines the error layer from an error code.
    /// </summary>
    private static ErrorLayer DetermineLayerFromCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) 
            return ErrorLayer.Pipeline;
        
        // Check explicit layer prefixes
        if (code.StartsWith("Transport:", StringComparison.OrdinalIgnoreCase))
            return ErrorLayer.Transport;
        if (code.StartsWith("Task:", StringComparison.OrdinalIgnoreCase))
            return ErrorLayer.Task;
        if (code.StartsWith("Pipeline:", StringComparison.OrdinalIgnoreCase))
            return ErrorLayer.Pipeline;
        
        // Infer from known patterns
        if (code.StartsWith("Dapr.", StringComparison.OrdinalIgnoreCase) ||
            code.Contains(".Cancelled", StringComparison.OrdinalIgnoreCase) ||
            code.Contains(".Timeout", StringComparison.OrdinalIgnoreCase))
            return ErrorLayer.Transport;
        
        if (code.StartsWith("Http", StringComparison.OrdinalIgnoreCase) ||
            code.Contains(":4") || code.Contains(":5")) // HTTP status patterns
            return ErrorLayer.Task;
        
        if (code.Contains("Mapping", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("Validation", StringComparison.OrdinalIgnoreCase))
            return ErrorLayer.Pipeline;
            
        return ErrorLayer.Pipeline;
    }

    /// <summary>
    /// Normalizes an error code to the standard format.
    /// </summary>
    private static string NormalizeCode(string? originalCode, ErrorLayer layer)
    {
        if (string.IsNullOrEmpty(originalCode))
            return $"{layer}:Unknown:0";
        
        // Already in correct format
        if (originalCode.StartsWith($"{layer}:", StringComparison.OrdinalIgnoreCase))
            return originalCode;
        
        // Convert legacy patterns
        // "Dapr.Service:503" → "Transport:Dapr.Service:503"
        // "Http:500" → "Task:Http:500"
        // "Task:400007" → "Pipeline:Task:400007"
        
        var colonIndex = originalCode.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = originalCode[..colonIndex];
            var suffix = originalCode[(colonIndex + 1)..];
            
            // Check if it's a status code pattern
            if (int.TryParse(suffix, out _))
            {
                return $"{layer}:{prefix}:{suffix}";
            }
            
            // Already has category:detail format
            return $"{layer}:{originalCode}";
        }
        
        // Single value - use as detail
        return $"{layer}:Unknown:{originalCode}";
    }

    /// <summary>
    /// Builds a task error code from available information.
    /// </summary>
    private static string BuildTaskErrorCode(string? originalCode, int? statusCode, string? exceptionType)
    {
        // If original code already has proper format, normalize it
        if (!string.IsNullOrEmpty(originalCode))
        {
            // Check if it already has layer prefix
            if (originalCode.StartsWith("Task:", StringComparison.OrdinalIgnoreCase) ||
                originalCode.StartsWith("Transport:", StringComparison.OrdinalIgnoreCase) ||
                originalCode.StartsWith("Pipeline:", StringComparison.OrdinalIgnoreCase))
            {
                return originalCode;
            }
            
            // Has category:detail format (e.g., "Http:503", "Dapr.Service:Timeout")
            var colonIndex = originalCode.IndexOf(':');
            if (colonIndex > 0)
            {
                return $"Task:{originalCode}";
            }
            
            // Has dot notation (e.g., "Http.Timeout")
            var dotIndex = originalCode.IndexOf('.');
            if (dotIndex > 0)
            {
                var category = originalCode[..dotIndex];
                var detail = originalCode[(dotIndex + 1)..];
                return $"Task:{category}:{detail}";
            }
        }
        
        // Build from status code
        if (statusCode.HasValue)
        {
            var category = GetCategoryFromExceptionType(exceptionType) ?? "Http";
            return $"Task:{category}:{statusCode.Value}";
        }
        
        // Fallback
        var fallbackCategory = GetCategoryFromExceptionType(exceptionType) ?? "Unknown";
        return $"Task:{fallbackCategory}:0";
    }

    /// <summary>
    /// Gets the category from an exception type name.
    /// </summary>
    private static string? GetCategoryFromExceptionType(string? exceptionType)
    {
        if (string.IsNullOrEmpty(exceptionType))
            return null;
        
        return exceptionType switch
        {
            "HttpRequestException" or "HttpResponseException" => "Http",
            "TimeoutException" or "TaskCanceledException" or "OperationCanceledException" => "Timeout",
            "DaprServiceException" or "DaprHttpEndpointException" => "Dapr",
            var t when t.Contains("Dapr", StringComparison.OrdinalIgnoreCase) => "Dapr",
            var t when t.Contains("Http", StringComparison.OrdinalIgnoreCase) => "Http",
            var t when t.Contains("Timeout", StringComparison.OrdinalIgnoreCase) => "Timeout",
            _ => null
        };
    }

    /// <summary>
    /// Checks if a status code indicates an error.
    /// </summary>
    private static bool IsErrorStatusCode(int? statusCode)
    {
        return statusCode.HasValue && statusCode.Value >= 400;
    }

    /// <summary>
    /// Checks if a status code indicates a transient error.
    /// </summary>
    private static bool IsTransientStatusCode(int? statusCode)
    {
        return statusCode.HasValue && TransientStatusCodes.Contains(statusCode.Value);
    }

    /// <summary>
    /// Checks if an error code indicates a transient error.
    /// </summary>
    private static bool IsTransientCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) 
            return false;
        
        return TransientPatterns.Any(pattern => 
            code.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}

