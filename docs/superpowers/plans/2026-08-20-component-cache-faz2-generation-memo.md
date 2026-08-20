# Component Cache Phase 2 (GenerationMemoSeconds) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Activate in-process memoization of component generation tokens so hot-path version
resolutions cost zero Dapr calls, with the ≤N-second cross-pod publish window formally accepted
and documented in the CI/CD process.

**Architecture:** No new mechanism — `ComponentGenerationProvider` already implements the memo
(`TryReadMemo`/`WriteMemo`/drop-on-bump) behind `ComponentCacheOptions.GenerationMemoSeconds`
(default 0, range 0–60). The work is: pin the behavior with unit tests, keep the code default
at 0 (a test deliberately pins "correctness first"), activate per environment via configuration
(Helm / compose env), extend the l1-cache-lab proof, and land the CI/CD contract change in docs.

**Tech Stack:** .NET 10, xUnit + Shouldly, existing `CacheSetTestHarness` fakes.

**Contract doc:** `docs/runtime/component-cache-generation-memo.md` (written alongside this plan).

---

### Task 1: Unit tests pinning memo behavior

**Files:**
- Create: `test/BBT.Workflow.Application.Tests/Caching/ComponentGenerationProviderMemoTests.cs`
- Reuse: `CacheSetTestHarness.AdjustableTimeProvider`, `FakeDistributedCacheService` (both public)

- [ ] **Step 1.1: Write the failing tests** (construct the provider directly, memo enabled via
  `ComponentCacheOptions { GenerationMemoSeconds = 5 }`, options wrapped with
  `Microsoft.Extensions.Options.Options.Create`):

```csharp
using System;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

public class ComponentGenerationProviderMemoTests
{
    private sealed class FixedGuids : IGuidGenerator
    {
        private int _n;
        public Guid Create() => new(System.Threading.Interlocked.Increment(ref _n), 0, 0, new byte[8]);
    }

    private static (ComponentGenerationProvider Sut,
                    FakeDistributedCacheService Cache,
                    CacheSetTestHarness.AdjustableTimeProvider Time)
        Create(int memoSeconds)
    {
        var time = new CacheSetTestHarness.AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var cache = new FakeDistributedCacheService(time);
        var sut = new ComponentGenerationProvider(
            cache,
            new FixedGuids(),
            Microsoft.Extensions.Options.Options.Create(
                new ComponentCacheOptions { GenerationMemoSeconds = memoSeconds }),
            time,
            NullLogger<ComponentGenerationProvider>.Instance);
        return (sut, cache, time);
    }

    private const string GenKey = "sys-tasks:core:k:gen";

    [Fact]
    public async Task Memo_hit_skips_the_distributed_read()
    {
        var (sut, cache, _) = Create(5);
        var first = await sut.GetAsync("sys-tasks", "core", "k");
        cache.ClearLog();

        var second = await sut.GetAsync("sys-tasks", "core", "k");

        second.ShouldBe(first);
        cache.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Memo_expires_after_the_window_and_rereads()
    {
        var (sut, cache, time) = Create(5);
        await sut.GetAsync("sys-tasks", "core", "k");
        cache.ClearLog();

        time.Advance(TimeSpan.FromSeconds(6));
        await sut.GetAsync("sys-tasks", "core", "k");

        cache.Reads.ShouldContain(GenKey);
    }

    [Fact]
    public async Task Bump_replaces_the_memo_so_the_publishing_pod_is_immediately_fresh()
    {
        var (sut, cache, _) = Create(5);
        var before = await sut.GetAsync("sys-tasks", "core", "k");

        var bumped = await sut.BumpAsync("sys-tasks", "core", "k");
        cache.ClearLog();
        var after = await sut.GetAsync("sys-tasks", "core", "k");

        after.ShouldBe(bumped);
        after.ShouldNotBe(before);
        cache.Reads.ShouldBeEmpty("bump memoizes the new token; no re-read needed");
    }

    [Fact]
    public async Task Failed_bump_write_leaves_no_memo_behind()
    {
        var (sut, cache, _) = Create(5);
        await sut.GetAsync("sys-tasks", "core", "k");

        cache.FailWrites = key => key == GenKey;
        cache.FailRemoves = key => key == GenKey;
        await sut.BumpAsync("sys-tasks", "core", "k");

        cache.FailWrites = null;
        cache.FailRemoves = null;
        cache.ClearLog();
        await sut.GetAsync("sys-tasks", "core", "k");

        cache.Reads.ShouldContain(GenKey, customMessage: "memo must be dropped even when the bump write fails");
    }

    [Fact]
    public async Task Disabled_memo_reads_the_distributed_cache_every_time()
    {
        var (sut, cache, _) = Create(0);
        await sut.GetAsync("sys-tasks", "core", "k");
        cache.ClearLog();

        await sut.GetAsync("sys-tasks", "core", "k");

        cache.Reads.ShouldContain(GenKey);
    }
}
```

