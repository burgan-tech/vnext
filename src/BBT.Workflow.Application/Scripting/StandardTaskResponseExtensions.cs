namespace BBT.Workflow.Scripting;

/// <summary>
/// Extension methods for StandardTaskResponse to provide consistent success/failure checks.
/// These methods unify the criteria for determining task execution outcomes across the system.
/// </summary>
public static class StandardTaskResponseExtensions
{
    /// <summary>
    /// Checks if the task execution was successful with no errors.
    /// </summary>
    /// <remarks>
    /// Success criteria (ALL must be true):
    /// - No explicit Error property set
    /// - IsSuccess flag is true
    /// - StatusCode is 2xx (200-299) if present
    /// </remarks>
    public static bool IsSuccessful(this StandardTaskResponse? response)
    {
        if (response == null)
            return true;

        // Has explicit error
        if (response.Error.HasValue)
            return false;

        // IsSuccess flag is false
        if (!response.IsSuccess)
            return false;

        // StatusCode check (2xx is success)
        if (response.StatusCode.HasValue)
        {
            var statusCode = response.StatusCode.Value;
            return statusCode >= 200 && statusCode < 300;
        }

        return true;
    }

    /// <summary>
    /// Checks if the response has an explicit domain error.
    /// </summary>
    /// <remarks>
    /// This indicates a domain-level error was set by the task executor,
    /// separate from HTTP status codes or IsSuccess flags.
    /// </remarks>
    public static bool HasExplicitError(this StandardTaskResponse? response)
    {
        return response?.Error.HasValue == true;
    }

    /// <summary>
    /// Checks if the response has a non-success HTTP status code.
    /// </summary>
    /// <remarks>
    /// Returns true for:
    /// - 4xx Client errors
    /// - 5xx Server errors
    /// Returns false for 2xx or 3xx or no StatusCode.
    /// </remarks>
    public static bool HasNonSuccessStatusCode(this StandardTaskResponse? response)
    {
        return response?.StatusCode.HasValue == true && response.StatusCode.Value >= 400;
    }

    /// <summary>
    /// Checks if the response indicates a client error (4xx).
    /// </summary>
    public static bool IsClientError(this StandardTaskResponse? response)
    {
        return response?.StatusCode is >= 400 and < 500;
    }

    /// <summary>
    /// Checks if the response indicates a server error (5xx).
    /// </summary>
    public static bool IsServerError(this StandardTaskResponse? response)
    {
        return response?.StatusCode is >= 500 and < 600;
    }

    /// <summary>
    /// Checks if the response indicates a transient error that should be retried.
    /// </summary>
    /// <remarks>
    /// Transient errors include:
    /// - 408 Request Timeout
    /// - 429 Too Many Requests
    /// - 500 Internal Server Error
    /// - 502 Bad Gateway
    /// - 503 Service Unavailable
    /// - 504 Gateway Timeout
    /// </remarks>
    public static bool IsTransientError(this StandardTaskResponse? response)
    {
        if (response?.StatusCode == null)
            return false;

        return response.StatusCode.Value is 408 or 429 or 500 or 502 or 503 or 504;
    }

    /// <summary>
    /// Checks if the error is retryable based on error code patterns.
    /// </summary>
    /// <remarks>
    /// Checks ErrorCode or Error.Code for patterns like:
    /// - "Timeout", "503", "502", "429"
    /// - "Cancelled" (but not user-initiated)
    /// </remarks>
    public static bool HasRetryableErrorCode(this StandardTaskResponse? response)
    {
        if (response == null)
            return false;

        var errorCode = response.ErrorCode ?? response.Error?.Code;
        if (string.IsNullOrEmpty(errorCode))
            return false;

        return errorCode.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
               errorCode.Contains("503") ||
               errorCode.Contains("502") ||
               errorCode.Contains("429") ||
               errorCode.Contains("Unavailable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the response indicates any kind of failure (error or non-success).
    /// </summary>
    /// <remarks>
    /// Returns true if ANY of these conditions are met:
    /// - Has explicit error
    /// - IsSuccess is false
    /// - StatusCode is non-2xx
    /// </remarks>
    public static bool HasFailure(this StandardTaskResponse? response)
    {
        return !response.IsSuccessful();
    }

    /// <summary>
    /// Checks if the failure should be retried based on both status code and error code.
    /// </summary>
    /// <remarks>
    /// Combines transient status code checks and retryable error code patterns.
    /// Used by Polly pipelines to determine retry eligibility.
    /// </remarks>
    public static bool ShouldRetry(this StandardTaskResponse? response)
    {
        if (response == null)
            return false;

        return response.IsTransientError() || response.HasRetryableErrorCode();
    }

    /// <summary>
    /// Gets a human-readable summary of the response status.
    /// Useful for logging and diagnostics.
    /// </summary>
    public static string GetStatusSummary(this StandardTaskResponse? response)
    {
        if (response == null)
            return "No response";

        if (response.IsSuccessful())
            return $"Success (StatusCode: {response.StatusCode ?? 200})";

        var parts = new List<string>();

        if (response.HasExplicitError())
            parts.Add($"Error: {response.Error?.Code}");

        if (!response.IsSuccess)
            parts.Add("IsSuccess=false");

        if (response.HasNonSuccessStatusCode())
            parts.Add($"StatusCode: {response.StatusCode}");

        return string.Join(", ", parts);
    }
}

