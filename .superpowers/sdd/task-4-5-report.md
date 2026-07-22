# Tasks 4-5 Implementation Report

## Outcome

Implemented runner-owned post-commit orchestration with fresh parent settlement and failure recovery. A transition stage now commits and disposes its workflow scope before post-commit handlers run; any returned parent continuation executes as a bounded, fully isolated runner stage. Handoff settlement and post-commit fault recovery acquire the normal parent lock and mutate a freshly reloaded aggregate in a new UoW.

## Authoritative Ordering Clarification

The transition pipeline retains its existing lock ownership boundary. The implemented ordering is:

1. The pipeline executes and returns at the post-commit barrier.
2. Returning from the pipeline disposes its transition lock and clears `ChainLockRegistry` visibility.
3. The execution core returns.
4. The runner stages deferred events and commits the stage UoW.
5. The stage UoW and workflow DI scope are disposed.
6. Post-commit coordination runs in a separate workflow scope.
7. A callback/continuation runs as another fresh runner stage, or parent settlement/fault recovery acquires the normal parent lock in a fresh UoW.

This intentionally does not move the transition lock into `TransitionRunner`, does not register synthetic chain ownership, and does not retain a tracked `Instance` across the handoff.

## Architecture

- `TransitionRunner` uses a bounded 50-stage loop. Every stage creates a new workflow scope, applies `ICurrentUser.ChangeFromHeaders`, begins a `RequiresNew` UoW, runs the core, stages deferred events, commits, and disposes before coordinating post-commit work.
- Failed stage commits prevent post-commit execution.
- `HandoffToChild` never dispatches the stale parent `NextTransition`; it awaits the job and returns output from fresh settlement.
- `ContinueParent` executes `NextContext` as a new runner stage with fresh scoped core/UoW/transition lock dependencies.
- `PostCommitParentSnapshot` carries only identity and immutable request input. Header and route dictionaries are copied into immutable dictionaries and JSON data is cloned; no tracked parent entity is retained.
- `PostCommitParentMutationService` acquires the normal `vnext:{domain}:{flow}:{instanceId}` lock through `ITransitionLockScopeFactory`, starts a `RequiresNew` UoW, reloads correlations and data, re-evaluates terminal state, applies settlement/fault mutations, commits, and then releases the lock.
- Shared `TransitionSettlement` logic keeps the ordinary in-pipeline and fresh post-handoff resolved-status, chain-release, and notification rules aligned.
- `ContinuationSet` now preserves `EndChainRequested` across the barrier.

## TDD Evidence

### RED

- `PostCommitParentMutationServiceTests`: compilation failed only because `PostCommitParentSnapshot` and `PostCommitParentMutationService` did not yet exist.
- `TransitionRunnerPostCommitTests`: 3 failed, 1 passed against the old runner. Failures demonstrated that post-commit never ran, handoff returned/timed out without awaiting the job, and the ordering stopped after `pipeline -> stage-events -> commit`. The already-existing failed-commit guard passed.

### GREEN

- Fresh parent mutation tests: 9 passed.
- Runner post-commit tests: 6 passed.
- Final focused regression set: 43 passed, 0 failed, 0 skipped. This included `TransitionPipelineTests`, `TransitionRunnerEventDurabilityTests`, `TransitionRunnerPostCommitTests`, `PostCommitTransitionCoordinatorTests`, `PostCommitParentMutationServiceTests`, `PostCommitExecutorTests`, and `WorkflowExecutionServiceTests`.
- `BBT.Workflow.Domain.csproj`: build succeeded with 0 warnings and 0 errors.
- `BBT.Workflow.Application.csproj`: build succeeded with 32 existing warnings and 0 errors.
- `git diff --check`: passed.

## Regression Coverage

- Exact success order: `pipeline -> stage-events -> commit -> post-commit -> fresh-next-stage -> commit`.
- Stage scope/UoW disposal and cleared transition lock/`ChainLockRegistry` before post-commit.
- No post-commit execution after a failed commit.
- Handoff awaits job completion and ignores stale parent continuation.
- Separate scoped execution core, UoW manager, lock factory, user scope, and transition lock per continuation stage.
- Post-commit fault request returns fresh authoritative output.
- Post-commit error without a fault request is returned unchanged.
- Maximum runner-stage depth prevents malformed loops.
- Lock-before-UoW-before-reload-before-write-before-commit-before-unlock ordering.
- Lock conflict performs no reload, UoW, or write.
- Source aggregate remains untouched; only the fresh repository aggregate is mutated.
- Completed, Faulted, and Passive authoritative results are not overwritten.
- Active SubFlow correlation prevents invalid Active settlement.
- Resolved status, chain release, and notification use freshly reloaded status/correlation/state.
- Snapshot dictionaries and JSON payload cannot be changed through later source mutation.

## Files Changed

- `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`
- `src/BBT.Workflow.Application/Execution/PostCommit/IPostCommitParentMutationService.cs`
- `src/BBT.Workflow.Application/Execution/PostCommit/PostCommitParentMutationService.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionSettlement.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs`
- `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Context/ContinuationSet.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Context/PipelineDirectives.cs`
- `test/BBT.Workflow.Application.Tests/Execution/PostCommit/PostCommitParentMutationServiceTests.cs`
- `test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerPostCommitTests.cs`
- `test/BBT.Workflow.Application.Tests/Execution/Services/WorkflowExecutionServiceTests.cs`

The pre-existing `TransitionRunnerEventDurabilityTests` already covered event staging/commit failure durability and required no source change.

## Self-Review and Concerns

- Reviewed the full owned diff for stale entity retention, lock/UoW disposal order, terminal idempotency, failure propagation, user/header propagation, and scope reuse.
- The repository setup script printed a success path despite missing `wget`; the required `NETStandard.Library.Ref` package was already present, so builds and tests proceeded normally.
- The focused build/test run emitted existing `NU1903`, `NU1510`, nullability, XML documentation, and EF raw-SQL analysis warnings. No new compiler errors or task-specific warnings were introduced.
- The full solution test suite was not run; verification was deliberately limited to the requested focused serial test set and affected production builds.
- The four unrelated pre-existing dirty files were preserved and excluded from staging.
