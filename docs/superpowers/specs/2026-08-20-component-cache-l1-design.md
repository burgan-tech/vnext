# Component Cache L1 Layer — Design Spec (Option A / bytes mode)

**Date:** 2026-08-20
**Full analysis:** [ai-docs/component-cache-l1-analysis.md](../../../ai-docs/component-cache-l1-analysis.md)
**Approved by:** user ("Seçenek A için devam edelim")

## Goal

Eliminate the dominant Dapr state (Redis) round-trips on the component-definition read path by adding
an in-process (L1) cache in front of the existing distributed (L2) cache — **without changing publish
visibility semantics in any way**.

## Requirements

1. A full-version body read (`full:{canonicalVersion}`) that has been seen once by a pod costs **zero**
   Dapr calls afterwards (until TTL/memory eviction). Full versions are immutable by design.
2. A range/latest resolution read costs **one** Dapr call (the small generation-token read) instead of
   two; the large body payload never crosses the wire on an L1 hit.
3. Publish freshness is **identical to today**: the generation token is still read from L2 on every
   resolution, and L1 resolution entries embed the generation in their key, so a `BumpAsync` makes
   them unreachable instantly on every pod. No new staleness window is introduced.
4. Negative ("no version matched") answers are **never** served from L1.
5. Mutation isolation: every L1 hit returns a **fresh object instance** (bytes mode — the envelope is
   stored serialized and deserialized per read), preserving today's behavior where each Redis read
   deserializes a new instance. This defers the definition-immutability audit that materialized-object
   caching (A2) would require.
6. Bounded memory: single shared size-limited store across all component types (default 64 MB,
   entry size = serialized byte length).
7. Feature can be disabled by configuration (`ComponentCache:L1Enabled=false`) restoring today's exact
   behavior.
8. Observability: cache spans carry a `cache.l1.hit` tag so the next load test can quantify the win.

## Non-goals

- `GenerationMemoSeconds` activation (Phase 2, config-only, separate product decision).
- Pub/sub generation broadcast (Phase 3).
- Pipeline-scoped memoization (side quest, low priority once L1 exists).
- Materialized-object L1 (A2) — requires immutability audit first.
- Dapr Jobs latency and instance SaveState costs (separate work items).

## Design

### New units

| Unit | Responsibility |
|---|---|
| `IComponentL1Cache` (`src/BBT.Workflow.Application/Caching/`) | `TryGet<T>(key)` / `Set<T>(key, envelope, absoluteExpiration)` / `Remove(key)`. Envelope-level, key = the existing Redis key (so resolution keys inherit the generation scope for free). |
| `ComponentL1Cache` | Singleton. Owns a private `MemoryCache` with `SizeLimit = L1SizeLimitMb`. Stores `JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSerializerConstants.JsonOptions)`; deserializes per read. Skips `IsNegative` envelopes. Serialization failures degrade to no-op (L1 is an optimization, never a failure source). Disabled mode = null store, all no-ops. |

Note: the coding-standards rule "always `IDistributedCache`, not `IMemoryCache`" is a default for
business data; L1 here is deliberately in-process and correctness is carried by the key scheme, not by
cross-pod visibility. `ComponentL1Cache` owns a private `MemoryCache` instance rather than injecting
`IMemoryCache` to keep that rule intact elsewhere.

### `CacheSet<T>` integration (only file with read-path changes)

- ctor gains `IComponentL1Cache l1Cache`.
- `GetResolvedAsync`: after computing `redisKey` (which already includes the L2-fetched generation),
  try L1 first → hit: tag span (`cache.hit=true`, `cache.l1.hit=true`), `HydrateReference`, return.
  On L2 hit (non-negative): populate L1 with `ResolutionEntryOptions().AbsoluteExpiration`.
- `GetFullVersionAsync`: same pattern with `FullEntryOptions()`.
- `TryWriteAsync`: write-through to L1 (skips negatives via `ComponentL1Cache.Set` guard) using the
  same `AbsoluteExpiration` as the L2 entry — publish warm-up and backend resolution fill L1 on the
  serving pod automatically.
- `InvalidateAsync`: also `l1Cache.Remove(fullKey)`.
- Generation bump paths need **no** L1 action: old-generation resolution keys become unreachable, and
  TTL/LRU garbage-collects them — same principle as L2.

### Wiring

- `ComponentCacheOptions`: `L1Enabled` (default `true`), `L1SizeLimitMb` (default `64`, range 8–2048).
- `DomainCacheContext`: ctor gains `IComponentL1Cache`, passes to each `CacheSet<T>`.
- `AddCacheServices`: `services.AddSingleton<IComponentL1Cache, ComponentL1Cache>();` (covers both
  full hosts and the read-only cache module path).
- `BBT.Workflow.Application.csproj`: add `Microsoft.Extensions.Caching.Memory`
  `$(MicrosoftPackageVersion)`.
- `CacheActivityHelper`: `cache.l1.hit` tag + `SetL1Hit` helper.

### Error handling

L1 never throws into the read path: deserialize failure removes the poisoned entry and falls through
to L2; serialize failure skips the L1 write. The existing L2 failure semantics (`TryGet/TryWrite`
catch-and-log) are untouched.

### Testing

- Unit (`test/BBT.Workflow.Application.Tests/Caching/`): new `CacheSetL1Tests` on the existing
  `CacheSetTestHarness` (extended with an L1 + options-configure hook), plus `ComponentL1CacheTests`.
  Key assertions: second full read issues no L2 read; second resolution read issues only the
  generation read; a publish (generation bump) is visible immediately despite L1; negatives bypass L1;
  L1 hits return distinct instances; disabled mode reproduces today's read counts.
- Integration (vnext-example, per CLAUDE.local policy — major core-path change): run the existing
  `Core.IntegrationTests` suites against the locally built runtime (`VNEXT_BASE_URL=http://localhost:4201`).
  No new scenario needed: L1 is behaviorally invisible; the suites act as regression proof, and the
  publish-then-consume flows in them exercise freshness.

### Rollout notes

- Helm: new envs `ComponentCache__L1Enabled` / `ComponentCache__L1SizeLimitMb` should be surfaced in
  vnext-helm-charts values (reminder to check — required config has defaults, so absence is safe).
- Rolling deploys: L1 is pod-local and the L2 key scheme is unchanged — no old/new build interaction.
