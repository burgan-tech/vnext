# Component Cache L1 Layer Implementation Plan

> **Status (2026-08-20):** Tasks 0–6 executed inline and committed on `feature/component-cache-l1`.
> Caching tests 119/119 green; full Application.Tests matches the master baseline exactly (same 20
> pre-existing failures, zero regression). Two deviations from the written steps, both fixes found
> by the tests: L1 population must happen AFTER `HydrateReference` (L2 round-trips leave
> private-setter reference properties null and `View.SemanticVersion` throws on serialization), and
> `ComponentL1Cache` takes `TimeProvider` to convert absolute expirations to relative TTLs
> (`MemoryCache` runs on its own clock). Task 7.2 ran on 2026-08-20 against the local L1
> runtime (4201): pipeline-critical suites (ChainBusy, ContractSigning, SubflowOrchestration) fully
> green; all 31 failures in the full 107-test run attributed to non-L1 causes (RoleMatrixLab expects
> the unmerged caller-role-provider branch; MoneyTransfer broken by #881's documented breaking
> change; FuturePay by a MockLab template error; AccountOpening by a fixture script reading a
> `user-agent` header the SDK does not send; DataIntegrityLab stuck-Busy matches the known pending
> concurrent-write work). Task 7.3 (Helm envs) remains open.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a generation-keyed, bytes-mode in-process (L1) cache in front of the distributed component cache so full-version reads cost zero Dapr calls and range resolutions cost only the small generation read — with publish visibility unchanged.

**Architecture:** A singleton `ComponentL1Cache` (private size-limited `MemoryCache` storing serialized `CacheEnvelope<T>` bytes) is consulted by `CacheSet<T>` before every L2 (Dapr/Redis) envelope read and written through on every L2 envelope write. L1 keys are the existing Redis keys; resolution keys already embed the generation token, so publish invalidation works unchanged.

**Tech Stack:** .NET 10, Microsoft.Extensions.Caching.Memory, System.Text.Json (`JsonSerializerConstants.JsonOptions`, ns `BBT.Workflow`), xUnit + Shouldly.

**Spec:** `docs/superpowers/specs/2026-08-20-component-cache-l1-design.md`

---

### Task 0: Branch

- [ ] **Step 0.1:** `git checkout -b feature/component-cache-l1` from `master`; commit the spec, plan and analysis docs (`docs: add component cache L1 design spec and plan`).

### Task 1: Options + package reference

**Files:**
- Modify: `src/BBT.Workflow.Application/BBT.Workflow.Application.csproj` (ItemGroup with PackageReferences)
- Modify: `src/BBT.Workflow.Application/Caching/ComponentCacheOptions.cs`

- [ ] **Step 1.1:** Add to csproj ItemGroup:
```xml
<PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="$(MicrosoftPackageVersion)" />
```

- [ ] **Step 1.2:** Add to `ComponentCacheOptions` (after `PurgeLegacyKeysOnPublish`):
```csharp
    /// <summary>
    /// Gets or sets whether the in-process (L1) envelope cache in front of the distributed cache is
    /// enabled. Default is true.
    /// </summary>
    /// <remarks>
    /// Correctness is carried by the key scheme, not by this flag: full-version bodies are immutable,
    /// and resolution entries embed the generation token in their key, so a publish bump makes stale
    /// L1 entries unreachable exactly as it does for L2. Disabling this restores the previous
    /// behavior of one distributed-cache read per envelope access.
    /// </remarks>
    public bool L1Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the memory budget in megabytes for the L1 envelope cache, shared across all
    /// component types in the process. Default is 64.
    /// </summary>
    /// <remarks>
    /// Entries are stored as serialized bytes and sized by their byte length, so this bounds actual
    /// payload memory. When the budget is exceeded, least-recently-used entries are compacted away —
    /// an eviction is a re-fetch from the distributed cache, never an error.
    /// </remarks>
    [Range(8, 2048)]
    public int L1SizeLimitMb { get; set; } = 64;
```

- [ ] **Step 1.3:** `dotnet build src/BBT.Workflow.Application` — expect success. Commit: `feat(cache): add L1 options and memory cache package`.

### Task 2: `IComponentL1Cache` + `ComponentL1Cache` (TDD)

**Files:**
- Create: `src/BBT.Workflow.Application/Caching/IComponentL1Cache.cs`
- Create: `src/BBT.Workflow.Application/Caching/ComponentL1Cache.cs`
- Create: `test/BBT.Workflow.Application.Tests/Caching/ComponentL1CacheTests.cs`

