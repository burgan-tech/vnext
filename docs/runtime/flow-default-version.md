# Flow Default Version

Publishing a flow version makes it *reachable*; it does not make it the *default*. A flow's
**default-version pointer**, kept in the `sys-version-registry` system flow, decides which version a
floating request (no version, `latest`, `2`, `2.1`) starts with. Versions above the pointer stay
**pending** until a caller asks for them explicitly. Source of truth in code:
`DefaultVersionResolver` (pure resolution), `CacheSet<T>` (where it is applied),
`VersionRegistryPolicySource` (reads the pointer), `VersionRegistryWriteGuard` (validates writes),
`InvalidateVersionPolicyJob` (invalidation).

A flow with no registry instance behaves exactly as before: the policy is **opt-in per flow**.

## Concepts

| Term | Meaning |
|---|---|
| Published | Every version that has a row in `sys-flows`. |
| Pointer | The `defaultVersion` written in the registry: major (`2`), minor (`2.1`), artifact (`2.1.0`), full (`2.1.0-pkg.1.0.0`) or `"latest"` / `null` (Automatic). |
| Effective default (D) | The best published match of the pointer. For a partial pointer D moves up by itself when a newer patch of that line is published. |
| Pending | Published versions above D. |
| Explicit request | A full (`-pkg.`) or artifact (`2.2.0`) version. It never meets the ceiling, so pending versions are reachable. |

### Pin modes

