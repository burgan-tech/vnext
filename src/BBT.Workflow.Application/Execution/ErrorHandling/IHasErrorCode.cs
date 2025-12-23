namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Interface for exceptions that carry a domain-specific error code.
/// Used by ErrorContextBuilder to extract error codes for policy matching.
/// </summary>
public interface IHasErrorCode
{
    /// <summary>
    /// Gets the domain-specific error code.
    /// </summary>
    int ErrorCode { get; }
}