- [ ] **Step 2.1: Failing tests.** New test file (namespace `BBT.Workflow.Caching`, follow existing test style with Shouldly; use `CacheSetTestHarness.CreateView` for a real entity):
```csharp
using System;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

public class ComponentL1CacheTests
{
    private static ComponentL1Cache Create(Action<ComponentCacheOptions>? configure = null)
    {
        var options = new ComponentCacheOptions();
        configure?.Invoke(options);
        return new ComponentL1Cache(Microsoft.Extensions.Options.Options.Create(options));
    }

    private static CacheEnvelope<View> Envelope(string version = "1.0.0-pkg.1.0.0")
    {
        var view = CacheSetTestHarness.CreateView(version);
        return new CacheEnvelope<View>
        {
            Domain = view.Domain, Key = view.Key, Version = view.Version,
            Flow = view.ComponentKey, Entity = view
        };
    }

    private static DateTimeOffset FutureExpiry => DateTimeOffset.UtcNow.AddMinutes(30);

    [Fact]
    public void Set_then_TryGet_round_trips_the_envelope()
    {
        using var sut = Create();
        sut.Set("k1", Envelope(), FutureExpiry);

        var got = sut.TryGet<View>("k1");

        got.ShouldNotBeNull();
        got.Version.ShouldBe("1.0.0-pkg.1.0.0");
        got.Entity.ShouldNotBeNull();
    }

    [Fact]
    public void TryGet_returns_a_fresh_instance_per_read()
    {
        using var sut = Create();
        sut.Set("k1", Envelope(), FutureExpiry);

        var first = sut.TryGet<View>("k1");
        var second = sut.TryGet<View>("k1");

        ReferenceEquals(first!.Entity, second!.Entity).ShouldBeFalse();
    }

    [Fact]
    public void Negative_envelopes_are_never_stored()
    {
        using var sut = Create();
        sut.Set("k1", new CacheEnvelope<View> { Domain = "core", Key = "k", IsNegative = true }, FutureExpiry);

        sut.TryGet<View>("k1").ShouldBeNull();
    }

    [Fact]
    public void Remove_evicts_the_entry()
    {
        using var sut = Create();
        sut.Set("k1", Envelope(), FutureExpiry);

        sut.Remove("k1");

        sut.TryGet<View>("k1").ShouldBeNull();
    }

    [Fact]
    public void Disabled_cache_stores_nothing_and_never_throws()
    {
        using var sut = Create(o => o.L1Enabled = false);
        sut.Set("k1", Envelope(), FutureExpiry);

        sut.TryGet<View>("k1").ShouldBeNull();
        sut.Remove("k1");
    }

    [Fact]
    public void Miss_returns_null()
    {
        using var sut = Create();
        sut.TryGet<View>("absent").ShouldBeNull();
    }
}
```

- [ ] **Step 2.2:** `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ComponentL1CacheTests"` — expect FAIL (types missing).

- [ ] **Step 2.3: Interface.** `IComponentL1Cache.cs`:
```csharp
namespace BBT.Workflow.Caching;

/// <summary>
/// In-process (L1) cache for component cache envelopes, sitting in front of the distributed (L2)
/// cache. Keys are the L2 cache keys, so version-resolution entries inherit their generation scoping
/// — a publish bump changes the key and stale entries simply stop being reachable.
/// </summary>
/// <remarks>
/// Stores envelopes as serialized bytes and deserializes per read, so every hit returns a fresh
/// instance — the same isolation callers get from an L2 read today. Negative envelopes are never
/// stored. All members are optimizations: they must never throw into the read path.
/// </remarks>
public interface IComponentL1Cache : IDisposable
{
    /// <summary>Returns the cached envelope for the key, or null on miss/disabled.</summary>
    CacheEnvelope<T>? TryGet<T>(string cacheKey) where T : class;

    /// <summary>Stores the envelope until the given expiry. Negative envelopes are ignored.</summary>
    void Set<T>(string cacheKey, CacheEnvelope<T> envelope, DateTimeOffset absoluteExpiration) where T : class;

    /// <summary>Removes the entry if present.</summary>
    void Remove(string cacheKey);
}
```

