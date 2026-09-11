# Dapr Component Footprint — which host needs which building block

Every Dapr component a sidecar loads costs a client and a connection pool, whether or not the
application ever calls it. This page is the **evidence-backed map** of what each host actually
uses, so a component is provisioned where it is consumed and nowhere else.

It is derived from consumption points in code (the type that injects the abstraction, or the
`DaprClient` call), not from DI registrations — a registration is lazy and proves nothing.
When this page and the code disagree, the code wins; re-derive with the grep recipes at the end.

## Building block → the single place that talks to it

| Building block | Entry point | Backing type |
|---|---|---|
| state — platform cache | `IDistributedCacheService` (Aether) | `DaprDistributedCacheService`, wired by `AddDistributedCache` from `DAPR_STATE_STORE_NAME` |
| state — domain task | `IStateStoreClient` | `DaprStateStoreClient`; `StateStoreTask` / `CacheAsideTask`, defaults to `DAPR_STATE_STORE_NAME` |
| lock — platform | `IDistributedLockService` (Aether) | `DaprDistributedLockService`, wired by `AddDistributedLock` from `DAPR_LOCK_STORE_NAME` |
| lock — workflow resource | `IResourceLockService` | `DaprResourceLockService` (`DaprClient.Lock` / `Unlock`); `ResourceLockStep`, `InstanceCancellationService` |
| pubsub — events | `IDistributedEventBus` | `DaprEventBus` (Aether) |
| pubsub — outbox nudge | `IOutboxWakeupNotifier` | `DaprOutboxWakeupNotifier` (Aether), gated by `Aether:Outbox:WakeupSignalEnabled` |
| pubsub — domain task | `DaprPubSubTaskInvoker` | binding's **required** `PubSubName`; there is no platform default |
| bindings | `NotificationTaskExecutor`, `StateNotificationDispatcher`, `DaprBindingTaskInvoker` | `DaprClient.InvokeBindingAsync` |
| jobs | `AddDaprJobScheduler()` + the `/job/{name}` callback | `DaprJobScheduler` (Aether) |
| secretstore | `AddDaprSecretStore` in each `Program.cs` | gated by `Vault:Enabled` |
| configuration | — | **nothing calls the Dapr Configuration API** |
| actors | — | **no actor code anywhere**; every state component sets `actorStateStore: "false"` |

## The matrix

| | orchestration | execution | inbox | outbox | db-migrator |
|---|---|---|---|---|---|
| **state** | ✅ platform cache | ✅ *domain tasks only* | ❌ | ❌ | ❌ |
| **lock** | ✅ | ❌ | ❌ | ❌ | ✅ |
| **pubsub** | ✅ *one publish* | ⚠️ domain-authored only | ✅ | ✅ | ❌ |
| **pubsub-broadcast** | ⬜ kept, unused | ❌ | ❌ | ❌ | ❌ |
| **configuration** | ⬜ kept, unused | ⬜ | ⬜ | ⬜ | ⬜ |
| **bindings** | ✅ notification | ⚠️ domain-authored only | ❌ | ❌ | ❌ |
| **jobs (scheduler)** | ✅ | ❌ | ❌ | ❌ | ❌ |
| **secretstore** | ⚠️ `Vault:Enabled` | ⚠️ | ⚠️ | ⚠️ | ⚠️ |

✅ consumed · ⚠️ conditional · ⬜ deliberately kept though unused · ❌ removed / never provisioned

### Why each ✅ is there

- **orchestration / state** — `CacheSet<T>`, `ComponentCacheStore`, `StateFunctionCache`,
  `DataFunctionCache`, `InstanceSchemaFunctionCache`, `CachingDiscoveryRegistryClient`,
  `DistributedCacheIdempotencyStore`.
- **orchestration / lock** — `InstanceStatusLock` (the Busy mutex), `TransitionLockScopeFactory`,
  `DistributedCacheIdempotencyStore`, `DiscoveryCacheRefresher`, `SchemaMigrationOrchestrator`,
  `DomainDiscoveryInitializationHostedService`, plus `DaprResourceLockService`.
- **orchestration / pubsub** — publish only, and only `OutboxWakeupEvent`. Domain events use
  `DomainEventDispatchStrategy.AlwaysUseOutbox` (DB rows), and `AddAetherOutbox` is called with
  `withHostedService: false`, so the outbox processor does **not** run here. No subscription.
- **orchestration / bindings** — `vnext-notification-email` / `-sms` / `-state`; the notification
  executors live in the Application layer, which only the orchestration host loads.
