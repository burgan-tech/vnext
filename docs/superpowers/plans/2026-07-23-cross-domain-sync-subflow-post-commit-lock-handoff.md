# Cross-Domain Sync Subflow Post-Commit Lock Handoff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Track progress with the checkboxes below.

**Goal:** Release the originating parent lock before synchronous cross-domain child invocation while preserving strict `sync=true` behavior, SubFlow blocking semantics, and independent SubProcess semantics.

**Architecture:** `TransitionPipeline` stops at a post-commit barrier and returns an immutable continuation snapshot. `TransitionRunner` commits the parent UoW, executes remote post-commit jobs after the pipeline lock has been disposed, then either returns authoritative state or starts a parent continuation in a fresh scope/UoW/lock. Any post-handoff parent mutation reloads the aggregate under the normal parent lock.

**Tech Stack:** .NET 10, C#, xUnit, NSubstitute/Moq, Aether UoW and event bus, existing `IDistributedLockService` abstraction.

## Non-Negotiable Constraints

- Keep `IDistributedLockService` and `IDistributedLockHandle` unchanged.
- Do not add a dependency on `NpgsqlDistributedLockService` or any provider-specific API.
- `sync=true` waits for the synchronous child call and required blocking-parent processing.
- SubFlow completion/cancellation may resume the parent; fault follows its resolved boundary action.
- SubProcess Completed/Faulted only close the correlation and never resume the parent.
- After lock handoff, never mutate the original tracked `TransitionExecutionContext.Instance`.
- Keep enqueued continuations inside the originating UoW so transition-per-job remains atomic.
- Preserve the unrelated working-tree changes listed at implementation start; stage task files explicitly.

## Task 1: Encode continuation ownership on subflow post-commit jobs

**Files:**

- Modify: `src/BBT.Workflow.Domain/Execution/PostCommit/IPostCommitJob.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/HandleSubFlowStep.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ForwardToActiveSubflowStep.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Execution/Transitions/Context/ContinuationsTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/HandleSubFlowStepTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/ForwardToActiveSubflowStepTests.cs`

- [ ] Add failing tests that distinguish blocking SubFlow from independent SubProcess jobs.

Add assertions equivalent to:

```csharp
job.ContinuationBehavior.ShouldBe(PostCommitContinuationBehavior.HandoffToChild);
```

for SubFlow/forward jobs, and:

```csharp
job.ContinuationBehavior.ShouldBe(PostCommitContinuationBehavior.ContinueParent);
```

for SubProcess start jobs.

- [ ] Run the narrow tests and confirm compilation/assertion failure before production changes.

```bash
dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ContinuationsTests"
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~HandleSubFlowStepTests|FullyQualifiedName~ForwardToActiveSubflowStepTests"
```

Expected: failure because continuation behavior is not represented yet.

- [ ] Add the domain contract.

Use this shape in `IPostCommitJob.cs`:

```csharp
public enum PostCommitContinuationBehavior
{
    HandoffToChild = 0,
    ContinueParent = 1
}

public interface IPostCommitContinuationJob : IPostCommitJob
{
    PostCommitContinuationBehavior ContinuationBehavior { get; }
}
```

Make `StartSubflowJob` accept the behavior explicitly and make `ForwardToSubflowJob` expose `HandoffToChild`. Do not infer behavior later from a stale execution context.

- [ ] Select the behavior when the job is created.

`HandleSubFlowStep` must map the configured target flow type:

```csharp
var behavior = context.Target!.SubFlow!.Type.Equals(SubFlowType.SubProcess)
    ? PostCommitContinuationBehavior.ContinueParent
    : PostCommitContinuationBehavior.HandoffToChild;
```

Use `context.Target!.SubFlow!.Type.Equals(SubFlowType.SubProcess)` to select `ContinueParent`; all other starts use `HandoffToChild`. Forwarding to an already active blocking SubFlow always uses `HandoffToChild`.

- [ ] Update all direct job constructors in tests and run the narrow tests green.

- [ ] Commit only Task 1 files.

```bash
git add src/BBT.Workflow.Domain/Execution/PostCommit/IPostCommitJob.cs \
  src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/HandleSubFlowStep.cs \
  src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ForwardToActiveSubflowStep.cs \
  test/BBT.Workflow.Domain.Tests/Execution/Transitions/Context/ContinuationsTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/HandleSubFlowStepTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/ForwardToActiveSubflowStepTests.cs
git commit -m "feat: encode subflow continuation ownership"
```