- [ ] **Step 2.4: Implementation.** `ComponentL1Cache.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Caching;

/// <summary>
/// Bytes-mode <see cref="IComponentL1Cache"/> backed by a private size-limited
/// <see cref="MemoryCache"/> shared across all component types in the process.
/// </summary>
/// <remarks>
/// Entries are the envelope serialized with <see cref="JsonSerializerConstants.JsonOptions"/> and
/// sized by byte length, so <see cref="ComponentCacheOptions.L1SizeLimitMb"/> bounds real payload
/// memory. A private cache instance is used, not the DI <c>IMemoryCache</c>, to keep the
/// "distributed cache for business data" rule intact everywhere else.
/// </remarks>
public sealed class ComponentL1Cache : IComponentL1Cache
{
    private readonly MemoryCache? _cache;

    public ComponentL1Cache(IOptions<ComponentCacheOptions> options)
    {
        if (options.Value.L1Enabled)
        {
            _cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = (long)options.Value.L1SizeLimitMb * 1024 * 1024
            });
        }
    }

    /// <inheritdoc />
    public CacheEnvelope<T>? TryGet<T>(string cacheKey) where T : class
    {
        if (_cache is null || !_cache.TryGetValue(cacheKey, out byte[]? bytes) || bytes is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<CacheEnvelope<T>>(bytes, JsonSerializerConstants.JsonOptions);
        }
        catch (JsonException)
        {
            // A poisoned entry must not poison the read path; drop it and fall through to L2.
            _cache.Remove(cacheKey);
            return null;
        }
    }

    /// <inheritdoc />
    public void Set<T>(string cacheKey, CacheEnvelope<T> envelope, DateTimeOffset absoluteExpiration) where T : class
    {
        if (_cache is null || envelope.IsNegative || envelope.Entity is null)
            return;

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSerializerConstants.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return;
        }

        _cache.Set(cacheKey, bytes, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = absoluteExpiration,
            Size = bytes.LongLength
        });
    }

    /// <inheritdoc />
    public void Remove(string cacheKey) => _cache?.Remove(cacheKey);

    public void Dispose() => _cache?.Dispose();
}
```

- [ ] **Step 2.5:** Re-run the Step 2.2 filter — expect PASS. Commit: `feat(cache): add bytes-mode component L1 cache`.

### Task 3: `cache.l1.hit` span tag

**Files:**
- Modify: `src/BBT.Workflow.Application/Caching/CacheActivityHelper.cs`

- [ ] **Step 3.1:** Add constant next to the other tags: `private const string TagCacheL1Hit = "cache.l1.hit";` and helper after `SetCacheHit`:
```csharp
    /// <summary>
    /// Records whether the in-process (L1) envelope cache answered the read, so load tests can
    /// attribute latency between L1 and the distributed store.
    /// </summary>
    public static void SetL1Hit(Activity? activity, bool hit)
    {
        activity?.SetTag(TagCacheL1Hit, hit);
    }
```

- [ ] **Step 3.2:** Build; commit with Task 4 (tag is used there).

### Task 4: `CacheSet<T>` integration (TDD)

**Files:**
- Modify: `src/BBT.Workflow.Application/Caching/CacheSet.cs`
- Modify: `test/BBT.Workflow.Application.Tests/Caching/CacheSetTestHarness.cs`
- Create: `test/BBT.Workflow.Application.Tests/Caching/CacheSetL1Tests.cs`

- [ ] **Step 4.1: Harness.** Give the harness an options hook and an L1, and pass the L1 to the SUT. In `CacheSetTestHarness`:
  - Change ctor signature to `public CacheSetTestHarness(Action<ComponentCacheOptions>? configure = null)` and invoke `configure?.Invoke(Options);` right after `Options = new ComponentCacheOptions();`.
  - Add property `public ComponentL1Cache L1 { get; }`, construct after `optionsAccessor`: `L1 = new ComponentL1Cache(optionsAccessor);`
  - Pass `L1` as the new last argument of `new CacheSet<View>(...)` (see Step 4.4 signature).

