---
name: cross-domain-lab
description: Cross-domain (çoklu domain) davranışı lokalde test etmek — Dapr Name Resolution + Service Invocation, discovery registry, SubFlow/SubProcess/trigger task'ları ve fonksiyon descent'i. Tetikleyiciler: "cross-domain test", "cross domain lab", "çapraz domain", "iki domain", "partner domain", "discovery ile test", "useDapr test".
---

# Cross-Domain Lab

Lab **vnext-example**'da yaşar: `labs/cross-domain/` (`lab.sh`, `README.md`,
`orchestration.overlay.env`, `VNEXT-BUILD-PLAN.md`). Runtime kodu bu repoda (vnext) derlenir; lab onu
`ghcr.io/burgan-tech/vnext/*:dapr-nr` imajları olarak koşturur. Ürün örneğini ve testleri **asla** vnext
reposuna yazma — vnext-example'a yaz (CLAUDE.local.md integration-test politikası).

## Ne zaman

- `Remote*` servisleri, `DaprRemoteTransport`, `DaprDomainDiscoveryProvider`, `RemoteServiceExtensions`,
  trigger task executor'ları (`Tasks/Executors/Trigger/*`, `Execution/Invokers/*RemoteInvoker`),
  `HandleSubFlowStep`/`ForwardToActiveSubflowStep`, `InstanceQueryAppService` descent noktaları veya
  `DomainRegistrationService` değiştiyse → **major**, lab'a karşı koştur.
- Unit test yeterli: yalnız log/doküman/refactor.

## Prosedür

1. **Durum kontrolü, yeniden başlatma yok:** `labs/cross-domain/lab.sh status`. Üç `/health` 200 ve iki
   registry kaydı (`appId: vnext-app-core|partner`) varsa lab hazırdır.
2. **Runtime değişikliği varsa:** `lab.sh images` (vnext kaynağı `VNEXT_SRC_DIR`, varsayılan
   `../vnext`) → `lab.sh down` → `lab.sh up`. Üç domain de **lokal** imajlarla koşar (release `latest`
   Dapr çalışmasından önce); Dapr `VNEXT_LAB_DAPR_VERSION` (1.18.0) ile pinlidir — `docker ps`'te
   `daprio/daprd:latest` görüyorsan lab güncel değildir. `up` sonunda `verify` çalışır; kırmızıysa README'deki
   tuzaklara bak. En sık: DbMigrator DI doğrulama hatası (imaj eski) ve `vNextApi__BaseUrl` localhost.
3. **Senaryo yazımı — vnext-plan-gate geçerlidir.** Yeni akış/bileşen istenirse önce
   `labs/cross-domain/VNEXT-BUILD-PLAN.md` (veya yeni bir plan) onaylanır; sonra bileşenler:
   - parent tarafı `core/{Workflows,Tasks}/<senaryo>/`, child tarafı **`partner/`** kökü +
     `vnext.partner.config.json` (SDK `LocalDomainPublisher` config dosyası adını alır).
   - cross-domain task config'i: `{"domain":"partner","flow":"...","useDapr":true}`; id/key/filtre
     mapping'te `SetInstance/SetKey/SetFilterSpec`. SubFlow: `stateType:4`, `subFlow.process.domain:"partner"`.
   - `LocalDomainPublisher.ReplaceDomain` `config`/`process` anahtarlarına girmez → `partner`
     referansları korunur; `version` her yerde `{v}-pkg.{cfg.version}+{domain}` olur (prefix çözümleme
     sayesinde `1.0.0` referansı yine bulur).
   - `.csx` yazdıktan sonra `python3 labs/cross-domain/encode-scripts.py <klasörler>` — runtime
     `code`'dan derler, `location`'dan değil (VS Code eklentisi yoksa `code` boş kalır).
   - Referans uygulama: `core/{Tasks,Workflows}/cross-domain-lab/`, `partner/`,
     `tests/Core.IntegrationTests/Tests/CrossDomainLab/` (11 test, `SkippableFact`).