- **execution / state** — `DaprStateStoreClient` alone. A `StateStoreTask` or `CacheAsideTask` with
  no explicit `storeName` falls back to `DAPR_STATE_STORE_NAME`, so the component must exist even
  though the execution process resolves no `IDistributedCacheService`.
- **inbox / pubsub** — `DaprEventDiscoveryController` publishes `/dapr/subscribe`; deliveries land on
  `DaprEventController`.
- **outbox / pubsub** — `OutboxProcessor` → `DaprEventBus` is the platform's real publisher, and
  `DaprSubscribeController` subscribes to the wakeup topic.
- **db-migrator / lock** — `SchemaMigrationRunner` → `SchemaMigrationOrchestrator.MigrateSchemaWithLockAsync`.

### Why the ⬜ entries stay

- **`pubsub-broadcast` on orchestration** — a product decision, not an oversight.
  `DAPR_PUBSUB_BROADCAST_STORE_NAME` is read by no C# code today; the component is held in place for
  a planned pod-to-pod invalidation path. Removed from execution / inbox, where nothing planned it.
- **`configuration` (`vnext-config`, `configuration.redis`)** — kept on every host by decision, even
  though the Dapr Configuration API is never called.

Both are listed so a future audit does not "discover" them again and delete them.

## Store names must resolve, and two of them look wrong but are not

Every `DAPR_*_STORE_NAME` a host reads must name a component that host's sidecar actually loads.
A name that resolves to nothing fails only at first use, which is why the execution host once
carried `vnext-execution-state` / `-pubsub` / `-secret` against components named `vnext-state` /
`vnext-pubsub` / `vnext-secret` (and a sidecar `config.yaml` secrets scope on the same phantom
store) — latent, because `Vault:Enabled` is `false` locally and no example flow authored a
`StateStoreTask` without an explicit `storeName`. Fixed in 0.0.90; cross-check with the last grep
recipe below after touching any component or env file.

Two apparent mismatches are correct and must be left alone:

- **`vnext-pubsub-inbox` / `vnext-pubsub-outbox` vs. orchestration's `vnext-pubsub`.** A Dapr
  pub/sub component name is *local to the sidecar*; the Redis Streams key is the topic. All four
  components are `pubsub.redis` against the same `vnext-redis:6379`, so a nudge published on
  `vnext-pubsub` is consumed through `vnext-pubsub-outbox`. The per-worker names exist to let the
  workers carry their own settings. The Helm chart collapses them to one `-pubsub` component; both
  shapes are valid.
- **The inbox and outbox share `consumerID: "vnext-workers"`.** Redis Streams consumer groups are
  per-stream, and the two workers subscribe to disjoint topics.

## What changed in the runtime repo (0.0.90)

Applied here; **mirror the same reasoning in `vnext-helm-charts`**.

| Change | Where |
|---|---|
| Monitor API host removed in full | `monitoring/`, `etc/monitoring/`, `test/BBT.Workflow.Monitor.Application.Tests/`, `docs/monitoring/`, `BBT.Workflow.slnx`, compose files, `run-docker.sh` (`--monitor` flag gone), `vnext-meta/deprecations.json` records it |
| `lock` component dropped | execution, inbox, outbox |
| `state` component dropped | inbox, outbox |
| `pubsub-broadcast` dropped | execution, inbox |
| `AddDistributedCache` / `AddDistributedLock` calls dropped | execution host — nothing in that process resolved either |
| `AddRedis()` dropped | orchestration, execution, db-migrator — Aether opens an **eager** `ConnectionMultiplexer` at startup and vNext has no `IConnectionMultiplexer` consumer. `Redis` config sections and `Redis__*` env removed with it |
| Dapr placement removed | `dapr-placement` container, every `--placement-host-address` arg, `DAPR_PLACEMENT_HOST` env. No actors exist; `actorStateStore` is `"false"` everywhere |
| Dead env keys removed | `DAPR_LOCK_STORE_NAME` (execution), `DAPR_STATE_STORE_NAME` (inbox/outbox), `DAPR_PUBSUB_BROADCAST_STORE_NAME` (execution/inbox/outbox) |
| Execution store names aligned | `.env.execution.dev` / `.stage` and `etc/execution/dapr/config.yaml` named `vnext-execution-{state,pubsub,secret}`; the provisioned components are `vnext-{state,pubsub,secret}` |

## Helm work list (`vnext-helm-charts`, `charts/vnext`)

The chart has **no monitoring deployment**, so the host removal needs nothing there.
(`templates/common/monitoring-config/` is Grafana dashboards — unrelated, leave it.)

### 1. Scope the platform components — the biggest win

