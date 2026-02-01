using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Default implementation of post-commit failure policy.
/// Distinguishes between client errors (validation, not found, etc.) and system errors.
/// Client errors are returned to the caller without faulting the instance.
/// System errors (dependency, transient) mark the instance as faulted.
/// </summary>
public sealed class DefaultPostCommitFailurePolicy : IPostCommitFailurePolicy
{
    /// <inheritdoc />
    public PostCommitFailureDecision Decide(PostCommitFailureContext context)
    {
        // Check if this is a client error (should not fault instance)
        // Client errors are validation issues, not found, conflicts, etc.
        // These should be returned to the client as proper error responses
        if (IsClientError(context.Error))
        {
            return new PostCommitFailureDecision(
                ShouldContinue: false,
                ShouldMarkInstanceFaulted: false,
                FaultErrorCode: string.Empty,
                FaultErrorMessage: null);
        }

        // System/Infrastructure errors - fault the instance
        // These are dependency failures, transient errors, etc.
        // The workflow cannot continue safely in these scenarios
        return new PostCommitFailureDecision(
            ShouldContinue: false,
            ShouldMarkInstanceFaulted: true,
            FaultErrorCode: context.Error.Code,
            FaultErrorMessage: context.Error.Message);
    }

    /// <summary>
    /// Determines if an error is a client error that should be returned to the user
    /// rather than faulting the instance.
    /// </summary>
    /// <param name="error">The error to classify.</param>
    /// <returns>True if this is a client error, false if it's a system error.</returns>
    private static bool IsClientError(Error error)
    {
        // Client errors that should return to user, not fault instance
        return error.Prefix is 
            ErrorCodes.Prefixes.Validation or
            ErrorCodes.Prefixes.NotFound or
            ErrorCodes.Prefixes.Conflict or
            ErrorCodes.Prefixes.Unauthorized or
            ErrorCodes.Prefixes.Forbidden;
    }
}