## Task 2: Make the pipeline stop at the post-commit barrier

**Files:**

- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs`
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Services/TransitionCoreOutput.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Services/WorkflowExecutionService.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/TransitionPipelineTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Services/WorkflowExecutionServiceTests.cs`

- [ ] Add a failing pipeline test proving jobs are returned without being consumed or executed.

Arrange one post-commit job, run the pipeline, and assert:

```csharp
await postCommitExecutor.DidNotReceiveWithAnyArgs()
    .ExecuteAsync(default!, default!, default);
result.Value!.Directives.PostCommitJobs.Count.ShouldBe(1);
```

Also capture the post-commit handler boundary and assert `ChainLockRegistry.IsHeld(parentKey)` is false only after `RunAsync` has returned.

- [ ] Add a failing transition-core-output test proving the runner receives the execution context/continuation snapshot needed after commit.

- [ ] Run the narrow tests and record the expected red result.

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionPipelineTests|FullyQualifiedName~WorkflowExecutionServiceTests"
```

- [ ] Change `TransitionPipeline.RunChainAsync` so a non-empty post-commit collection is a terminal barrier for that lock scope.

Required behavior:

1. Do not call `ConsumePostCommitJobs()`.
2. Do not invoke `IPostCommitExecutor` inside `TransitionPipeline`.
3. For `EnqueueContinuations=true`, dispatch the enqueue continuation before returning so it is committed atomically with the transition.
4. For inline mode, leave `NextTransition` intact for runner-owned orchestration.
5. Return before resolved-status, chain-ownership, or notification writes that would rely on state after a remote callback.

The no-post-commit path must retain the existing in-lock auto-chain loop and lock-extension behavior.

- [ ] Remove `IPostCommitExecutor` from the pipeline constructor and remove the lock-held post-commit fault mutation path. Keep ordinary pipeline execution failure handling unchanged.

- [ ] Add `TransitionExecutionContext ExecutionContext` to `TransitionCoreOutput`; update XML documentation and construction in `WorkflowExecutionService`.

- [ ] Make the existing output projection helper `internal static` only if the coordinator/runner needs to rebuild the final `TransitionOutput`; do not duplicate mapping logic.

- [ ] Run the narrow tests green and commit Task 2 files.

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs \
  src/BBT.Workflow.Domain/Execution/Transitions/Services/TransitionCoreOutput.cs \
  src/BBT.Workflow.Application/Execution/Services/WorkflowExecutionService.cs \
  test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/TransitionPipelineTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Services/WorkflowExecutionServiceTests.cs
git commit -m "refactor: expose transition post-commit barrier"
```

## Task 3: Add a runner-owned post-commit coordinator

**Files:**

- Create: `src/BBT.Workflow.Application/Execution/PostCommit/PostCommitTransitionCoordinator.cs`
- Create: `src/BBT.Workflow.Application/Execution/PostCommit/IPostCommitTransitionCoordinator.cs`
- Create: `src/BBT.Workflow.Domain/Execution/PostCommit/PostCommitCoordinationResult.cs`
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/PostCommit/PostCommitTransitionCoordinatorTests.cs`

- [ ] Add failing coordinator tests for the three outcomes.

1. No jobs: return the original context with no next execution context.
2. `HandoffToChild`: execute jobs and never dispatch the stale outer continuation.
3. `ContinueParent`: execute jobs, then use `ContinuationDispatcher` in `Inline` mode to produce a fresh `WorkflowExecutionContext` when `NextTransition` exists.

Also verify job execution happens with no registered parent key:

```csharp
observedParentLockHeld.ShouldBeFalse();
```

- [ ] Run the coordinator tests and confirm they fail because the type is absent.

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~PostCommitTransitionCoordinatorTests"
```

- [ ] Implement a small result contract that carries only orchestration decisions.

```csharp
public sealed record PostCommitCoordinationResult(
    TransitionExecutionContext SourceContext,
    WorkflowExecutionContext? NextContext,
    PostCommitFaultRequest? FaultRequest,
    Error? Error);
```

Use the repository's concrete Aether error type and nullable conventions. Return a failed `Result<T>` for executor errors without a fault request; carry a fault request for fresh-state recovery.

- [ ] Implement `PostCommitTransitionCoordinator`.

Coordinator rules:

- Copy jobs to a local immutable list, then consume once to prevent re-execution.
- Execute them through the existing `IPostCommitExecutor`.
- Determine continuation ownership from the job contract; reject mixed ownership in one barrier as configuration-invalid unless an existing invariant proves only one job can exist.
- Never read or write `SourceContext.Instance` as authoritative after `ExecuteAsync` starts.
- Never dispatch inline continuation for `HandoffToChild`.
- For `ContinueParent`, dispatch inline only after all jobs succeed.

- [ ] Register the coordinator as scoped and run coordinator plus existing executor tests green.

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~PostCommitTransitionCoordinatorTests|FullyQualifiedName~PostCommitExecutorTests|FullyQualifiedName~DefaultPostCommitFailurePolicyTests"
```

- [ ] Commit Task 3 files.

```bash
git add src/BBT.Workflow.Application/Execution/PostCommit/PostCommitTransitionCoordinator.cs \
  src/BBT.Workflow.Application/Execution/PostCommit/IPostCommitTransitionCoordinator.cs \
  src/BBT.Workflow.Domain/Execution/PostCommit/PostCommitCoordinationResult.cs \
  src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs \
  test/BBT.Workflow.Application.Tests/Execution/PostCommit/PostCommitTransitionCoordinatorTests.cs
git commit -m "feat: coordinate transition work after commit"
```

## Task 4: Enforce commit → post-commit → fresh continuation ordering in the runner

**Files:**

- Modify: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerEventDurabilityTests.cs`
- Create: `test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerPostCommitTests.cs`

- [ ] Add failing ordering tests with a shared call log.

Assert exact ordering:

```text
pipeline
stage-events
commit
post-commit
fresh-next-stage
```

Cover these cases:

- successful commit runs post-commit;
- failed commit never runs post-commit;
- `HandoffToChild` returns only after the job completes and does not run stale parent continuation;
- `ContinueParent` executes the next transition as a second runner stage;
- the second stage obtains a fresh scoped `IWorkflowExecutionCore`, `IUnitOfWork`, and transition lock.

- [ ] Run the runner tests and confirm the ordering assertions fail with current behavior.

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionRunnerEventDurabilityTests|FullyQualifiedName~TransitionRunnerPostCommitTests"
```

- [ ] Refactor `TransitionRunner.RunAsync` into a bounded stage loop.

Each stage must:

1. Open a new workflow DI scope.
2. Open `RequiresNew` UoW.
3. Run `IWorkflowExecutionCore` and collect its `TransitionCoreOutput`.
4. Stage deferred events.
5. Commit the UoW.
6. Dispose the workflow scope/UoW so the pipeline lock and tracked DbContext cannot leak.
7. Call `IPostCommitTransitionCoordinator` outside that stage scope.
8. If a `NextContext` is returned, repeat from step 1.

Use the existing maximum-chain-depth protection or add an equivalent runner-stage bound so a malformed continuation cannot loop forever.

- [ ] Keep user/header propagation identical in every stage.

`ICurrentUser.ChangeFromHeaders(context.Headers)` must be established inside each fresh workflow scope, including the post-SubProcess continuation stage.

- [ ] Build the final response only after coordinator work settles.

For a `HandoffToChild` result, do not return `coreOutput.Output` blindly; route authoritative output construction through the fresh-state settlement service introduced in Task 5. Until Task 5 lands, keep this test red or use an interface mock rather than introducing a stale read.

- [ ] Run runner tests green for the ordering and continuation cases that do not require Task 5, then commit.

```bash
git add src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs \
  test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerEventDurabilityTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerPostCommitTests.cs
git commit -m "refactor: run post-commit work after transition commit"
```

## Task 5: Add fresh-lock parent settlement and post-commit failure recovery

**Files:**

- Create: `src/BBT.Workflow.Application/Execution/PostCommit/IPostCommitParentMutationService.cs`
- Create: `src/BBT.Workflow.Application/Execution/PostCommit/PostCommitParentMutationService.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/PostCommit/PostCommitParentMutationServiceTests.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerPostCommitTests.cs`

- [ ] Add failing tests that reject stale aggregate mutation.

Use two different instance objects: the source context snapshot and the repository's fresh authoritative instance. Assert that settlement/fault changes only the reloaded instance.

Required cases:

- normal parent lock is acquired before repository load/mutation;
- lock conflict returns `WorkflowErrors.InstanceLockConflict` and performs no write;
- settlement reloads current status/correlation after a synchronous callback;
- failure recovery adds incident/fault only when the authoritative instance still permits it;
- an already Completed/Faulted terminal result from a successful callback is not overwritten;
- resolved status, end-chain ownership, and state notification run against fresh state when still applicable.

- [ ] Run the new tests red.

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~PostCommitParentMutationServiceTests|FullyQualifiedName~TransitionRunnerPostCommitTests"
```

