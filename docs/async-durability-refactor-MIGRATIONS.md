# Async/Durability Refactor — Required EF Core Migrations

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
| S8 | `Instance` | `ResumePoint jsonb NULL` | `InstancesModelCreatingExtensions` |
| S8 | `InstanceTask` | `IdempotencyKey` + unique index `(InstanceTransitionId, TaskKey)` | InstanceTask config |

## Multi-schema note

Migrations in this repo target schema `public`; the runtime applies them per tenant/flow
schema. Verify the generated `AddColumn`/`CreateIndex` operations carry the correct
`schema:` argument consistent with existing `Instances` migrations.
