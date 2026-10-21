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
| Pointer | The `defaultVersion` written in the registry: major (`2`), minor (`2.1`), artifact (`2.1.0`), full (`2.1.0-pkg.1.0.0`) or `null` (Automatic). |
| Effective default (D) | The best published match of the pointer. For a partial pointer D moves up by itself when a newer patch of that line is published. |
| Pending | Published versions above D. |
| Explicit request | A full (`-pkg.`) or artifact (`2.2.0`) version. It never meets the ceiling, so pending versions are reachable. |

### Pin modes

| Pointer | Mode | New patch in the line | Higher minor or major |
|---|---|---|---|
| `null` / no record | Automatic (today's behaviour) | live | live |
| `2.1.0` | Exact pin | pending | pending |
| `2.1` | Minor line | live | pending |
| `2` | Major line | live | live within `2`, `3.0.0` pending |

## Resolution

| Request | Result |
|---|---|
| Full or artifact version | Exact match, ceiling not applied (pending included) |
| `null`, empty, `latest` | D |
| `M` or `M.m` | Best published match at or below D |
| Floating request whose matches are all above D | `Instance:100051` (404) |
| Pointer matches nothing published | `Instance:100052` (409), fail-closed, never replaced by latest |

Example, published `2.0.0, 2.1.0, 2.1.3, 2.2.0, 2.3.0, 3.0.0`:

| Pointer | D | Request | Result |
|---|---|---|---|
| none | | `latest` / `2` | `3.0.0` / `2.3.0` |
| `2.1.0` | `2.1.0` | `latest` / `2.1` | `2.1.0` / `2.1.0` |
| `2.1.0` | `2.1.0` | `2.2.0` | `2.2.0` (explicit) |
| `2.1.0` | `2.1.0` | `2.2` | `Instance:100051` |
| `2.1` | `2.1.3` | `2` | `2.1.3` |
| `2` | `2.3.0` | `3` | `Instance:100051` |
| `2.4` (no `2.4.x`) | none | any floating | `Instance:100052` |

Only the `Workflows` cache set applies the policy, only on a backend miss, and `sys-*` keys and full
versions bypass it (the registry cannot depend on itself). Policy errors are never negative-cached.
Because the pointer is validated by `DefaultVersionResolver.IsValidPointer` before resolution, a
broken pointer blocks explicit artifact requests too until it is fixed; full `-pkg.` versions keep
working. Running instances are unaffected: each is pinned to the resolved version it started with.
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
| `defaultVersion` | `M`, `M.m`, `M.m.p`, a full `-pkg.` version, or `null` to clear. `latest` and blank are rejected. |
| `reason` | Optional audit text. |

Flow shape: `create` -> `draft` (Initial) -> automatic `register` -> `active`, then a single
`set-default` transition (`null` clears). There is no final state.

Domain without the registry flow (old core package) => Automatic, no error. Registry read failure =>
`Instance:100053` (503, transient): resolution fails instead of falling back to latest.

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
  a pointer change is visible on every pod within about 5 seconds.

## Error codes

| Code | Name | HTTP | Meaning |
|---|---|---|---|
| `Instance:100051` | FlowVersionNotActivated | 404 | Requested version(s) exist but are above the default. |
| `Instance:100052` | FlowDefaultVersionInvalid | 409 | Pointer is malformed or matches no published version. |
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
409 `100052`: fix the registry pointer (`set-default` to a published version or `null`). 503 `100053`:
check the database, then retry.