- [ ] **Step 1.2:** Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ComponentGenerationProviderMemoTests"` — expect PASS immediately if the shipped implementation is correct (these tests PIN existing behavior; a failure is a real finding, not a missing feature). If `FakeDistributedCacheService`'s fail hooks or `AdjustableTimeProvider` visibility differ from the plan's assumptions, adapt the test to the harness, not the production code.

- [ ] **Step 1.3:** Confirm the default-pinning test still stands untouched: `CacheSetTests` line ~731 asserts `GenerationMemoSeconds == 0` by default ("memoization trades correctness for latency"). Do NOT change the code default.

- [ ] **Step 1.4:** Commit: `test(cache): pin generation memo semantics for Phase 2 activation`.

### Task 2: Environment activation surfaces

**Files:**
- Modify: `etc/docker` compose env for orchestration + execution (dev opt-in, commented or profile-gated)
- Check/Modify: `vnext-helm-charts` values (separate repo `/Users/U0B006/Documents/repos/burgan-tech/vnext-helm-charts`)

- [ ] **Step 2.1:** Add `ComponentCache__GenerationMemoSeconds` to the Helm chart values for
  orchestration and execution deployments with default `"0"` (off) and a documented
  recommended value `"5"`. Both hosts resolve components — set it on BOTH or the gain halves.
- [ ] **Step 2.2:** Add the same env (commented, with a pointer to
  `docs/runtime/component-cache-generation-memo.md`) to the local docker/dev launch surface so
  the lab re-run in Task 3 has a one-line switch.
- [ ] **Step 2.3:** Commit per repo (`chore(helm): surface ComponentCache generation memo knob`).

### Task 3: Single-pod behavior proof (l1-cache-lab re-run)

**Files:**
- Modify: `vnext-example/api-tests/l1-cache-lab/README.md` (add a "memo enabled" run section)

- [ ] **Step 3.1:** Restart the 4 local apps with `ComponentCache__GenerationMemoSeconds=5`
  (launch profiles + env; check apps are otherwise idle first).
- [ ] **Step 3.2:** Run `python3 api-tests/l1-cache-lab/l1-cache-behaviour-test.py --minor <next>` —
  expected **18/18 PASS**: on a single pod the publish path drops the memo, so freshness
  assertions hold even inside the memo window. This pins the `BumpAsync` drop-first contract
  end to end.
- [ ] **Step 3.3:** Redis MONITOR spot-check during 6 repeated view reads: expected ~0–2 gen
  `HGETALL` total (only at memo expiry boundaries) instead of 12.
- [ ] **Step 3.4:** Document in the lab README: the cross-pod ≤N window is NOT observable on a
  single-pod stack; it is measured in a multi-replica environment by publishing through one
  pod and polling `latest` through another (expected lag ≤ N).
- [ ] **Step 3.5:** Commit in vnext-example.

### Task 4: Documentation landing

**Files:**
- Modify: `docs/README.md` (navigation)
- Modify: `docs/domain/domain-cache-context.md` (short Phase 2 pointer in the L1 paragraph)
- Modify: `.claude/rules/vnext-workflow-developer.md` — no change needed unless activation
  becomes default; skip.

- [ ] **Step 4.1:** Add nav entry: "Read [Component Cache Generation Memo](runtime/component-cache-generation-memo.md)
  before enabling `GenerationMemoSeconds` or editing the CD propagation window."
- [ ] **Step 4.2:** In `domain-cache-context.md`, add one sentence to the L1 note: the remaining
  per-resolution generation read can be memoized (`GenerationMemoSeconds`, opt-in) at the cost
  of a ≤N-second cross-pod publish window — link the runtime doc.
- [ ] **Step 4.3:** Commit: `docs: document generation memo activation and CD propagation window`.

### Task 5: Verification + handoff (checkpoint)

- [ ] **Step 5.1:** Full `dotnet test test/BBT.Workflow.Application.Tests` — compare to the
  known baseline (same 20 pre-existing failures; zero new).
- [ ] **Step 5.2:** Hand the CI/CD contract section of
  `docs/runtime/component-cache-generation-memo.md` to the team's CI/CD documentation owner —
  the pipeline change is: **wait N+margin after the last publish before smoke/cutover, and add
  the same window to the rollback runbook**. The gap acceptance decision (N=5 recommended) is
  a product/release-engineering sign-off, not an engineering default.
- [ ] **Step 5.3:** Load-test rerun (preprod): compare Dapr state line item and the `cache.l1.hit`
  span tag distribution before/after enabling the memo.

---

## Self-review notes

- Scope deliberately excludes: pub/sub generation broadcast (Phase 3 — only if the ≤N window is
  rejected), pipeline-scoped memoization (independent, semantics-free alternative), and any
  change to the code default (pinned by test at 0).
- Type consistency: tests construct `ComponentGenerationProvider` with the exact ctor
  `(IDistributedCacheService, IGuidGenerator, IOptions<ComponentCacheOptions>, TimeProvider, ILogger<>)`
  as it exists today; `FakeDistributedCacheService.FailWrites/FailRemoves/Reads/ClearLog` exist
  in the harness file.
- The plan produces working verified behavior at every task boundary; Tasks 2–3 are
  environment/config work and carry the only cross-repo touches (helm charts, vnext-example).
