using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution;

/// <summary>
/// Immutable snapshot of "what should happen next" after a single transition has
/// executed its steps. Projected from <see cref="PipelineDirectives"/> as a pure
/// (non-consuming) read so that orchestration concerns (auto-chain, post-commit
/// work, deferred status, resume point) can be reasoned about as data rather than
/// by mutating the directives accumulator.
/// </summary>
/// <remarks>
/// This value object is the boundary between single-transition execution and
/// continuation realization (inline vs. enqueue). The subflow start / forward
/// continuations are carried as <see cref="IPostCommitJob"/> instances
/// (e.g. <see cref="StartSubflowJob"/>, <see cref="ForwardToSubflowJob"/>),
/// mirroring how the pipeline already accumulates them.
/// </remarks>
/// <param name="Next">The next transition to execute in an auto-chain, or null when the chain is complete.</param>
/// <param name="PostCommitJobs">Side-effect jobs to run after commit (subflow start/forward, etc.).</param>
/// <param name="ResolvedStatus">Deferred instance status to apply after post-commit work completes, or null.</param>
/// <param name="ResumeFromOrder">Lifecycle order to resume execution from (subflow resume / replan), or null.</param>
/// <param name="TerminalReached">Whether the pipeline reached a terminal state.</param>
/// <param name="Epilogue">The epilogue execution mode (Run/DispatchOnly/Skip).</param>
public sealed record ContinuationSet(
    NextTransitionRequest? Next,
    IReadOnlyList<IPostCommitJob> PostCommitJobs,
    InstanceStatus? ResolvedStatus,
    int? ResumeFromOrder,
    bool TerminalReached,
    EpilogueMode Epilogue)
{
    /// <summary>
    /// An empty continuation set representing "nothing left to do".
    /// </summary>
    public static readonly ContinuationSet Empty =
        new(null, Array.Empty<IPostCommitJob>(), null, null, false, EpilogueMode.Run);

    /// <summary>
    /// Gets a value indicating whether there is any actionable continuation work
    /// (a next transition to chain or post-commit jobs to run).
    /// </summary>
    public bool HasWork => Next is not null || PostCommitJobs.Count > 0;
}
