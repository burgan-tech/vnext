# Task Execution Isolation and Durable Persistence Design

## Problem

`TaskExecutionEngine` currently selects persistence from `TaskTrigger`, creates a random transition id when none is supplied, and persists from inside parallel remote execution. This makes Function and Extension persistence behavior implicit, can produce an invalid `InstanceTasks.TransitionId`, and couples concurrent remote calls to EF Core contexts and ambient units of work. Parallel tasks also mutate one shared `ScriptContext`, so output order depends on completion timing.

## Decisions

1. Introduce `TaskExecutionOrigin` with `Flow`, `Function`, and `Extension` values. `TaskTrigger` continues to describe lifecycle timing; origin alone decides persistence.
2. Only `Flow` executions create `InstanceTask` rows. A Flow execution without a transition id fails before task factory resolution or remote invocation. Function and Extension executions never access `IInstanceTaskRepository`.
3. A Flow task has one stable journal identity per `(TransitionId, TaskId)`. Creation is get-or-create and the database enforces uniqueness. Retry updates the same row instead of creating another attempt row.
4. Journal creation and completion each run in a short transactional `RequiresNew` unit of work. Remote invocation occurs between these scopes and therefore does not use the journal DbContext or transaction.
5. Parallel tasks execute in isolated DI scopes and isolated `ScriptContext` copies. Each branch returns its context delta. The coordinator merges deltas after `Task.WhenAll` in task declaration order, so scheduling cannot change the output.
6. Infrastructure `Result.Fail` in a parallel branch is converted into a concrete failure result and cancels sibling work; it must never be lost because `Result.Value` is null.

## Data Flow

For a Flow task, the engine validates `TransitionId`, opens a short persistence scope to get or create the journal row, commits, invokes the remote executor, and then opens another short persistence scope to save the final request, response, invocation result, status, and business status. The stable unique key makes retry converge on the same row.

Function and Extension executions skip both persistence scopes. They still execute mappings and return output through `ScriptContext`.

For a same-order group, the coordinator snapshots the input context once, clones it per task, runs the branches concurrently, orders outcomes by the original definition index, and merges `Body`, `TaskResponse`, `OutputResponse`, metadata, mutations, and instance-data additions deterministically. A duplicate output key with different values is a validation failure instead of last-writer-wins nondeterminism.

## Error and Durability Semantics

- Missing Flow transition id is a validation failure and no remote call is made.
- A journal creation failure prevents the remote call, because otherwise the side effect would be untracked.
- A completion failure fails the execution. A subsequent workflow retry reuses the stable journal row. Remote endpoints must receive the stable execution id as an idempotency key where the executor protocol supports headers/metadata.
- The local journal is atomic per state transition. Absolute exactly-once behavior across PostgreSQL and an arbitrary remote system is impossible without remote idempotency; this design guarantees at-least-once invocation with idempotent convergence when the remote honors the key.

## Verification

- Function and Extension origins perform zero journal repository calls.
- Flow origin rejects a null transition id.
- Flow retry uses one journal row for the same transition and task.
- Two same-order Flow tasks complete without sharing a DbContext.
- Parallel infrastructure failure is propagated.
- Parallel output merge is deterministic and conflicting keys fail explicitly.
- A delayed remote invocation observes no active journal transaction.
