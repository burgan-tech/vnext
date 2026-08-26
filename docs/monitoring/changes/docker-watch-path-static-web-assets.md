# Docker / `dotnet watch` path düzeltmesi — değişiklik matrisi

Bu dosya iki ayrı iyileştirmeyi özetler: **(1)** Static Web Assets path sorunu, **(2)** Docker geliştirmede PostSharp’ın `SkipPostSharp` ile devre dışı bırakılması.

**Konu (1):** Linux konteyner içinde `dotnet watch` çalışırken, kaynak kod Windows’tan bind mount edildiğinde oluşan `obj\Debug\...\staticwebassets.development.json` karışık yol (`/` + `\`) hatası.

**Çözüm (SWA):** Bu üç host yalnızca REST API sunduğu için Static Web Assets (SWA) devre dışı bırakıldı; ilgili geliştirme manifest’i artık üretilmiyor veya kullanılmıyor.

---

## Dosya × değişiklik matrisi

| #   | Dosya                                                                                                        | Ne değişti?                                                                                                                 | Etki                                                                                          |
| --- | ------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| 1   | `vnext/orchestration/BBT.Workflow.Orchestration.HttpApi.Host/BBT.Workflow.Orchestration.HttpApi.Host.csproj` | `<PropertyGroup>` içine `<StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>` eklendi; kısa bir XML yorum satırı eklendi | Orchestration host’ta SWA kapatıldı → `staticwebassets.development.json` path zinciri kesilir |
| 2   | `vnext/execution/BBT.Workflow.Execution.HttpApi.Host/BBT.Workflow.Execution.HttpApi.Host.csproj`             | Aynı property: `<StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>`                                                     | Execution host’ta aynı koruma                                                                 |
| 3   | `vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host/BBT.Workflow.Monitor.HttpApi.Host.csproj`                | Aynı property: `<StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>`                                                     | Monitor host’ta aynı koruma                                                                   |

---

## Özellik × dosya matrisi

| Özellik                              | Orchestration `.csproj` | Execution `.csproj` | Monitor `.csproj` |
| ------------------------------------ | ----------------------- | ------------------- | ----------------- |
| `StaticWebAssetsEnabled` = `false`   | Evet                    | Evet                | Evet              |
| Açıklayıcı XML yorumu (SWA / Docker) | Evet                    | Hayır               | Hayır             |

---

## Kapsam dışı (bu path işi için bilinçli olarak yapılmadı)

| Yaklaşım                                                       | Durum                              | Not                                                                                                                |
| -------------------------------------------------------------- | ---------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| `BaseIntermediateOutputPath` (`obj/`) ile global normalizasyon | Uygulanmadı (denendi, geri alındı) | `common.props` / `Directory.Build.props` zamanlaması MSB3539 uyarısına yol açabiliyor; asıl hata SWA manifest’iydi |
| `Directory.Build.props` patch                                  | Final çözümde yok                  | Yukarıdaki nedenle                                                                                                 |
| Dockerfile / compose ortam değişkeni (path için)               | SWA çözümünde kullanılmadı         | Path için MSBuild’de yeterli oldu; **PostSharp için** `Dockerfile.dev` değişiklikleri aşağıdaki ayrı başlıkta      |

---

## Konu (2): PostSharp — `Dockerfile.dev` ile isteğe bağlı etkinleştirme

### Sorun

Dev ortamında PostSharp'ın çalışabilmesi için `NETStandard.Library.Ref 2.1.0` targeting pack'i gerekir. Önceki `Dockerfile.dev` dosyalarında bu paket yoktu ve `SkipPostSharp=true` hardcoded olarak CMD'ye yazılmıştı. Dolayısıyla PostSharp'ı açmak istendiğinde image rebuild + Dockerfile düzenlemesi gerekiyordu.

### Çözüm

| Adım | Açıklama |
|------|----------|
| 1. Targeting pack kurulumu | `NETStandard.Library.Ref 2.1.0` paketi image build sırasında NuGet cache (`/root/.nuget/packages/`) ve PostSharp fallback dizinine (`/var/tmp/postsharp/NuGetFallback/`) indiriliyor |
| 2. `ENV SKIP_POSTSHARP=true` | Default olarak PostSharp kapalı — lisans/performans sorunları önleniyor |
| 3. CMD → shell form | `bash -c "... /p:SkipPostSharp=$SKIP_POSTSHARP"` — runtime'da environment variable çözümleniyor |

### Etkilenen dosyalar

| Dockerfile.dev | Proje |
|---|---|
| `vnext/orchestration/.../Dockerfile.dev` | Orchestration API |
| `vnext/execution/.../Dockerfile.dev` | Execution API |
| `vnext/monitoring/.../Dockerfile.dev` | Monitor API |
| `vnext/workers/BBT.Workflow.DbMigrator/Dockerfile.dev` | DbMigrator |

### Kullanım

**Default (PostSharp AÇIK — orchestration/execution):** `etc/docker/.env.orchestration.dev` ve
`.env.execution.dev` dosyaları `SKIP_POSTSHARP=false` set eder; compose `env_file` değerleri
Dockerfile `ENV`'ini ezdiği için container weaving açık başlar. Bunun nedeni trace bütünlüğü:
`transition/{key}` ve `Task.Execute.{key}` span'larını `[Trace]` aspect'i üretir — weaving
kapalıyken bu span'lar hiç oluşmaz ve `SetDisplayName` çağrıları ambient job/server span'ının
adını her task'ta üzerine yazar (trace'te task'lar kaybolur). Bkz.
`docs/monitoring/correlation-and-tracing.md` § "What to check", madde 0.

**PostSharp'ı kapatmak için** (watch-rebuild hızı trace'ten önemliyse): ilgili `.env.*.dev`
dosyasında `SKIP_POSTSHARP=true` yap. Monitoring ve DbMigrator container'larında `Dockerfile.dev`
default'u (`true`) geçerlidir — `[Trace]` kodu çalıştırmadıkları için weaving'e ihtiyaçları yok.

Not: `dotnet watch`'un hot-reload patch'leri weave edilmez; `[Trace]`'li bir metoda dokunan
hot-reload sonrası aspect span'ı bir sonraki tam rebuild'e kadar kaybolabilir.

Veya tek seferlik:

```bash
docker run -e SKIP_POSTSHARP=false <image>
```

### Not

Image rebuild gereklidir (targeting pack image'a gömülü olduğundan sadece ilk sefer):

```bash
cd vnext/etc/docker
docker compose -f docker-compose.dev.yml build --no-cache vnext-app vnext-execution-app vnext-monitoring-app vnext-db-migrator
```

---

## Referans hata mesajı (önce — SWA path sorunu)

```text
Failed to read '/app/.../obj\Debug/net10.0/staticwebassets.development.json':
Could not find a part of the path '.../obj\Debug/net10.0/...'
```

---