- [ ] **Step 4.2: Failing tests.** `CacheSetL1Tests.cs`:
```csharp
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

public class CacheSetL1Tests
{
    private const string Full = "1.0.0-pkg.1.0.0";

    [Fact]
    public async Task Second_full_version_read_is_served_from_L1_without_an_L2_read()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        var first = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        var second = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        harness.Cache.Reads.Count(k => k == harness.FullKey(Full)).ShouldBe(1);
        harness.Backend.LoadCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Second_latest_read_costs_only_the_generation_read()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");
        var generation = await harness.CurrentGenerationAsync();
        harness.Cache.ClearLog();

        var second = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");

        second.IsSuccess.ShouldBeTrue();
        harness.Cache.Reads.ShouldBe([harness.GenerationKey()]);
        _ = generation; // resolution key must not appear in Reads above
    }

    [Fact]
    public async Task A_publish_is_visible_immediately_despite_a_warm_L1()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.0");
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");

        const string newer = "1.1.0-pkg.1.1.0";
        harness.AddPublished(newer);
        await harness.Sut.SetAsync(CacheSetTestHarness.CreateView(newer));

        var resolved = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value!.Version.ShouldBe(newer);
    }

    [Fact]
    public async Task Invalidate_removes_the_full_body_from_L1_too()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        harness.Deactivate(Full);
        await harness.Sut.InvalidateAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        var after = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        after.IsSuccess.ShouldBeFalse();
        harness.Backend.LoadCallCount.ShouldBe(2); // second read went back to the backend
    }

    [Fact]
    public async Task L1_hits_return_distinct_instances()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        var first = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        var second = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        ReferenceEquals(first.Value, second.Value).ShouldBeFalse();
    }

    [Fact]
    public async Task Negative_answers_are_served_from_L2_not_L1()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        var miss1 = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "9");
        var generation = await harness.CurrentGenerationAsync();
        var miss2 = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "9");

        miss1.IsSuccess.ShouldBeFalse();
        miss2.IsSuccess.ShouldBeFalse();
        harness.Cache.Reads.Count(k => k == harness.ResolutionKey(generation, "9")).ShouldBe(2);
    }

    [Fact]
    public async Task Disabled_L1_reproduces_the_previous_read_counts()
    {
        var harness = new CacheSetTestHarness(o => o.L1Enabled = false);
        harness.Publish(Full);

        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        harness.Cache.Reads.Count(k => k == harness.FullKey(Full)).ShouldBe(2);
    }
}
```
Note for the second test: `GetByVersionAsync(..., "latest")` first performs the generation read, then
the L1 hit; `Reads` after `ClearLog()` must contain exactly the generation key and nothing else.

- [ ] **Step 4.3:** Run `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~CacheSetL1Tests"` — expect FAIL (ctor signature / no L1 behavior).

- [ ] **Step 4.4: Implement in `CacheSet.cs`.**
  1. Add ctor parameter (last position): `IComponentL1Cache l1Cache`.
  2. In `GetResolvedAsync`, right after the `CacheActivityHelper.SetGeneration(activity, generation);` line and before the L2 read:
```csharp
        var l1Envelope = l1Cache.TryGet<T>(redisKey);
        if (l1Envelope?.Entity is not null)
        {
            CacheActivityHelper.SetCacheHit(activity, true);
            CacheActivityHelper.SetL1Hit(activity, true);
            HydrateReference(l1Envelope);
            return Result<T>.Ok(l1Envelope.Entity);
        }
```
  3. Still in `GetResolvedAsync`, inside the existing `if (envelope.Entity is not null)` L2-hit branch, before `HydrateReference(envelope);`:
```csharp
                CacheActivityHelper.SetL1Hit(activity, false);
                PopulateL1(redisKey, envelope, ResolutionEntryOptions());
```
  4. In `GetFullVersionAsync`, right after the activity is started and before `TryGetEnvelopeAsync`:
```csharp
        var l1Envelope = l1Cache.TryGet<T>(redisKey);
        if (l1Envelope?.Entity is not null)
        {
            CacheActivityHelper.SetCacheHit(activity, true);
            CacheActivityHelper.SetL1Hit(activity, true);
            HydrateReference(l1Envelope);
            return Result<T>.Ok(l1Envelope.Entity);
        }
```
  and in its L2-hit branch (`if (envelope?.Entity is not null)`), before `HydrateReference(envelope);`:
```csharp
            CacheActivityHelper.SetL1Hit(activity, false);
            PopulateL1(redisKey, envelope, FullEntryOptions());
```
  5. In `TryWriteAsync`, first line of the method body (write-through; `ComponentL1Cache.Set` already
     ignores negatives, and a failed L2 write leaving a correct entity in L1 is harmless — the data
     came from the backend):
```csharp
        PopulateL1(redisKey, envelope, entryOptions);
```
  6. In `InvalidateAsync`, immediately after the `TryRemoveAsync(CreateFullKey(...))` call inside the
     `IsFullVersion` branch:
```csharp
            l1Cache.Remove(CreateFullKey(domain, key, version));
```
  7. Add the private helper next to `TryWriteAsync`:
