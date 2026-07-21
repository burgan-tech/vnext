# Task Execution Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Function and Extension tasks non-persistent and make parallel Flow task execution isolated, deterministic, and idempotently journaled.

**Architecture:** Persistence selection uses an explicit execution origin. Flow journal writes use short transactional `RequiresNew` scopes around a unique `(TransitionId, TaskId)` row, while remote invocation runs between them. Parallel branches use private contexts and merge their deltas in definition order.

**Tech Stack:** .NET 10, C#, xUnit, NSubstitute, Shouldly, EF Core 10, PostgreSQL, Aether Unit of Work.

## Global Constraints

- Preserve existing user changes in the dirty worktree.
- Only Flow executions persist `InstanceTask` rows.
- Function and Extension executions never touch task persistence.
- Flow executions require a real persisted `InstanceTransition` id.
- Remote calls must not run inside the journal persistence transaction.
- Parallel output must be deterministic by definition order.

---

### Task 1: Explicit execution origin and transition validation

**Files:**
- Modify: `src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs`
- Modify: `src/BBT.Workflow.Domain/Tasks/Coordinator/ITaskCoordinator.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/ITaskExecutionEngine.cs`
- Modify: `src/BBT.Workflow.Domain/Tasks/Persistence/ITaskPersistenceStrategy.cs`
- Modify: `src/BBT.Workflow.Domain/Tasks/Persistence/ITaskPersistenceStrategyFactory.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Persistence/**`
- Modify: Flow, Function, and Extension coordinator call sites
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Persistence/Strategies/TaskPersistenceStrategyTests.cs`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskExecutionEngineTests.cs`

**Interfaces:**
- Produces: `TaskExecutionOrigin { Flow, Function, Extension }`
- Produces: coordinator and engine methods accepting `TaskExecutionOrigin origin` separately from `TaskTrigger taskTrigger`

- [ ] **Step 1: Write failing tests** asserting origin-based strategy selection, zero persistence for Function/Extension, and Flow null-transition rejection before executor invocation.
- [ ] **Step 2: Run** `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskPersistenceStrategyTests|FullyQualifiedName~TaskExecutionEngineTests"` and verify failures describe the missing origin API and validation.
- [ ] **Step 3: Implement** the enum, signatures, call-site origin values, origin-based strategies, and fail-fast validation. Remove the random transition-id fallback.
- [ ] **Step 4: Re-run the focused tests** and verify they pass.

### Task 2: Stable idempotent Flow journal

**Files:**
- Modify: `src/BBT.Workflow.Domain/Instances/IInstanceTaskRepository.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceTaskRepository.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Data/InstancesModelCreatingExtensions.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Persistence/Strategies/StandardTaskPersistenceStrategy.cs`
- Modify: `src/BBT.Workflow.Domain/Tasks/Persistence/ITaskPersistenceStrategy.cs`
- Create: EF Core migration for unique `(TransitionId, TaskId)` index
- Test: application persistence tests and infrastructure repository tests

**Interfaces:**
- Produces: `Task<InstanceTask> GetOrCreateAsync(Guid transitionId, string taskId, CancellationToken cancellationToken)` on the Flow persistence strategy.
- Produces: repository lookup by transition id and task id.

- [ ] **Step 1: Write a failing test** proving two creation attempts return/update one journal identity.
- [ ] **Step 2: Run the focused test** and verify duplicate rows are currently possible.
- [ ] **Step 3: Implement** repository lookup, transactional `RequiresNew` get-or-create, completion update, and the unique index/migration. Treat a unique-race insert as a reload of the winning row.
- [ ] **Step 4: Run application and PostgreSQL infrastructure tests** and verify one row remains.

### Task 3: Parallel failure propagation

**Files:**
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs`
- Create: `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskCoordinatorTests.cs`

**Interfaces:**
- Consumes: origin-aware engine API from Task 1.
- Produces: deterministic conversion of `Result.Fail` into a blocking `TasksExecutionResult`.

- [ ] **Step 1: Write a failing test** with two same-order tasks where one engine call returns `Result.Fail`; assert the coordinator returns failure and records the correct task key.
- [ ] **Step 2: Run the test** and verify current code loses the null `Result.Value` failure.
- [ ] **Step 3: Implement** indexed outcomes and explicit infrastructure-failure conversion without shared `firstFailure` mutation.
- [ ] **Step 4: Re-run the test** and verify it passes.

### Task 4: Private parallel contexts and deterministic merge

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/Models.cs`
- Create: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionContextSnapshot.cs`
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskCoordinatorTests.cs`

**Interfaces:**
- Produces: a context snapshot/clone operation and an ordered context delta merge operation.

- [ ] **Step 1: Write failing tests** where faster second task completes first but merged Body/TaskResponse order follows definitions, plus a test where conflicting output keys fail.
- [ ] **Step 2: Run the tests** and verify the shared context is timing-dependent.
- [ ] **Step 3: Implement** private branch contexts, immutable deltas, ordered merge, and explicit conflict detection.
- [ ] **Step 4: Re-run coordinator and scripting tests** and verify deterministic output.

### Task 5: Transaction-boundary integration verification

**Files:**
- Add or modify PostgreSQL integration tests under `test/BBT.Workflow.Infrastructure.Tests`
- Modify persistence implementation only if the integration test exposes a scope leak.

**Interfaces:**
- Consumes: short `RequiresNew` journal operations and stable unique key.

- [ ] **Step 1: Write a PostgreSQL integration test** that persists a transition, runs two delayed same-order Flow tasks, and records whether a journal transaction is active during the delay.
- [ ] **Step 2: Run the test** against the repository test fixture and verify the pre-fix failure mode is reproduced or guarded.
- [ ] **Step 3: Make the minimum boundary adjustment** needed so journal transactions are complete before invocation and each result write is isolated.
- [ ] **Step 4: Run** the focused application tests, infrastructure tests, and `dotnet build BBT.Workflow.slnx --no-restore`; record any unrelated dirty-worktree failures separately.
