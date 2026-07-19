# PostgreSQL Distributed Lock Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve Aether's Dapr-backed `IDistributedLockService` as the general orchestration lock while exposing vNext's PostgreSQL lease implementation through a dedicated contract consumed only by Chain Reaper.

**Architecture:** Add an empty derived interface beside the vNext PostgreSQL implementation so it inherits the complete Aether lock contract. Register Dapr and PostgreSQL locks under separate DI service types, then change Chain Reaper to request only the PostgreSQL-specific type. Inbox and Outbox remain unchanged because their SDK processors use their own lease stores.

**Tech Stack:** .NET 10, Microsoft.Extensions.DependencyInjection, Aether distributed locks, Npgsql, xUnit, Shouldly, NSubstitute.

## Global Constraints

- `IDistributedLockService` must always resolve through Aether's `AddDaprDistributedLock` registration in orchestration.
- `IPostgreSqlDistributedLockService` must inherit `IDistributedLockService` without duplicating members.
- `NpgsqlDistributedLockService` behavior and lifetime remain unchanged.
- Only `ChainReaperHostedService` changes to the PostgreSQL-specific contract.
- Inbox and Outbox source and registrations must not change.
- `WorkflowExecution:LockProvider` must no longer switch the general DI binding.
- No database migration or Aether framework change is allowed.

---

### Task 1: Add the PostgreSQL Contract and Separate DI Bindings

**Files:**
- Create: `src/BBT.Workflow.Infrastructure/Execution/Locks/IPostgreSqlDistributedLockService.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Execution/Locks/NpgsqlDistributedLockService.cs`
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs`
- Create: `test/BBT.Workflow.Infrastructure.Tests/Execution/Locks/DistributedLockRegistrationTests.cs`

**Interfaces:**
- Consumes: `BBT.Aether.DistributedLock.IDistributedLockService`, `AddDaprDistributedLock(string)`.
- Produces: `IPostgreSqlDistributedLockService : IDistributedLockService` and dual DI registration.

- [ ] **Step 1: Write the failing DI and contract tests**

Create `DistributedLockRegistrationTests.cs`:

```csharp
using BBT.Aether;
using BBT.Aether.DistributedLock;
using BBT.Aether.DistributedLock.Dapr;
using BBT.Workflow.Infrastructure.Execution.Locks;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Locks;

public sealed class DistributedLockRegistrationTests
{
    [Fact]
    public void Postgres_contract_inherits_the_aether_contract()
    {
        typeof(IDistributedLockService)
            .IsAssignableFrom(typeof(IPostgreSqlDistributedLockService))
            .ShouldBeTrue();
    }

    [Fact]
    public void AddDistributedLock_keeps_dapr_as_default_and_registers_postgres_separately()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DAPR_LOCK_STORE_NAME"] = "lock-store",
                ["WorkflowExecution:LockProvider"] = "Postgres",
                ["ConnectionStrings:Default"] =
                    "Host=localhost;Database=locks;Username=postgres;Password=postgres"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<DaprClient>());
        services.AddSingleton(Substitute.For<IApplicationInfoAccessor>());

        services.AddDistributedLock(configuration);

        using var provider = services.BuildServiceProvider();
        var defaultLock = provider.GetRequiredService<IDistributedLockService>();
        var postgresLock = provider.GetRequiredService<IPostgreSqlDistributedLockService>();

        defaultLock.ShouldBeOfType<DaprDistributedLockService>();
        postgresLock.ShouldBeOfType<NpgsqlDistributedLockService>();
        ReferenceEquals(defaultLock, postgresLock).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the tests and verify RED**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~DistributedLockRegistrationTests"
```

Expected: compilation fails because `IPostgreSqlDistributedLockService` does not exist.

- [ ] **Step 3: Add the derived PostgreSQL contract**

Create `IPostgreSqlDistributedLockService.cs`:

```csharp
using BBT.Aether.DistributedLock;

namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Explicit PostgreSQL-backed distributed-lock capability for consumers that must not use the
/// application's default Aether lock provider.
/// </summary>
public interface IPostgreSqlDistributedLockService : IDistributedLockService
{
}
```

- [ ] **Step 4: Implement the dedicated contract**

Change the declaration in `NpgsqlDistributedLockService.cs`:

```csharp
public sealed class NpgsqlDistributedLockService : IPostgreSqlDistributedLockService
```

Do not modify any method body or lifetime behavior.

- [ ] **Step 5: Replace the provider switch with dual registration**

Replace the conditional provider block in `AddDistributedLock` with:

```csharp
var lockStoreName = configuration["DAPR_LOCK_STORE_NAME"]!;

services.AddDaprDistributedLock(lockStoreName);
services.AddSingleton<
    BBT.Workflow.Infrastructure.Execution.Locks.IPostgreSqlDistributedLockService,
    BBT.Workflow.Infrastructure.Execution.Locks.NpgsqlDistributedLockService>();

services.AddResourceLock(lockStoreName);
return services;
```

Remove comments describing `WorkflowExecution:LockProvider` as a global provider switch.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run the Step 2 command. Expected: 2 passed, 0 failed. The test sets `WorkflowExecution:LockProvider=Postgres` and still resolves the default contract to Dapr.

- [ ] **Step 7: Run existing PostgreSQL lock tests**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~NpgsqlDistributedLockServiceTests"
```

