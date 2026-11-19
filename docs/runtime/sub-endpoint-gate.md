# Sub-Endpoint Gate and the Runtime-Caller Token

> Issue: [burgan-tech/vnext#1265](https://github.com/burgan-tech/vnext/issues/1265) · source thread
> burgan-tech/vnext-client-sdk-core#162 · council session `2026-11-17-sub-endpoint-role-gate`
> ([decision log](../agent-council/sessions/README.md)). **Chair approval pending**: the gate ships
> Off; switching an environment to `Enforce` has its own preconditions (below).

## What it protects

Two endpoints exist for runtime-to-runtime calls only. Both are hidden from Swagger, and network isolation was their only protection:

| Endpoint | Called by | What a client could do with it |
|---|---|---|
| `POST {domain}/workflows/{workflow}/sub/instances/start` | a parent runtime starting a child in another domain | Start a child directly, skipping whatever restricts starting it, with a body marked runtime-produced (`TrustedPayload = true`: file references are accepted as they are). |
| `POST {domain}/workflows/{workflow}/instances/{instance}/complete` | the completion relay and the Inbox backup | Complete an open parent/child correlation with child data and carried headers it chose. The parent resumes early and the data flows through its output mapping. Reproduced locally on `76ca97ab`: the parent moved on, tried to start the next child, and faulted, while the real child was still waiting. |

burgan-tech/vnext-client-sdk-core#162 reports that platform gateways route `/<domain>/*` wildcards to the runtime (its platform-lab route script and the bank's APISIX routes), so these routes may be reachable unless a gateway rule closes them. Whether a given environment exposes them is confirmed per environment (council condition C-4).

The gate is **optional** and set in **domain configuration** (`Workflow:SubEndpointGate`), not in a flow definition. The question it answers is "who may call this domain's runtime-internal endpoints"; it has no per-flow meaning. Answering it from a flow would need a definition load before refusing and a schema change, and a flow author could reopen what the operator closed.

## Why roles alone cannot work

The runtime's own calls carry the user's identity when there is one, and nothing when there is not:

| # | Runtime caller | Host | Identity it carries |
|---|---|---|---|
| C1 | Cross-domain SubFlow start (`RemoteInstanceCommandAppService.StartSubAsync`) | Orchestration | the user's headers, merged with the mapping's |
| C2 | Cross-domain SubProcess start (`SubProcessRemoteInvoker`) | Execution | the mapping's resolved headers |
| C3 | Cross-domain completion relay (`RemoteInstanceCommandAppService.CompleteAsync`) | Orchestration (child domain) | the settling hop's user, often **none** (a timer or Inbox-settled hop) |
| C4 | Same-domain completion backup (`DaprOrchestrationForwarder`) | Inbox | **none** |

Same-domain and co-hosted starts and completions run in process and never reach these endpoints, except the C4 backup, which posts to its own domain's `complete`.

Any allow grant refuses a role-less caller, so a gate based only on roles would refuse C4 and most C3 calls.

- **C4 refused:** the forwarder drops non-transient answers.
- **C3 refused:** the parent has **no other path**. Event topics are per publishing domain, and a child domain's Inbox drops a completion whose parent is hosted elsewhere (`InstanceSubCompletedEventHandler`'s domain guard), so the parent stays parked.

The gate therefore has to recognise the runtime itself.

## The runtime-caller token

Each of the four callers stamps one header, as the **last** step before sending, so no ambient-user or mapping header can replace or remove it:

```
x-vnext-runtime-token: v1;k={keyId};t={unixSeconds};d={callerDomain};c={callerClass};s={base64url(mac)}
```

- **The MAC** is HMAC-SHA256 over `v1 \n keyId \n t \n callerDomain \n callerClass \n operation \n domain \n workflow \n instance`. The route values are lower-cased, and `-` stands in for the instance on sub-start.
- **Route binding.** The token is bound to the **operation and the target route values**: domain, workflow, and for `complete` the parent instance. It is not bound to the URL path.
  - The callers spell the path differently (`api/v1/` vs `api/v1.0/`), a BaseUrl may carry a prefix, and Dapr service invocation and plain HTTP deliver it differently.
  - The route values are what the sender knows and what the receiver reads from routing.
  - A token is therefore worthless on any other route or for any other parent instance.