| Pointer | Mode | New patch in the line | Higher minor or major |
|---|---|---|---|
| `"latest"` / `null` / no record | Automatic (today's behaviour) | live | live |
| `2.1.0-pkg.1.0.168` (full) | Package-build pin | pending | pending |
| `2.1.0` | Artifact pin: follows re-published package builds of `2.1.0` | pending | pending |
| `2.1` | Minor line | live | pending |
| `2` | Major line | live | live within `2`, `3.0.0` pending |

A full version pins one **package build**. Write it without `+metadata` (the registry schema rejects the
stored `+core` form); matching ignores build metadata, so `2.1.0-pkg.1.0.168` selects the published
`2.1.0-pkg.1.0.168+core`. An artifact pointer `2.1.0` pins the artifact only: a re-publish under a new
`-pkg.` build moves D. Partial package forms (`2.1.0-pkg.1.0`, `2.1.0-pkg.1`) are not supported and are
rejected as an invalid pointer (`Instance:100052`).

## Resolution

| Request | Result |
|---|---|
| Full (build metadata ignored) or artifact version | Exact match, ceiling not applied (pending included); served even while the pointer matches nothing |
| `null`, empty, `latest` | D |
| `M` or `M.m` | Best published match at or below D |
| Floating request whose matches are all above D | `Instance:100051` (404) |
| Pointer matches nothing published | `Instance:100052` (409) for `latest`/empty and floating requests, fail-closed, never replaced by latest |

Example, published `2.0.0, 2.1.0, 2.1.3, 2.2.0, 2.3.0, 3.0.0`:

| Pointer | D | Request | Result |
|---|---|---|---|
| none | | `latest` / `2` | `3.0.0` / `2.3.0` |
| `2.1.0` | `2.1.0` | `latest` / `2.1` | `2.1.0` / `2.1.0` |
| `2.1.0` | `2.1.0` | `2.2.0` | `2.2.0` (explicit) |
| `2.1.0` | `2.1.0` | `2.2` | `Instance:100051` |
| `2.1` | `2.1.3` | `2` | `2.1.3` |
| `2` | `2.3.0` | `3` | `Instance:100051` |
| `2.4` (no `2.4.x`) | none | `latest` / any floating | `Instance:100052` |
| `2.4` (no `2.4.x`) | none | `2.2.0` | `2.2.0` (explicit) |

Only the `Workflows` cache set applies the policy, only on a backend miss, and `sys-*` keys and full
versions bypass it (the registry cannot depend on itself). Policy errors are never negative-cached.
Explicit always wins: `DefaultVersionResolver.Resolve` handles a full or artifact request before it
judges the pointer, so a pointer that matches no published version blocks only `latest`/empty and
floating requests — an explicit artifact (`2.2.0`) or full `-pkg.` version keeps working and is the way
around a broken pointer until it is fixed. A pointer of the wrong form (blank, a non-string
value) is rejected by the registry read itself (409) before resolution. Running instances are unaffected: each is pinned to the resolved version it started with.
A SubFlow or SubProcess parent stores the child's **resolved** version
([SubFlow Execution](../architecture/subflow-execution.md)).

## Registry contract

The registry flow `sys-version-registry` ships with the `vnext-sys-flow` package
(`@burgan-tech/vnext-core-runtime`): one instance per flow, **instance key = flow key**.

```json
{ "type": "Flow", "defaultVersion": "2.1", "reason": "pilot finished, CHG-12345" }
```

| Field | Rule |
|---|---|
| `type` | Informational; the runtime never reads it. |
| `defaultVersion` | `M`, `M.m`, `M.m.p`, a full `-pkg.` version, or `"latest"` (lowercase in the schema) for Automatic. `"latest"` is the way to **clear** a pointer; `null` is valid but only takes effect at create, because the instance-data merge ignores a `null` on a key that already exists ([merge semantics](../domain/instance-data-merge-concept.md)). Blank is rejected. |
| `reason` | Optional audit text. |

Flow shape: `create` -> `draft` (Initial) -> automatic `register` -> `active`, then a single
`set-default` transition (`"latest"` clears; `null` does not on an existing instance). There is no final state.

The pointer is the latest committed data row (`IsLatest`, selected in SQL) of the newest instance with
the flow key, **whatever its status** (`IInstanceRepository.FindLatestDataByKeyAsync`): a registry
transition commits with the instance Busy and the invalidation job can run before it settles Active, and a
Faulted registry instance still carries its last pointer — a status-filtered read would fall back to
latest in both cases.

Domain without the registry flow (old core package) or no instance for the key => Automatic, no error.
Registry read failure => `Instance:100053` (503, transient, fixed message; the cause is in the 20502 log
only): resolution fails instead of falling back to latest. A stored `defaultVersion` that is not a string
is a broken pointer => `Instance:100052` (409), not an outage.

**Future types, key prefix rule (K10).** The instance key is the flow key. Function or other component
types, when supported, use a prefixed key (`function.{key}`); flows keep the bare key, so v1 data stays
valid.

## Writes and propagation

- `VersionRegistryWriteGuard` runs in `TransitionValidationService`, in the validation phase before
  admission and Busy, on start and transitions of the registry flow. An invalid or unmatched pointer
  answers `Instance:100052` (409) and the registry instance stays Active.
- `VersionRegistryInvalidationStep` (`LifecycleOrder.VersionRegistryInvalidation` = 109) enqueues a
  best-effort post-commit `InvalidateVersionPolicyJob` (continuation, `ContinueParent`) that bumps the
  flow's cache generation. A failed bump only logs (EventId 20504).
- With the per-pod generation memo ([Component Cache Generation Memo](component-cache-generation-memo.md))
  a pointer change is visible on every pod within ≈ `GenerationMemoSeconds` (default 5 s).

## Error codes

| Code | Name | HTTP | Meaning |
|---|---|---|---|
| `Instance:100051` | FlowVersionNotActivated | 404 | Requested version(s) exist but are above the default. |
| `Instance:100052` | FlowDefaultVersionInvalid | 409 | Pointer is malformed (non-string included) or matches no published version (`latest`/floating requests only). |
| `Instance:100053` | VersionRegistryUnavailable | 503 | Registry read failed; transient, retry. |

## Observability

- `Instance.Create` span: `vnext.flow.version.requested` (what the caller sent; absent when none) and
  `vnext.flow.version` (what it resolved to).
- Cache resolution span: `vnext.flow.version.pointer`, `vnext.flow.version.source`
  (`automatic|explicit|default|ceiling`).
- Logs, `WorkflowLogs.cs` Flow Version Policy region: 20500 resolved by policy, 20501 invalid default,
  20502 registry read failed, 20503 registry invalidated, 20504 invalidation failed.

## Runbook

**EC1, pilot in one domain (live `1.2.0`, pilot `1.2.1`).** `set-default 1.2.0` (exact pin, a line
pointer `1.2` would let `1.2.1` go live at once); publish `1.2.1` (pending); pilot clients start with
`?version=1.2.1`; when ready `set-default 1.2.1` (or `1.2` to let patches flow). Rollback is
`set-default 1.2.0`; instances started on `1.2.1` stay on it.

**EC2, cross-domain.** Resolution happens in the callee's domain with the callee's pointer. A caller
referencing `"1.2.0"` or `"1.2"` must change its reference to move; one referencing `1`, `latest` or
nothing follows the callee's pointer. Pilot: publish the callee's new version (pending), reference it
by full version from the pilot caller version, flip callee's default, then the caller's. The two
registries are not atomic: flip the callee first.

**Symptoms.** 404 `100051`: request is above the pointer; use an explicit version or move the pointer.
409 `100052`: fix the registry pointer (`set-default` to a published version or `"latest"`). 503 `100053`:
check the database, then retry.