- [ ] Implement `IPostCommitParentMutationService` with explicit operations.

Use a contract equivalent to:

```csharp
Task<Result<TransitionOutput>> SettleAsync(
    PostCommitParentSnapshot source,
    ContinuationSet continuations,
    CancellationToken cancellationToken);

Task<Result<TransitionOutput>> FaultAsync(
    PostCommitParentSnapshot source,
    PostCommitFaultRequest request,
    CancellationToken cancellationToken);
```

`PostCommitParentSnapshot` must contain identifiers and immutable input needed to reload; it must not contain the tracked `Instance` entity.

- [ ] Implement fresh lock/UoW/reload ordering.

The service must:

1. Resolve the normal parent lock key using the existing lock-key builder/factory.
2. Acquire through `ITransitionLockScopeFactory`.
3. Begin a fresh `RequiresNew` UoW.
4. Reload the parent through `IInstanceRepository`.
5. Re-evaluate current terminal/idempotency state.
6. Apply only still-valid settlement or fault mutations.
7. Stage/publish generated deferred events using the repository's established event path.
8. Commit before releasing the lock.

Do not call `ChainLockRegistry.Register` to simulate ownership transfer. The lock acquisition must be real and provider-agnostic.

- [ ] Integrate with `TransitionRunner`.

- `HandoffToChild`: call `SettleAsync` after the child invocation and return its authoritative output; never dispatch the old `NextTransition`.
- `ContinueParent` with `NextContext`: run the fresh stage, then settle normally at the final resting stage.
- post-commit executor fault request: call `FaultAsync` and return authoritative output.
- post-commit error without a fault request: return the error unchanged.

- [ ] Register the service and run all Task 5 tests green.

- [ ] Commit only Task 5 files.

```bash
git add src/BBT.Workflow.Application/Execution/PostCommit/IPostCommitParentMutationService.cs \
  src/BBT.Workflow.Application/Execution/PostCommit/PostCommitParentMutationService.cs \
  src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs \
  src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs \
  test/BBT.Workflow.Application.Tests/Execution/PostCommit/PostCommitParentMutationServiceTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerPostCommitTests.cs
git commit -m "fix: settle post-commit parent state under fresh lock"
```

## Task 6: Prove cross-domain terminal callbacks and SubProcess semantics

**Files:**

- Create: `test/BBT.Workflow.Application.Tests/SubFlow/CrossDomainSyncSubflowLockHandoffTests.cs`
- Modify: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs`
- Modify: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs`
- Modify: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCancellationServiceTests.cs`
- Modify: `test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerPostCommitTests.cs`

- [ ] Build a process-boundary test double that deliberately does not inherit `AsyncLocal` state.

The double must invoke the child terminal callback from a clean execution context, or explicitly suppress execution-context flow:

```csharp
using (ExecutionContext.SuppressFlow())
{
    callbackTask = Task.Run(() => callback(cancellationToken), cancellationToken);
}
await callbackTask;
```

Do not unregister the parent key manually inside the callback; the production ordering must ensure the originating pipeline has already returned and released the real lock.

- [ ] Add failing synchronous blocking SubFlow tests.

Cover independently:

- Completed callback acquires the parent lock, closes correlation, and finishes required resume processing before the original `sync=true` call returns.
- Faulted callback acquires the parent lock and completes the configured parent error-boundary action before return.
- Canceled callback acquires the parent lock and completes required resume processing before return.
- No case observes `Parent instance terminal lock could not be acquired` solely because the originating parent request still owns the lock.

- [ ] Add failing independent SubProcess tests.

Cover:

- Completed closes correlation and never calls parent resume.
- Faulted closes correlation and never calls parent resume.
- Parent auto-continuation starts after the synchronous SubProcess call in a fresh lock/UoW.
- Parent continuation preserves the correlation terminal outcome written by the callback.
- duplicate terminal delivery remains a no-op.

- [ ] Run only this subflow regression slice red, implement only integration corrections exposed by it, then run green.

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~CrossDomainSyncSubflowLockHandoffTests|FullyQualifiedName~SubflowCompletionServiceTests|FullyQualifiedName~SubflowFaultServiceTests|FullyQualifiedName~SubflowCancellationServiceTests"
```