- **Time window.** It is valid within `MaxClockSkewSeconds` (default 300, range 60–900) of the receiver's clock.
- **Lifetime.** It is minted per send attempt and never stored. One leaked at an older, ungated callee is worth nothing elsewhere and expires in minutes. A static shared secret would not: an older callee persists inbound headers in `InstanceTransitions."Header"`, which the public transitions history API serves.
- **Verification.** The MAC is compared in constant time and checked **before** the timestamp. A forgery reports `bad_signature`, never "only stale".
- **Attribution.** `callerDomain` and `callerClass` (`subflow`, `subprocess`, `relay`, `inbox`) are signed, but every key holder can claim any value. They are for logs, never for authorization.
- **No other targets.** Calls to targets a flow author chooses (HttpTask, DaprService and others) are never signed.

### Key ring

One JSON file per environment, mounted read-only from a Kubernetes Secret into the Orchestration, Execution and Inbox containers of every domain. The images run as a non-root user (`workflowuser`) and a Secret's files are owned by root, so give the pod a `securityContext.fsGroup` and the volume `defaultMode: 0440`: the file is then group-owned by that group, which the container's processes belong to. A plain `0400` without `fsGroup` cannot be read, and a host with `KeyFile` set fails to start (`cannot be read: UnauthorizedAccessException`).

```json
{ "activeKeyId": "k2", "keys": { "k2": "<base64, at least 32 bytes>", "k1": "<base64>" } }
```

- **Signing and verifying.** The active key signs; every key in the ring verifies.
- **Rotation, restart-based:**
  1. Add the new key everywhere.
  2. Move `activeKeyId` everywhere.
  3. Remove the old key once no traffic names it (the `vnext.runtime_caller.kid` tag).
- **Why a file and not configuration.** Flow scripts can read `IConfiguration`, the Dapr secret store (`ScriptBase.GetSecret`) and environment variables, but the script sandbox bans `System.IO` file access.
  - Configuration therefore holds only the path. A key-looking value under `Workflow:RuntimeCaller` fails startup.
  - Only the Orchestration host runs flow scripts. A key ring there with `Scripting:Sandbox:Enabled=false` fails startup unless `Workflow:RuntimeCaller:AllowWithoutScriptSandbox=true`. That bypass logs Critical (EventId 50049) at every start, and only the Chair may allow it.

### Where the header goes, and where it never goes

- **Ingress strip.** The Orchestration host moves the header out of every request, on every route and in every mode (Off included), into an `HttpContext` feature (`RuntimeCallerTokenMiddleware`).
  - It runs right after `UseExceptionHandler`, before `UseCurrentUser` and before Aether's `UseHttpBodyLogging`, which serialises every request header.
  - So the token never reaches request logs, `input.Headers`, transition records, job payloads or `context.Headers`.
  - As a second layer, Orchestration's `appsettings.json` lists the header under `Telemetry:Logging:Body:AdditionalSensitiveHeaderNames`, so Aether redacts it should a future middleware ever log headers before the strip. Only Orchestration logs request headers (`UseHttpBodyLogging`). An environment that overrides that array must keep the entry.
- **Never logged:** the key, the token, its MAC, and the caller's roles.

## The gate

`[SubEndpointGate(operation)]` on `StartSubAsync` and `CompleteSubAsync` is an authorization filter. It decides **before the body is bound and before anything is read or written**.

| `Mode` (and `CompleteMode` for `complete`) | Effect |
|---|---|
| `Off` (default) | Nothing — the endpoint behaves as before this feature. |
| `Observe` | Decides, logs `would refuse` (Warning, EventId 50051) and admits. |
| `Enforce` | Refuses with **403 `Authorization:110001`** (Warning, EventId 50050). |

Decision:

1. A **valid token** for this route is admitted (`admitted_runtime`), with no role lookup, under every grant set.
2. Otherwise the caller is decided by `Workflow:SubEndpointGate:Grants`, using `ICallerRoleResolver.ResolveRoleSetAsync` and the shared role-grant rule (`EvaluateRolesStatic`).
   - Grants are single roles or `allOf` / `anyOf` of static roles, plus `$Everyone`.
   - Identity or instance-bound leaves (`$InstanceStarter`, `$user.…`, `$role.…`) cannot be evaluated without an instance and are rejected at startup.
   - **Runtime only** is written `[{ "role": "$Everyone", "grant": "deny" }]`.
   - **Ops tooling** gets a role, for example `[{ "role": "vnext.ops", "grant": "allow" }]`, and never the key.
