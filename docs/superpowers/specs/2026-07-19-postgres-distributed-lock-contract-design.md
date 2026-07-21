# PostgreSQL Distributed Lock Contract Design

## Objective

Keep the orchestration application's default `IDistributedLockService` binding on Aether's Dapr implementation while exposing vNext's PostgreSQL lease implementation through an explicit, narrowly scoped interface. Only `ChainReaperHostedService` will consume the PostgreSQL-specific contract.

Inbox and Outbox workers are outside this change because their current SDK processors already use dedicated PostgreSQL lease stores and do not consume `IDistributedLockService`.

## Current Behavior

`WorkflowApiBaseServiceCollectionExtensions.AddDistributedLock` currently reads `WorkflowExecution:LockProvider`. When the value is `Postgres`, it replaces the application-wide `IDistributedLockService` registration with `NpgsqlDistributedLockService`. Consequently every orchestration component that requests the Aether interface receives the PostgreSQL implementation, including transition-chain locking and unrelated infrastructure consumers.

`ChainReaperHostedService` currently requests `IDistributedLockService`, even though its leader-election lease is the specific use case that must always use the PostgreSQL lease implementation.

## Contract and Registration Design

Create `IPostgreSqlDistributedLockService` in the vNext infrastructure lock namespace:

```csharp
public interface IPostgreSqlDistributedLockService : IDistributedLockService
{
}
```

The interface intentionally adds no members. Inheriting `IDistributedLockService` guarantees that the PostgreSQL implementation continues to satisfy the complete Aether lock contract without duplicating method signatures.

`NpgsqlDistributedLockService` will implement `IPostgreSqlDistributedLockService`. The concrete acquisition, fencing, extension, and owner-conditional release behavior remains unchanged.

`AddDistributedLock` will register two independent services:

- `IDistributedLockService` through Aether's existing `AddDaprDistributedLock(lockStoreName)` extension.
- `IPostgreSqlDistributedLockService` as a singleton backed by `NpgsqlDistributedLockService`.

The PostgreSQL registration must not replace, alias, decorate, or otherwise modify the default Aether interface registration. Resolving the two contracts must yield different implementation types.

The `WorkflowExecution:LockProvider` provider-switch branch becomes obsolete and will be removed. Existing configuration values may remain in deployment configuration temporarily, but they no longer affect DI selection.

## Consumer Changes

`ChainReaperHostedService` will replace its constructor dependency and documentation reference from `IDistributedLockService` to `IPostgreSqlDistributedLockService`. Its leader-election algorithm, lock key, lease duration, cancellation, and handle disposal remain unchanged.

All other orchestration consumers continue requesting `IDistributedLockService` and therefore receive Aether's Dapr implementation. This includes transition locking, schema migration orchestration, discovery, idempotency, and other existing consumers.

Inbox and Outbox worker registration and processing code will not change.

## Dependency Direction

The new interface lives beside `NpgsqlDistributedLockService` under `BBT.Workflow.Infrastructure.Execution.Locks`. This keeps the exception contract owned by the vNext PostgreSQL implementation rather than extending Aether's public SDK.

Because `ChainReaperHostedService` already belongs to the orchestration host and references vNext infrastructure types through the composed application, it may depend directly on this explicit infrastructure capability.

## Failure and Lifetime Semantics

Both bindings are singletons, matching the stateless service implementations and their existing ownership model.

Failure behavior does not change:

- Dapr remains responsible for general orchestration locks.
- PostgreSQL connection/configuration failures affect only consumers of `IPostgreSqlDistributedLockService`.
- Chain Reaper continues logging and skipping a cycle when leadership cannot be acquired.
- PostgreSQL lock handles continue using owner-conditional release and lease expiry as crash recovery.

## Verification

Tests will prove:

1. `IDistributedLockService` resolves to Aether's Dapr lock service even when `WorkflowExecution:LockProvider` is set to `Postgres`.
2. `IPostgreSqlDistributedLockService` resolves to `NpgsqlDistributedLockService`.
3. The two service contracts do not resolve to the same implementation instance or type.
4. `ChainReaperHostedService` requires `IPostgreSqlDistributedLockService` and can execute its leader-acquisition path through that contract.
5. Existing real-PostgreSQL `NpgsqlDistributedLockServiceTests` continue passing unchanged in behavior.
6. Inbox and Outbox worker source and DI registrations remain untouched.
7. The targeted orchestration tests and the vNext solution build succeed.

## Compatibility and Scope

The change is source-compatible for callers of `NpgsqlDistributedLockService` and preserves the full Aether lock method contract. It intentionally changes DI behavior for deployments that previously selected the PostgreSQL provider globally through `WorkflowExecution:LockProvider`: general consumers return to the Aether Dapr implementation, while Chain Reaper remains PostgreSQL-backed.

No database migration, lock-table change, configuration migration, Inbox/Outbox change, or Aether framework modification is included.
