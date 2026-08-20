# Component Cache — Generation Memoization (Phase 2)

Status: **designed, not yet enabled** — the mechanism ships in the runtime
(`ComponentCacheOptions.GenerationMemoSeconds`, default `0` = off) and is activated per
environment by configuration. This document defines what turning it on means, what it costs,
and what the CI/CD process must change to accept that cost.

## What it is

With the L1 layer (Phase 1, see [Domain Cache Context](../domain/domain-cache-context.md)),
every version resolution (`latest`, `1`, `1.0`, `1.0.0`) still performs exactly one small
distributed-cache read: the component's **generation token**. That token is the freshness
carrier — a publish bumps it, which instantly invalidates every cached resolution on every pod.

`GenerationMemoSeconds = N` memoizes that token **in process for N seconds** (allowed range
0–60). Within the window, a hot resolution performs **zero** Dapr/Redis calls.

## What it saves

The remaining per-resolution round trip, including its idle-wake tail. Measured locally
(2026-08-20): a single `account-opening` start performs 22 generation reads across its hops
(flow ×6, mappings ×10, schemas ×3, tasks ×3); typical read ≈ 1–2 ms, but the first read of
each hop pays an idle-wake tax measured at 24–34 ms (reproduced independently of the app:
back-to-back reads p50 1.9 ms, after 10 s idle up to 33.8 ms). Under load this is the tail
that inflated Dapr state time (preprod p95 202 ms class). With the memo warm, all of it
disappears from the hot path.

## What it costs — the staleness window

The generation token is the **only** freshness signal. Memoizing it for N seconds means:

| Surface | Behavior with memo = N |
|---|---|
| Publishing pod | **Immediately fresh** — `BumpAsync` drops its own memo before writing the new token |
| Every other pod | May serve resolutions of the **old** generation for up to N seconds after the bump |
| Pinned full versions (`1.0.0-pkg.*`) | Unaffected — immutable bodies, no generation involved |
| Running instances | Unaffected — pinned to their `FlowVersion` |
| `latest` / range requests (`1`, `1.0`, artifact) | The affected surface: new starts and range-referenced sub-components |
| Newly published reference that was 404 just before | The negative answer can persist up to N seconds on pods that had requested it |
| **Deactivation / rollback** | Same window: a withdrawn version may keep being served for up to N seconds on other pods |

Worst case is exactly N (a pod refreshed its memo an instant before the bump). There is no
partial state within one pod-and-component: a resolution is either old-generation or
new-generation, never a mix of both for the same component read.

## CI/CD contract change

### Today (memo off)

```
init service publishes each component → 200 OK
        ⇒ generation bumped in Redis
        ⇒ ALL pods correct on their very next read
"CD finished" == "release live, cluster-wide"
smoke tests may run immediately
```

### With memo = N

```
init service publishes each component → 200 OK      (unchanged)
        ⇒ generation bumped in Redis                 (unchanged)
        ⇒ publishing pod fresh immediately
        ⇒ other pods fresh within ≤ N seconds
"CD finished" == "release live after a propagation window of N seconds"
```

Required pipeline adjustments:

1. **Propagation window** — after the *last* publish call, wait `N + margin` seconds (margin
   1–2 s) before running smoke tests, cutting traffic over, or declaring the release live.
   Alternative: run the first smoke assertion with a retry tolerance of at least N seconds.
   Note the window is anchored to the last publish: with multiple runtime pods behind a load
   balancer, each component may be published through a different pod, so "the publishing pod
   is fresh" is a per-component statement, not a per-release one.
2. **Rollback runbook** — a rollback or deactivation is a publish like any other: after
   withdrawing a bad version, other pods may serve it for up to N more seconds. Incident
   procedures that verify "the bad version is gone" must apply the same window.
3. **Nothing else changes** — publish API, init service, ordering (leaf-first), and the
   idempotency of re-publish are all unaffected.

### Choosing N

| N | Hot-path effect | Window |
|---|---|---|
| 0 (today) | 1 small read per resolution, idle-wake tail included | none |
| **5 s (recommended)** | ~0 reads for any component resolved in the last 5 s | ≤ 5 s |
| 30–60 s | marginal further gain (hot components stay hot at 5 s already) | operationally noticeable |

Recommendation: `5`. Traffic keeps hot components' memos warm continuously, so the practical
gain saturates at small N, while the CD window grows linearly with it.

### Configuration

Environment-level, never a code default (the runtime default stays `0`; a unit test pins the
"correctness first" default deliberately):

```
ComponentCache__GenerationMemoSeconds: "5"
```

Surfaced in vnext-helm-charts values so product teams opt in per environment.

## Failure-mode interaction (unchanged from Phase 1)

- Generation read fails → unshared fresh token → that call resolves from the backend
  (correct, slower). The memo is not written on failure.
- Bump write fails → memo dropped first, then remove-fallback; the pod never keeps serving
  a memoized token after a bump attempt, landed or not.
- The `GenerationTtlSeconds` (default 3600) backstop for a lost bump is unaffected.

## Verification

- Unit: memo hit skips the distributed read; expiry re-reads; `BumpAsync` drops and rewrites
  the memo (publishing-pod immediacy); default 0 keeps today's behavior.
- Single-pod behavior lab: `vnext-example/api-tests/l1-cache-lab` re-run with the memo enabled
  must stay green — on one pod the publish path drops the memo, so freshness assertions hold.
  The cross-pod window **cannot** be observed on a single-pod local stack; it is measured in
  a multi-replica environment (publish through one pod, poll `latest` through another, record
  the lag; expected ≤ N).