3. If the role provider cannot answer for a caller without a token, the answer is **503 `Authorization:110006`**: retryable, never an admit.

Startup validation fails when:
- `Mode` or `CompleteMode` is not `Off` but `Grants` is empty (the shared rule would read an empty set as "allow everyone");
- no key ring is loaded (every runtime call would arrive without a token);
- a grant is malformed or not evaluable.

### Observability

- **Logs:** `SubEndpointGateRefused` (50050) and `SubEndpointGateWouldRefuse` (50051), with operation, route, parent instance (`complete`), `Reason`, `CallerClass`, `CallerDomain` and `TokenReason`. Caller class and domain are `-` unless the token's signature verified, so a client cannot write into them. The two reasons are separate vocabularies:
  - `TokenReason` is the token verdict: `missing`, `malformed`, `unknown_key`, `bad_signature`, `clock_skew` (never `valid` on these lines).
  - `Reason` is why the grants did not admit the caller: `grant_denied` or `roles_unavailable`.
- **Span tags** on the endpoint's server span, whenever the gate is on:
  - `vnext.gate.decision`: `admitted_runtime`, `admitted_grant`, `would_refuse`, `refused` or `unavailable`;
  - `vnext.gate.reason`: `grant_denied` or `roles_unavailable`, on the three refusing decisions only;
  - `vnext.runtime_caller.verdict`: the token verdict (`valid`, `missing`, `malformed`, `unknown_key`, `bad_signature`, `clock_skew`);
  - `vnext.runtime_caller.class`, `.domain` and `.skew_s` once the token's signature verified; `.kid` whenever the key id is well formed.
- **Startup line:** every host logs which key id it signs with (`RuntimeCallerIdentityLoaded`, 50047), or that it signs nothing (`RuntimeCallerIdentityNotConfigured`, 50048).
- **Inbox:** a forward refused by the gate is **re-delivered**, not dropped (`InboxForwardRefusedByGate`, 50052; `vnext.forward.outcome = gate_refused`). After the inbox retry budget it is dead-lettered and can be replayed (see Failure modes). Only a signed forward (the `complete` backup) is re-delivered this way; every other 4xx keeps today's drop.

## Configuration

| Key | Hosts | Default | Meaning |
|---|---|---|---|
| `Workflow:RuntimeCaller:KeyFile` | Orchestration, Execution, Inbox | unset | Path to the mounted key ring. Unset: the host neither signs nor verifies. |
| `Workflow:RuntimeCaller:Required` | Orchestration, Execution, Inbox | `false` | `true` fails startup when `KeyFile` is blank; set it together with the mount. A `KeyFile` that is set must always load: a missing, unreadable or invalid file fails startup whatever `Required` says. |
| `Workflow:RuntimeCaller:MaxClockSkewSeconds` | Orchestration (verifies); range checked at startup on all three | `300` | Accepted token age / clock distance (60–900). |
| `Workflow:RuntimeCaller:AllowWithoutScriptSandbox` | Orchestration | `false` | Chair-only bypass, see above. |
| `Workflow:SubEndpointGate:Mode` | Orchestration | `Off` | `Off` / `Observe` / `Enforce` for both endpoints. |
| `Workflow:SubEndpointGate:CompleteMode` | Orchestration | = `Mode` | Overrides `Mode` for `complete` only. |
| `Workflow:SubEndpointGate:Grants` | Orchestration | none | Who may call without a token; non-empty when on. |

All keys are optional. The Helm chart (`vnext-helm-charts`) needs a mount and env entries before any environment can turn the gate on; that is a follow-up. On a co-hosted host set (`APP_DOMAINS`) the configuration is per host set, so it applies to every domain that host set serves.

## Enabling it in an environment

