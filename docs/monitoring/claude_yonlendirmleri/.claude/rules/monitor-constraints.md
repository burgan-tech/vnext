---
description: Monitoring gelistirmesinde KATI kurallar ve kisitlamalar
globs:
alwaysApply: true
---

# Monitor API — Hard Constraints

Bu kurallar HER ZAMAN gecerlidir ve hicbir kosulda ihlal edilemez.

## Dosya Degisiklik Kapsamı

Kaynak: `CLAUDE.md` §1 (detay ve tablo orada). Ozet asagida.

### Birincil yazim alani (monitoring)

Monitoring **Application** ve **API** katmanlari ayri projelerdir; dashboard/izleme ozellikleri burada gelistirilir:

- `vnext/monitoring/BBT.Workflow.Monitor.Application/`
- `vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host/`

### vNext Domain ve Infrastructure (kosullu yazim)

Asagidaki projelerde **degisiklik yapilabilir**, ancak **yalnizca ekleme** modeli gecerlidir:

- `vnext/src/BBT.Workflow.Domain/`
- `vnext/src/BBT.Workflow.Infrastructure/`

**Kritik:** Mevcut metotlarin, action'larin, imzalarin veya is kurali davranisinin **degistirilmesi yasaktir**. Ihtiyac **yeni** uyeler (yeni metot, yeni sinif, yeni repository operasyonu vb.) ile karsilanir. Mevcut API'ye **dokunma**; var olani bozma riski tasimayan **paralel yeni** API ekle.

### Diger vNext kodu (salt okunur — tuketim)

Asagidakiler ve genel olarak `vnext/src/BBT.Workflow.Application` ile orchestration/host/worker vb. **referans / tuketim** icindir; **dosya degisikligi yapilmaz**:

- `vnext/src/BBT.Workflow.Application/` ve bunun disinda kalan `vnext/` (monitoring + Domain/Infra istisnasi haric) yollar

Desenleri anlamak, lojik kopyalamak veya sozlesme dogrulamak icin OKU; orchestration application ve paylasilan kitapliklari **degistirme**. Ihtiyac monitor veya Domain/Infra **eklemeleri** ile karsilanir.

## Versiyon ve Publish

- **ASLA versiyon yukseltme** (workflow, task, schema veya proje versiyonlari).
- **ASLA publish komutu calistirma** (`dotnet publish`, `curl -X POST .../definitions/publish` vb.).
- Degisikliklerden sonra kullanicidan kaydetmesini, build etmesini ve test etmesini iste.

## Paket Yonetimi

- Monitor projelerinin veya transitif bagimliliklarin (`HttpApi.Shared`, `Application`, `Domain`) zaten referans ettigi paketler disinda yeni NuGet paketi ekleme.
- Gercekten gerekiyorsa once kullaniciya sor.

## Read-Only Prensibi

- Monitor API **ASLA** state mutate etmez. Command service yok, transition yok, event publish yok.
- Tum service'ler read-only query yapar.
- Repository cagirilari `AsReadOnly` / `AsNoTracking` varyantlarini kullanir.

## Gelistirme onceligi (tekrar kullanim)

Yeni ihtiyacta sirayla: **Aether SDK** → **`vnext/src`** icinde mevcut fonksiyon var mi → yoksa vNext mimarisi ve kod stiline uygun **yeni** kod (tercihen monitor; Domain/Infra gerekiyorsa **sadece ekleme**).

## Gelistirme Akisi

1. Degisiklikleri yap (`.cs` dosyalari — izin verilen kapsama uygun)
2. "Dosyalari kaydedip build eder misin?" diye sor
3. Kullanici build eder, test eder
4. Hata varsa kullanici bildirir
5. Cozum seceneklerini sun, kullanici onaylar
6. 1'den tekrar basla