Expected: all acquisition, extension, fencing, expiry, and owner-release tests pass.

- [ ] **Step 8: Commit Task 1**

```bash
git add src/BBT.Workflow.Infrastructure/Execution/Locks/IPostgreSqlDistributedLockService.cs
git add src/BBT.Workflow.Infrastructure/Execution/Locks/NpgsqlDistributedLockService.cs
git add src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs
git add test/BBT.Workflow.Infrastructure.Tests/Execution/Locks/DistributedLockRegistrationTests.cs
git commit -m "refactor(locks): separate postgres lock contract"
```

---

### Task 2: Make Chain Reaper Consume the PostgreSQL Contract

**Files:**
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/HostedServices/ChainReaperHostedService.cs`
- Modify: `test/BBT.Workflow.Infrastructure.Tests/Execution/Locks/DistributedLockRegistrationTests.cs`

**Interfaces:**
- Consumes: `IPostgreSqlDistributedLockService` from Task 1.
- Produces: Chain Reaper leader election isolated from the default Aether lock binding.

- [ ] **Step 1: Write the failing constructor-contract test**

Add to `DistributedLockRegistrationTests`:

```csharp
[Fact]
public void ChainReaper_requires_the_postgres_specific_contract()
{
    var constructor = typeof(BBT.Workflow.HostedServices.ChainReaperHostedService)
        .GetConstructors()
        .Single();
    var parameterTypes = constructor.GetParameters()
        .Select(parameter => parameter.ParameterType)
        .ToArray();

    parameterTypes.ShouldContain(typeof(IPostgreSqlDistributedLockService));
    parameterTypes.ShouldNotContain(typeof(IDistributedLockService));
}
```

- [ ] **Step 2: Run the constructor test and verify RED**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~ChainReaper_requires_the_postgres_specific_contract"
```

Expected: assertion fails because the constructor still contains `IDistributedLockService`.

- [ ] **Step 3: Switch Chain Reaper to the dedicated contract**

Add `using BBT.Workflow.Infrastructure.Execution.Locks;`, change its primary-constructor dependency to:

```csharp
IPostgreSqlDistributedLockService lockService,
```

Update the XML link to the new contract. Do not change the lock key, lease calculation, logging, cancellation, or handle disposal.

- [ ] **Step 4: Run focused tests and verify GREEN**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~DistributedLockRegistrationTests"
```

Expected: 3 passed, 0 failed.

- [ ] **Step 5: Verify Inbox and Outbox remain outside the diff**

```bash
git diff --name-only $(git merge-base HEAD claude/cherry-pick-commits-zmqhjw)..HEAD \
  | rg '^workers/BBT.Workflow.Workers.(Inbox|Outbox)/' && exit 1 || true
```

Expected: no worker path is printed.

- [ ] **Step 6: Commit Task 2**

```bash
git add orchestration/BBT.Workflow.Orchestration.HttpApi.Host/HostedServices/ChainReaperHostedService.cs
git add test/BBT.Workflow.Infrastructure.Tests/Execution/Locks/DistributedLockRegistrationTests.cs
git commit -m "refactor(locks): isolate chain reaper postgres lease"
```

---

### Task 3: Cross-Project Verification and Local Integration

**Files:** No production changes expected.

**Interfaces:**
- Consumes: completed Tasks 1 and 2.
- Produces: verified feature branch ready for local integration.

- [ ] **Step 1: Run the complete focused lock suite**

```bash
dotnet test test/BBT.Workflow.Infrastructure.Tests/BBT.Workflow.Infrastructure.Tests.csproj \
  --no-restore -m:1 \
  --filter "FullyQualifiedName~DistributedLockRegistrationTests|FullyQualifiedName~NpgsqlDistributedLockServiceTests"
```

Expected: zero failures.

- [ ] **Step 2: Build affected host and full solution**

```bash
dotnet build orchestration/BBT.Workflow.Orchestration.HttpApi.Host/BBT.Workflow.Orchestration.HttpApi.Host.csproj \
  --no-restore -m:1
dotnet build BBT.Workflow.slnx --no-restore -m:1
```

Expected: both builds succeed with zero errors; record existing warnings separately.

- [ ] **Step 3: Verify repository hygiene**

```bash
git diff --check
git status --short
git log -5 --oneline
```

Expected: clean feature worktree and only the spec plus Task 1/Task 2 commits after the target branch base.

- [ ] **Step 4: Integrate locally after review approval**

After task reviews and final verification pass, fast-forward or merge the feature branch into local `claude/cherry-pick-commits-zmqhjw`, rerun the focused test and solution build on the target checkout, and keep that branch checked out. Do not push.

---

## Plan Self-Review

- Every approved requirement maps to a task and test.
- Contract and signatures are consistent across tasks.
- Inbox/Outbox exclusion is verified explicitly.
- No migration, Aether framework edit, keyed DI, or unrelated refactor is included.
- Every production change follows a failing test and each implementation task ends with a commit.
