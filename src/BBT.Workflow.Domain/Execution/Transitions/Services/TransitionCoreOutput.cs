using BBT.Aether.Events;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Output from core transition execution containing the transition output
/// and deferred domain events collected during pipeline execution.
/// Deferred events are published explicitly by TransitionRunner after UoW commit.
/// </summary>
/// <param name="Output">The transition output containing instance ID and status.</param>
/// <param name="DeferredEvents">Domain events collected during pipeline execution for post-commit publishing.</param>
/// <param name="Continuations">Immutable snapshot of pending continuation work (next transition, post-commit jobs, resolved status). Projected from the pipeline directives for runner-owned orchestration after commit.</param>
/// <param name="ExecutionContext">The originating execution context, retained with its directives intact for runner-owned post-commit orchestration.</param>
public sealed record TransitionCoreOutput(
    TransitionOutput Output,
    IReadOnlyList<DomainEventEnvelope> DeferredEvents,
    ContinuationSet Continuations,
    TransitionExecutionContext ExecutionContext);
