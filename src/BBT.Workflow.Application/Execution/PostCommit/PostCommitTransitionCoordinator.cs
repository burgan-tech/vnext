using System.Collections.Immutable;
using BBT.Aether.Results;
using BBT.Workflow.Execution.Continuations;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Runner-owned coordinator for work exposed by the transition post-commit barrier.
/// </summary>
public sealed class PostCommitTransitionCoordinator(
    IPostCommitExecutor executor,
    ContinuationDispatcher continuationDispatcher) : IPostCommitTransitionCoordinator
{
    /// <inheritdoc />
    public async Task<Result<PostCommitCoordinationResult>> CoordinateAsync(
        TransitionExecutionContext sourceContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceContext);

        var jobs = sourceContext.Directives
            .ConsumePostCommitJobs()
            .ToImmutableArray();

        if (jobs.IsEmpty)
            return Result<PostCommitCoordinationResult>.Ok(CreateDecision(sourceContext));

        var ownershipResult = ResolveOwnership(jobs);
        if (!ownershipResult.IsSuccess)
            return Result<PostCommitCoordinationResult>.Fail(ownershipResult.Error);

        var executionResult = await executor.ExecuteAsync(jobs, sourceContext, cancellationToken);
        if (!executionResult.IsSuccess)
        {
            var error = executionResult.Error ?? Error.Failure(
                "PostCommit:InvalidResult",
                "Post-commit execution failed without an error.");

            if (executionResult.FaultRequest is null)
                return Result<PostCommitCoordinationResult>.Fail(error);

            return Result<PostCommitCoordinationResult>.Ok(
                CreateDecision(sourceContext, faultRequest: executionResult.FaultRequest, error: error));
        }

        if (ownershipResult.Value == PostCommitContinuationBehavior.HandoffToChild ||
            sourceContext.Directives.NextTransition is null)
        {
            return Result<PostCommitCoordinationResult>.Ok(CreateDecision(sourceContext));
        }

        var continuationResult = await continuationDispatcher.DispatchAsync(
            ContinuationMode.Inline,
            sourceContext,
            cancellationToken);
        if (!continuationResult.IsSuccess)
            return Result<PostCommitCoordinationResult>.Fail(continuationResult.Error);

        return Result<PostCommitCoordinationResult>.Ok(
            CreateDecision(sourceContext, continuationResult.Value));
    }

    private static Result<PostCommitContinuationBehavior> ResolveOwnership(
        ImmutableArray<IPostCommitJob> jobs)
    {
        var continuationJobs = jobs.OfType<IPostCommitContinuationJob>().ToArray();
        if (continuationJobs.Length != jobs.Length)
        {
            return Result<PostCommitContinuationBehavior>.Fail(
                CreateOwnershipError("Every post-commit job must declare continuation ownership."));
        }

        var ownership = continuationJobs
            .Select(job => job.ContinuationBehavior)
            .Distinct()
            .ToArray();
        if (ownership.Length != 1)
        {
            return Result<PostCommitContinuationBehavior>.Fail(
                CreateOwnershipError("Post-commit jobs cannot mix parent and child continuation ownership."));
        }

        return ownership[0] switch
        {
            PostCommitContinuationBehavior.HandoffToChild =>
                Result<PostCommitContinuationBehavior>.Ok(PostCommitContinuationBehavior.HandoffToChild),
            PostCommitContinuationBehavior.ContinueParent =>
                Result<PostCommitContinuationBehavior>.Ok(PostCommitContinuationBehavior.ContinueParent),
            var invalid => Result<PostCommitContinuationBehavior>.Fail(
                CreateOwnershipError($"Undefined post-commit continuation ownership value: {(int)invalid}."))
        };
    }

    private static Error CreateOwnershipError(string message) =>
        Error.Validation(WorkflowErrorCodes.ConfigInvalid, message);

    private static PostCommitCoordinationResult CreateDecision(
        TransitionExecutionContext sourceContext,
        WorkflowExecutionContext? nextContext = null,
        PostCommitFaultRequest? faultRequest = null,
        Error? error = null) =>
        new(sourceContext, nextContext, faultRequest, error);
}
