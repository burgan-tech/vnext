# Async/Durability Refactor — Required EF Core Migrations

> **STATUS: superseded, kept for history.** The `ChainToken`/`ChainHeartbeat`/`ResumePoint`
> columns this draft specifies were added (`20260611200135_Instance_LockChainToken` and
> follow-ups) and then dropped before the design they backed ever shipped as documented
> runtime behavior (`20260810181548_DropInstanceChainTokenColumns`,
> `20260812053101_DropInstanceResumePointColumn`). Current code has no `ChainToken`,
> `ChainReaperService`, or chain-ownership gate — concurrency is the plain Busy status CAS
> described in [`.claude/rules/vnext-workflow-developer.md`](../.claude/rules/vnext-workflow-developer.md)
> § "Locking — one lock, at the status change". Do **not** generate the migrations below;
> they do not correspond to anything `InstancesModelCreatingExtensions` maps today. See
> [Async Transition Execution Modes](architecture/async-transition-execution-modes.md) §
> "What used to be here" for the current state. The rest of this file is left as originally
> drafted, for anyone reconstructing why those migrations once existed.

> These migrations were **NOT generated** in the drafting environment (no .NET SDK /
> restricted network). They **must be generated with the EF Core tooling** (which also
> updates the `WorkflowDbContextModelSnapshot` + `.Designer` files) before the branch can
> run — the new entity properties are mapped in `InstancesModelCreatingExtensions` /
> `InstanceTask` config, so the columns are required at runtime.

Generate from the repo root (Infrastructure project, Workflow DB context):

```bash
dotnet ef migrations add AddInstanceChainToken \
  --project src/BBT.Workflow.Infrastructure \
  --startup-project src/BBT.Workflow.DbMigrator \
  --context WorkflowDbContext

dotnet ef migrations add AddInstanceChainHeartbeat \
  --project src/BBT.Workflow.Infrastructure \
  --startup-project src/BBT.Workflow.DbMigrator \
  --context WorkflowDbContext

dotnet ef migrations add AddInstanceResumePoint \
  --project src/BBT.Workflow.Infrastructure \
  --startup-project src/BBT.Workflow.DbMigrator \
  --context WorkflowDbContext

dotnet ef migrations add AddInstanceTaskIdempotencyKey \
  --project src/BBT.Workflow.Infrastructure \
  --startup-project src/BBT.Workflow.DbMigrator \
  --context WorkflowDbContext
```

(Adjust `--startup-project`/`--context` to match the repo's actual migration host.)

## Schema additions by spec

| Spec | Entity | Column / index | Mapping location |
|------|--------|----------------|------------------|
| S6 | `Instance` | `ChainToken uuid NULL` + `IX_Instances_ChainToken` (filtered `ChainToken IS NOT NULL`) | `InstancesModelCreatingExtensions` |
| S7 | `Instance` | `ChainHeartbeatAt timestamptz NULL` + filtered index `(Status, ChainHeartbeatAt)` | `InstancesModelCreatingExtensions` |
| S8 | `Instance` | `ResumePointStepOrder int NULL` | `InstancesModelCreatingExtensions` |
| S8 (deferred) | `InstanceTask` | per-task commit + execution-side idempotency key (`InstanceId:TransitionId:TaskId`) — needs TaskCoordinator restructuring + shared Execution contract change; do with compiler in the loop | TaskCoordinator / Execution.Abstractions |

## Multi-schema note

Migrations in this repo target schema `public`; the runtime applies them per tenant/flow
schema. Verify the generated `AddColumn`/`CreateIndex` operations carry the correct
`schema:` argument consistent with existing `Instances` migrations.