4. **Testler:** `tests/Core.IntegrationTests/Tests/<Senaryo>/`; `test.runsettings`'te
   `VNEXT_BASE_URL=http://localhost:4201` ve `VNEXT_PARTNER_BASE_URL=http://localhost:4211`. Partner
   publish'i **koleksiyon fixture'ında** yap — harici-stack modunda SDK'nın
   `OnAfterEnvironmentReadyAsync`'i çağrılmaz. Partner URL yoksa `Assert.Skip` (xunit v3).
5. **Trace doğrulaması:** OpenObserve'da `Discovery.Resolve/{domain}` span'i:
   `vnext.discovery.provider=dapr`, `vnext.discovery.resolution=convention|registry|cache`,
   `vnext.dapr.app_id`, `vnext.dapr.namespace`. Sonrasında caller + callee sidecar span'leri.
6. **Dokümantasyon zorunlu:** senaryo README'si + `TEST-SCENARIOS.md` satırı aynı commit'te
   (CLAUDE.local.md §2).

7. **Rollback tatbikatı:** `VNEXT_LAB_DISCOVERY_PROVIDER=http lab.sh up` ile aynı süiti koştur;
   `Remote*` servisleri registry `baseUrl` + HttpClient'a döner, `useDapr:true` task'ları Dapr'da
   kalır (task'ın Dapr talebi provider ile düşürülmez). Bayraksız `lab.sh up` dapr'a geri alır.

## Runtime gerçekleri (assert yazarken)

- `data` fonksiyonu gövdeyi subflow'a **indirmez**, yalnız `?extensions=`; `state`/`view`/`schema`/
  `authorize` iner (`InstanceQueryAppService`, `AuthorizeAppService`).
- Mutasyon client'ları (`IRemoteInstanceCommandAppService`, `Retry`) transport seviyesinde **tam bir
  deneme**; okuma client'ları retry'lı (`RemoteServiceProfile`). Callee'ye ulaşılamazsa sidecar 500
  `ERR_DIRECT_INVOKE` → `Error.Transient("remote_network_error")`.
- App-id çözümleme sırası: `Dapr.DomainOverrides[domain]` → registry `appId` (yalnız
  `RequireRegistryEntry=true` iken, `PreferRegistryAppId` ile) → konvansiyon `vnext-{domain}-app`.
  Runtime varsayılanı `RequireRegistryEntry=false` (registry'ye hiç gidilmez). Lab app-id'leri
  `vnext-app-{domain}` olduğundan overlay iki bayrağı da açar; biri eksikse çözümleme konvansiyona
  düşer ve her invoke `ERR_DIRECT_INVOKE` 500 alır.
- Ad çözümleme platform meselesidir: compose'da açık `mdns` (lab da vnext `etc/` de), cluster'da
  `kubernetes` resolver + template. Uygulama konfigürasyonu ikisinde de aynıdır. **`nameformat`
  kullanma:** daprd 1.16.x imajında yok ("couldn't find name resolver nameformat/v1") — sidecar
  resolver'sız kalkar ve her invoke 500 döner.
- `useDapr` task config'i vnext-schema 0.0.52'de yalnız task 15/19'da tanımlı; 11/12/13/14 için
  `npm run validate` "must match then schema" der. Runtime hepsinde okur — alanı koru, açığı bil.
- Registry kaydı `DomainRegistrationService`: `{domainName, baseUrl=vNextApi:BaseUrl, healthUrl,
  appId=DAPR_APP_ID}` → `{ServiceDiscovery:BaseUrl}/{Domain}/workflows/{RegistryFlow}/instances/start?sync=true`.

## Portlar (create-domain.sh)

app `4201+offset`, init `3005+offset`, orchestration Dapr HTTP `42110+offset*100`;
offset core 0 · partner 10 · discovery 30.