```csharp
    private void PopulateL1(string redisKey, CacheEnvelope<T> envelope, DistributedCacheEntryOptions entryOptions)
    {
        if (entryOptions.AbsoluteExpiration is { } expiry)
            l1Cache.Set(redisKey, envelope, expiry);
    }
```

- [ ] **Step 4.5:** Run the Step 4.3 filter — expect PASS. Then run the whole caching suite:
`dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~Caching"` — expect PASS
(pre-existing `CacheSetTests` must be green: L1 write-through means some of their read counts stay
identical because they assert on `FakeDistributedCacheService.Reads`/`Writes`, which L1 does not
change for first reads; if any assert on *second*-read L2 traffic, inspect and update the test's
intent comment — the new expectation is one fewer L2 read, and that is the feature).

- [ ] **Step 4.6:** Commit: `feat(cache): serve component envelopes from generation-keyed L1 in CacheSet`.

### Task 5: Wiring (`DomainCacheContext`, DI)

**Files:**
- Modify: `src/BBT.Workflow.Application/Caching/DomainCacheContext.cs`
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/WorkflowApplicationModuleServiceCollectionExtensions.cs` (inside `AddCacheServices`)

- [ ] **Step 5.1:** `DomainCacheContext` ctor: add parameter `IComponentL1Cache l1Cache` (after `TimeProvider timeProvider`), pass it as the last argument to all seven `new CacheSet<...>(...)` calls.

- [ ] **Step 5.2:** In `AddCacheServices`, next to the generation-provider registration:
```csharp
        // In-process envelope cache in front of the distributed store. Resolution entries are keyed
        // by generation, so publish invalidation applies to L1 exactly as it does to L2.
        services.AddSingleton<IComponentL1Cache, ComponentL1Cache>();
```

- [ ] **Step 5.3:** Build the solution: `dotnet build`. Fix any remaining `CacheSet`/`DomainCacheContext` construction sites the compiler reports (tests included). Expected other site: `DomainCacheContextTests` if it constructs the context directly.

- [ ] **Step 5.4:** Run the full test project: `dotnet test test/BBT.Workflow.Application.Tests`. Expect: no new failures relative to the master baseline (beware: repo has known pre-existing failures on master — compare counts, see memory note "vNext test baseline").

- [ ] **Step 5.5:** Commit: `feat(cache): wire component L1 cache into cache module DI`.

### Task 6: Docs + meta

**Files:**
- Modify: `ai-docs/component-cache-l1-analysis.md` (mark Faz 1 as implemented, link PR/branch)
- Check: `docs/` for an existing component-cache page (`grep -ril "component cache" docs/` — if a runtime page exists, add an L1 section describing the two-layer design and the `ComponentCache:L1Enabled`/`L1SizeLimitMb` settings; if none exists, add the section to `docs/README.md`-linked location `docs/runtime/component-cache.md` as a new page with Navigation entry)

- [ ] **Step 6.1:** Write the doc updates (English, per repo convention).
- [ ] **Step 6.2:** Commit: `docs: document component cache L1 layer and settings`.

### Task 7: Verification (checkpoint)

- [ ] **Step 7.1:** `dotnet build` + full `dotnet test` run; record pass/fail counts vs master baseline.
- [ ] **Step 7.2 (requires user-visible infra; propose before running):** Integration regression per CLAUDE.local policy — infra up (`etc/docker/run-docker.sh`, check first), 4 apps with `--launch-profile http`, then run vnext-example `Core.IntegrationTests` with `VNEXT_BASE_URL=http://localhost:4201`. Success = same green set as the recorded 33/33 baseline.
- [ ] **Step 7.3:** Remind about Helm values for `ComponentCache__L1Enabled` / `ComponentCache__L1SizeLimitMb` (defaults are safe; surfacing them is optional but recommended for load-test toggling).

---

## Self-review notes

- Spec req 1–2 → Task 4 (L1 read-first + write-through). Req 3 → key scheme untouched; test "publish visible immediately". Req 4 → `ComponentL1Cache.Set` negative guard + CacheSet test. Req 5 → bytes mode + distinct-instances tests. Req 6 → `SizeLimit`, entry `Size = bytes.LongLength`. Req 7 → disabled-mode tests. Req 8 → Task 3 tag, set on both hit and L2-fallback paths.
- Type consistency: `IComponentL1Cache.TryGet<T>/Set<T>/Remove` used identically in Tasks 2, 4, 5; `PopulateL1` defined and used only in Task 4.
- `GetAllExtensionsAsync`/`GetLatestByNameAsync` route through `GetResolvedAsync` — covered without extra work.