1. **Release everywhere.** Every domain's Orchestration, Execution and Inbox run a release with this feature, still unconfigured. Only the ingress strip is new. An older callee would copy the token into its headers, and an older caller sends none.
2. **Key everywhere.** Mount one ring on all three hosts of every domain, with `KeyFile` and `Required=true`. Check that every host's startup line names the same active key id. Signing starts; nothing verifies yet.
3. **Observe on domain X.** Move to step 4 only when:
   - there are zero `would refuse` lines with `TokenReason` `malformed`, `unknown_key`, `bad_signature` or `clock_skew`;
   - every `TokenReason: missing` is explained (no runtime caller among them — a runtime caller that does not sign is a host without the ring);
   - `admitted_runtime` traffic has been seen for every caller class X uses. A synthetic run covers any class with no organic traffic. C4 needs no forcing: every same-domain completion is forwarded by the Inbox.
4. **Enforce on X**, only after:
   - the shared key ring across domains is confirmed (council condition C-3);
   - reachability of the routes through the gateway is confirmed (C-4);
   - every domain hosting X's children attests signing.

   Consider `CompleteMode=Observe` first (next section).

Rollback is `Mode=Off` and `CompleteMode=Off` (or `CompleteMode` unset) on the affected domain, plus a restart of its Orchestration host (the options are read at start): no flow republish, and no other domain is touched. If a token or key was ever exposed, rotate the key.

**Rolling back the release** (any host back to an image without this feature) once a ring is mounted reopens both mixed-version hazards the enable order avoids. Go in this order:

1. `Mode=Off` and `CompleteMode=Off` on every domain, then restart its Orchestration. Otherwise a reverted Execution or Orchestration sends start and completion calls with no token and is refused, and a reverted Inbox drops a refused `complete` backup instead of re-delivering it, which strands the parent.
2. Unmount the ring and clear `KeyFile` and `Required` on every Orchestration, Execution and Inbox host, then restart them. A host keeps signing while it holds the ring, and a reverted callee copies the token into `InstanceTransitions."Header"` and its logs.
3. Revert the images.

## Failure modes

| Refused caller | Effect | Recovery |
|---|---|---|
| C1 SubFlow start | The parent faults (v0.0.103 and later, #1026 `00f3be33`; older parents stay Busy). | `retry` on the parent after fixing the key ring. |
| C2 SubProcess start | The task fails and the parent's error boundary decides. With `Ignore` / `Log` the child is silently never started. | Fix the ring; replay per the flow's own design. |
| C3 completion relay | **The parent stays parked. There is no backup** (see above). | Manual. Fix the ring, then re-drive the completion: the child domain's outbox still holds the `instance.sub.completed` event (retention-bound), and its data is the body of the `complete` call, which an operator role admitted by `Grants` may post. This is why `CompleteMode` exists and why Enforce on `complete` comes last. A durable C3 retry is a separate issue (council condition C-5). |
| C4 Inbox backup | Re-delivered with the inbox backoff (`RetryBaseDelay` doubling, `MaxRetryCount` 5: about 15 minutes at the 1-minute default), then dead-lettered. | Fix the ring, then replay in the parent domain's inbox: `UPDATE sys_queues."InboxMessages" SET "Status" = 0, "RetryCount" = 0, "NextRetryTime" = NULL, "LockedBy" = NULL, "LockedUntil" = NULL WHERE "Status" = 4 AND "EventName" = 'instance.sub.completed';` (`4` = DeadLetter, `0` = Pending; the receiver is idempotent, so a replay of an already-settled completion is a no-op). |

## What stays open

- **Flow authors.** A flow author can still make the runtime sign calls for them, for example a SubFlow into any flow. The gate stops clients, not flow authors.
- **Shared ring.** Any runtime holding the ring can mint tokens for every domain sharing it; per-domain keys are a follow-up.
- **Role grants** for callers without a token are only as strong as the gateway's identity-header hygiene, like every other vNext role decision.
- **Sibling endpoints.** The ~20 sibling runtime-internal endpoints (`sub/state`, `sub/fault`, `sub/cancel`, `busy`, `internal/*`, `child-*`, `related-data`, `internal/file`) are not gated.
  - Suggested order: `internal/related-data` and `internal/file` first, since they serve unfiltered data; then the parent-settlement endpoints.
  - Each needs only a new operation and its sender's stamp.
- **Raw headers persisted.** Every inbound header, a client's `authorization` included, is persisted raw in `InstanceTransitions."Header"` and served by the transitions history API. This is a separate issue (council condition C-6).