`templates/common/common-dapr-components/` declares the platform components with **no `scopes:`**,
so every daprd in the namespace loads all of them and opens a Redis pool per component. The domain
templates in the same folder (`vnext-pubsub-template`, `vnext-notification-template`,
`vnext-conversation-template`, `resiliency-*`) already use `scopes:` — apply the same pattern.

App-ids come from `templates/_helpers.tpl` (`vnext.daprAnnotations`), with `{d}` = `.Values.global.appDomain`:
`vnext-{d}-app` (orchestrator) · `vnext-{d}-execution-app` · `vnext-{d}-worker-inbox-app` ·
`vnext-{d}-worker-outbox-app` · `vnext-{d}-db-migrator-app`.

| Template | Action |
|---|---|
| `state-component.yaml` | `scopes:` → `vnext-{d}-app`, `vnext-{d}-execution-app` |
| `redis-lock.yaml` | `scopes:` → `vnext-{d}-app`, `vnext-{d}-db-migrator-app` |
| `pubsub-redis.yaml` | `scopes:` → `vnext-{d}-app`, `vnext-{d}-worker-inbox-app`, `vnext-{d}-worker-outbox-app` (add execution only if a domain authors a `DaprPubSubTask` against it) |
| `pubsub-redis-broadcast.yaml` | `scopes:` → `vnext-{d}-app` only |
| `redis-configuration.yaml` | keep as-is (decision above); scoping it to nothing is not possible, so leave it unscoped or scope it to the hosts you keep it for |
| `secretstore-component.yaml` | `scopes:` → the five app-ids (all hosts read it when Vault is on) |

Also: `state-component.yaml` keeps `actorStateStore: "false"` — correct, do not flip it.

### 2. Make the Dapr env per-app

`templates/_helpers.tpl` → `vnext.commonEnvVars` emits **every** `DAPR_*` key into **every** app's
configmap. Split it so each host gets only what it reads:

| Key | Emit for |
|---|---|
| `DAPR_STATE_STORE_NAME` | orchestrator, execution |
| `DAPR_LOCK_STORE_NAME` | orchestrator, db-migrator |
| `DAPR_PUBSUB_STORE_NAME` | orchestrator, worker-inbox, worker-outbox (+ execution if used) |
| `DAPR_PUBSUB_BROADCAST_STORE_NAME` | orchestrator |
| `DAPR_SECRET_STORE_NAME`, `DAPR_HTTP_PORT`, `DAPR_GRPC_PORT` | all |
| `DAPR_PLACEMENT_HOST` | **remove** — read by no code, and placement is being turned off |

Leaving a name set while the component is unscoped for that app is the failure mode to avoid: the
app resolves a store name the sidecar cannot serve, and the error only appears at first use.

### 3. Turn off actors / placement

`charts/dapr/values.yaml` has `global.actors.enabled: true` and `global.actors.serviceName: "placement"`.
No vNext code uses actors and every state component is `actorStateStore: "false"`, so set
`dapr.global.actors.enabled: false` in the umbrella values and drop
`global.dapr.placementHost` (`values.yaml`, currently `dapr-placement:50005`). This removes the
placement StatefulSet from the namespace. Keep `global.scheduler.enabled: true` — orchestration's
background jobs run on the Dapr Jobs API.

### 4. Drop the dead Redis wiring

`Redis__Standalone__EndPoints__0` is emitted into five configmaps
(`orchestrator`, `execution`, `worker-inbox`, `worker-outbox`, `db-migrator`) via
`vnext.redisEndpoint`. With `AddRedis()` gone from the runtime, that key configures nothing —
remove it from those configmaps. The Redis *server* stays: it backs the Dapr state/lock/pubsub
components.

### 5. Sizing follow-up

Sidecar resources are not set by the chart (they come from `<component>.podAnnotations`). Once the
component count per sidecar drops, revisit `dapr.io/sidecar-*` requests/limits per app —
see the `daprd-besteffort-gap` note and `charts/vnext/docs/RESOURCE_TUNING.md`.

## Re-deriving this map

```bash
# who consumes the platform cache / lock
grep -rn 'IDistributedCacheService\|IDistributedLockService\|IResourceLockService' --include='*.cs' src orchestration execution workers

# who talks to a building block through DaprClient directly
grep -rn 'PublishEventAsync\|InvokeBindingAsync\|daprClient.Lock\|ScheduleJobAsync' --include='*.cs' src orchestration execution workers

# config keys read in code vs. configured
grep -rhoE 'DAPR_[A-Z_]+' --include='*.cs' src orchestration execution workers | sort | uniq -c
grep -rhoE 'DAPR_[A-Z_]+' --include='*.json' --include='*.yml' orchestration execution workers etc | sort | uniq -c
```

A key that appears in the second list but not the first is dead configuration.
