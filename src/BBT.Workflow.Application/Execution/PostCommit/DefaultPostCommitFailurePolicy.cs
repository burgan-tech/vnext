namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Default implementation of post-commit failure policy.
/// Implements fail-fast behavior and marks instance as faulted on any failure.
/// </summary>
public sealed class DefaultPostCommitFailurePolicy : IPostCommitFailurePolicy
{
    /// <inheritdoc />
    public PostCommitFailureDecision Decide(PostCommitFailureContext context)
    {
        // INFO: If you need to customize subflow failure handling, you can use the following code block:
        // Subflow failures are critical - parent workflow cannot continue if subflow fails.
        //
        // if (context.Job is StartSubflowJob)
        // {
        //     return new PostCommitFailureDecision(
        //         ShouldContinue: false,
        //         ShouldMarkInstanceFaulted: true,
        //         FaultErrorCode: context.Error.Code,
        //         FaultErrorMessage: context.Error.Message);
        // }

        // Default behavior for other job types: fail-fast + mark instance faulted
        // Can be customized per job type if needed
        return new PostCommitFailureDecision(
            ShouldContinue: false,
            ShouldMarkInstanceFaulted: true,
            FaultErrorCode: context.Error.Code,
            FaultErrorMessage: context.Error.Message);
    }
}