- [ ] Confirm terminal services retain their own normal parent-lock acquisition. Do not special-case cross-domain calls inside those services.

- [ ] Commit regression coverage and any narrowly required integration fixes.

```bash
git add test/BBT.Workflow.Application.Tests/SubFlow/CrossDomainSyncSubflowLockHandoffTests.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs \
  test/BBT.Workflow.Application.Tests/SubFlow/SubflowCancellationServiceTests.cs \
  test/BBT.Workflow.Application.Tests/Execution/Services/TransitionRunnerPostCommitTests.cs
git diff --cached --stat
git commit -m "test: cover cross-domain sync subflow lock handoff"
```

If a regression exposes a production defect, amend the smallest owning implementation task with that explicit source path instead of broadly staging `src`.

## Task 7: Regression verification and documentation consistency

**Files:**

- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs`
- Modify: `src/BBT.Workflow.Domain/Execution/PostCommit/IPostCommitJob.cs`
- Verify: `docs/superpowers/specs/2026-07-23-cross-domain-sync-subflow-post-commit-lock-handoff-design.md`

- [ ] Search for obsolete lock-held post-commit claims.

```bash
rg -n "inside lock scope|under a single lock|post-commit phase|lock is held|after the distributed lock is released" \
  src/BBT.Workflow.Application/Execution \
  src/BBT.Workflow.Domain/Execution
```

Update comments so they describe the actual boundary: pipeline lock ends, UoW commits, then post-commit executes.

- [ ] Run formatting/build checks for touched projects.

```bash
dotnet build src/BBT.Workflow.Domain/BBT.Workflow.Domain.csproj --no-restore
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore
git diff --check
```

- [ ] Run targeted test projects.

```bash
dotnet test test/BBT.Workflow.Domain.Tests --no-restore
dotnet test test/BBT.Workflow.Application.Tests --no-restore
```

Record exact pass/fail/skip counts. If failures are unrelated baseline failures, identify each failing test and rerun the new regression filters separately; do not describe the full suite as green.

- [ ] Run focused final regression filters.

```bash
dotnet test test/BBT.Workflow.Application.Tests --no-restore --filter "FullyQualifiedName~TransitionPipelineTests|FullyQualifiedName~TransitionRunnerPostCommitTests|FullyQualifiedName~PostCommitTransitionCoordinatorTests|FullyQualifiedName~PostCommitParentMutationServiceTests|FullyQualifiedName~CrossDomainSyncSubflowLockHandoffTests|FullyQualifiedName~SubflowCompletionServiceTests|FullyQualifiedName~SubflowFaultServiceTests|FullyQualifiedName~SubflowCancellationServiceTests"
```

- [ ] Inspect scope and preserve the user's pre-existing changes.

```bash
git status --short
git diff --stat HEAD
git diff -- Directory.Build.props \
  orchestration/BBT.Workflow.Orchestration.HttpApi.Host/HostedServices/ChainReaperHostedService.cs \
  orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs \
  orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json
```

These four files must remain untouched by this implementation unless the user separately expands scope.

- [ ] Commit comment-only corrections if any.

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs \
  src/BBT.Workflow.Application/Execution/Services/TransitionRunner.cs \
  src/BBT.Workflow.Domain/Execution/PostCommit/IPostCommitJob.cs
git commit -m "docs: align post-commit lock lifecycle comments"
```

- [ ] Before reporting completion, use `superpowers:verification-before-completion`, quote the commands actually run, and report any remaining baseline or infrastructure failures separately.

## Completion Criteria

- A synchronous cross-domain Completed, Faulted, or Canceled callback can acquire the parent lock because the originating pipeline has released it.
- The original `sync=true` request waits until child invocation and required blocking-parent processing finish.
- SubProcess terminal callbacks only close correlation; parent continuation remains independent.
- Every post-handoff parent write uses a fresh scope, UoW, repository load, and normal distributed lock.
- Enqueued continuations remain atomic with their originating transition.
- `IDistributedLockService` is unchanged and no concrete lock provider appears in the new code.
- Existing same-domain reentrancy tests remain valid for paths outside this handoff boundary.
