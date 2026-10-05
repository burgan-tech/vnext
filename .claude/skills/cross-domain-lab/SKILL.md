---
name: cross-domain-lab
description: "Test cross-domain behaviour on the local three-domain lab (core + partner + discovery): Dapr name resolution and invocation, discovery registry, SubFlow/SubProcess/trigger tasks, function descent. Triggers: \"cross-domain test\", \"çapraz domain\", \"iki domain\", \"partner domain\", \"useDapr test\"."
---

# Cross-Domain Lab

The lab lives in **vnext-example**: `labs/cross-domain/` (`lab.sh`, `README.md`,
`orchestration.overlay.env`, `VNEXT-BUILD-PLAN.md`). The runtime is built from this repo (vnext); the
lab runs it as `ghcr.io/burgan-tech/vnext/*:dapr-nr` images. **Never** write example flows or tests
into vnext — they go into vnext-example (integration-test policy:
[integration-testing §1](../../../docs/testing/integration-testing.md#1-policy--when-an-integration-test-is-required)).

General facts this skill does not repeat — where things live, wiring tests to a local runtime,
debugging a red test, scenario documentation — are in
[`docs/testing/integration-testing.md`](../../../docs/testing/integration-testing.md) §2, §4, §5, §6
and §7. Port offsets and the domain runbook are in `AGENTS.md` § *Runbook: "bring up domain X"*.

## When

- `Remote*` services, `DaprRemoteTransport`, `DaprDomainDiscoveryProvider`, `RemoteServiceExtensions`,
  trigger task executors (`Tasks/Executors/Trigger/*`, `Execution/Invokers/*RemoteInvoker`),
  `HandleSubFlowStep` / `ForwardToActiveSubflowStep`, the `InstanceQueryAppService` descent points or
  `DomainRegistrationService` changed → **major**, run against the lab.
- Unit tests are enough for log / doc / refactor-only changes.

## Procedure

1. **Check state, do not restart:** `labs/cross-domain/lab.sh status`. Three `/health` 200 plus two
   registry entries (`appId: vnext-app-core|partner`) means the lab is ready.
2. **Runtime changed:** `lab.sh images` (vnext source `VNEXT_SRC_DIR`, default `../vnext`) →
   `lab.sh down` → `lab.sh up`. All three domains run on **local** images; Dapr is pinned by
   `VNEXT_LAB_DAPR_VERSION` (1.18.0) — `daprio/daprd:latest` in `docker ps` means the lab is stale.
   `up` ends with `verify`; when it is red, read the pitfalls in the lab README.
3. **Writing a scenario — vnext-plan-gate applies.** A new flow/component needs an approved
   `labs/cross-domain/VNEXT-BUILD-PLAN.md` (or a new plan) first. Then:
   - parent side `core/{Workflows,Tasks}/<scenario>/`, child side the **`partner/`** root +
     `vnext.partner.config.json` (the SDK `LocalDomainPublisher` takes the config file name).
   - cross-domain task config: `{"domain":"partner","flow":"...","useDapr":true}`; id/key/filter in
     the mapping via `SetInstance/SetKey/SetFilterSpec`. SubFlow: `stateType:4`,
     `subFlow.process.domain:"partner"`.
   - `LocalDomainPublisher.ReplaceDomain` does not enter `config`/`process` keys, so `partner`
     references survive; `version` becomes `{v}-pkg.{cfg.version}+{domain}` everywhere (prefix
     resolution still finds a `1.0.0` reference).
   - after writing `.csx`, run `python3 labs/cross-domain/encode-scripts.py <folders>` — the runtime
     compiles from `code`, not from `location` (without the VS Code extension `code` stays empty).
   - reference implementation: `core/{Tasks,Workflows}/cross-domain-lab/`, `partner/`,
     `tests/Core.IntegrationTests/Tests/CrossDomainLab/` (11 tests, `SkippableFact`).
4. **Tests:** `tests/Core.IntegrationTests/Tests/<Scenario>/`, with `VNEXT_PARTNER_BASE_URL` set
   beside `VNEXT_BASE_URL` ([§4](../../../docs/testing/integration-testing.md#4-wire-the-tests-to-the-local-runtime)).
   Publish the partner domain in the **collection fixture** — external-stack mode never calls
   `OnAfterEnvironmentReadyAsync`. No partner URL ⇒ `Assert.Skip` (xunit v3).
5. **Trace check:** in OpenObserve the `Discovery.Resolve/{domain}` span carries
   `vnext.discovery.provider=dapr`, `vnext.discovery.resolution=convention|registry|cache`,
   `vnext.dapr.app_id`, `vnext.dapr.namespace`, followed by caller and callee sidecar spans.
6. **Documentation is mandatory:** scenario README + `TEST-SCENARIOS.md` row in the same commit
   ([§5](../../../docs/testing/integration-testing.md#5-write-or-extend-a-scenario)).
7. **Rollback drill:** run the same suite with `VNEXT_LAB_DISCOVERY_PROVIDER=http lab.sh up`;
   `Remote*` services fall back to registry `baseUrl` + HttpClient while `useDapr:true` tasks stay
   on Dapr (the provider does not drop a task's Dapr request). Plain `lab.sh up` returns to dapr.

## Lab-specific facts (when writing assertions)

- **App-id resolution order:** `Dapr.DomainOverrides[domain]` → registry `appId` (only with
  `RequireRegistryEntry=true`, via `PreferRegistryAppId`) → convention `vnext-{domain}-app`. The
  runtime default is `RequireRegistryEntry=false` (the registry is never asked). Lab app-ids are
  `vnext-app-{domain}`, so the overlay turns both flags on; with one missing, resolution falls back
  to the convention and every invoke gets a 500 `ERR_DIRECT_INVOKE`.
- **Remote retry shape:** mutation clients (`IRemoteInstanceCommandAppService`, `Retry`) make exactly
  **one** transport attempt; read clients retry (`RemoteServiceProfile`). An unreachable callee is a
  sidecar 500 `ERR_DIRECT_INVOKE` → `Error.Transient("remote_network_error")`.
- **Name resolution:** explicit `mdns` in compose (lab and vnext `etc/`), the `kubernetes` resolver in
  a cluster; app config is identical. **Do not use `nameformat`** — the daprd 1.16.x image lacks it
  ("couldn't find name resolver nameformat/v1"), the sidecar starts resolver-less and every invoke 500s.
- **Schema gap:** `useDapr` is defined in vnext-schema 0.0.52 only for task types 15/19; for
  11/12/13/14 `npm run validate` says "must match then schema". The runtime reads it on all of them —
  keep the field, know the gap.
- **Registry entry** (`DomainRegistrationService`): `{domainName, baseUrl=vNextApi:BaseUrl, healthUrl,
  appId=DAPR_APP_ID}` → `{ServiceDiscovery:BaseUrl}/{Domain}/workflows/{RegistryFlow}/instances/start?sync=true`.
- Function descent across domains follows the same rule as in one domain (`data` does not descend) —
  see `.claude/rules/vnext-workflow-developer.md` § Long-Polling / State Function.
- **Lab offsets:** core 0 · partner 10 · discovery 30 (partner API `:4211`, discovery `:4231`);
  orchestration Dapr HTTP is `42110+offset*100`.
