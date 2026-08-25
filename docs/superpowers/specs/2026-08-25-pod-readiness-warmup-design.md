# Pod hazırlık ısıtması (readiness warm-up) — tasarım konsepti

**Durum:** konsept / onay bekliyor · **Tarih:** 2026-08-25

## Neden

Preprod ölçümü (trace `45333954`, deploy'dan 5 dk sonra, 6 contract pod'u):

| kalem | değer |
|---|---|
| wall | 32 359 ms |
| script derleme | 55 miss / **22 976 ms** (~418 ms/miss) |
| component L1 payı (L1 kapalı koşudan, `4a65f7d3`) | ~1 400 ms |

Tek bir kullanıcı akışı, filodaki **her pod'un** soğuk derlemesini tek başına ödüyor: load balancer hop'ları farklı pod'lara dağıtıyor, her pod'un L1'i ve script tip cache'i ayrı. Aynı script 6 kez derleniyor — pod başına bir kez, ki bu doğru davranış; yanlış olan, bedelini ilk kullanıcının ödemesi.

**Kritik gözlem:** ısıtmayı yalnız component L1 için tasarlarsak sorunun ~%6'sını çözeriz. Asıl maliyet script derlemesinde ve **aynı ısıtma döngüsü ikisini birden doldurur**:

```
warm(component) = L1'e byte doldur (~2 ms)  +  mapping'lerini derle (~418 ms)
                  └ küçük kazanç                └ ~200× büyük kazanç
```

Bu yüzden konsept "L1 warm-up" değil, **pod hazırlık ısıtması**: tek manifest, tek fan-out sinyali, tek readiness kapısı — arkasında iki cache birden dolar.

## Bugünkü mekanizma (değişmeyen zemin)

| | durum | kaynak |
|---|---|---|
| L1 | pod-içi `MemoryCache`, key generation token içerir | `ComponentL1Cache`, `CacheSet:166` |
| Publish → L2 ısıtma | **var**, yalnız publish eden pod, yalnız Redis | `CacheSet.WarmResolutionsAsync:357` |
| Publish → diğer pod'lara duyuru | **yok** — pod'lar publish'i öğrenmez, sadece key'i ıskalar | — |
| Startup ısıtma | **yok** | — |
| Broadcast kanalı | **yok** — `consumerID: "vnext-workers"` sabit; Redis Streams consumer-group'ta mesaj **tek** pod'a gider | `etc/workers/*/dapr/components/pubsub.yaml` |
| Script tip cache | pod-içi, content-addressed, async single-flight | `CSharpEvaluator._typeCache` |

## Tasarımın taşıdığı güvence

Generation token cache key'in **içinde**. Publish bump'ından sonra eski L1 girdisi *erişilemez* olur; pod otomatik L2'ye düşer. Dolayısıyla ısıtmanın gecikmesi:

- **performans** gap'idir (birkaç ms Redis okuması),
- **doğruluk** gap'i değildir (bayat component servis etmek imkânsız).

Bu, ısıtmayı "en iyi çaba" olarak tasarlamayı meşru kılar: kaçırırsan yavaşlarsın, yanlış çalışmazsın. Hiçbir ısıtma adımı istek yolunu bloke etmemeli veya hata üretmemeli.

## Konsept — üç parça

### 1. Isıtma manifesti (neyi ısıtacağız)

İki kademe, çünkü `L1SizeLimitMb=64` sınırı "hepsini ısıt"ı hedef olmaktan çıkarıyor:

- **Kademe A — kritik yol:** aktif workflow'lar + referansladıkları task / view / schema / mapping. Bir akışın ilk hop'unda mutlaka okunanlar. Readiness bunu bekler.
- **Kademe B — gerisi:** arka planda, düşük öncelik, readiness'i bekletmez.

Isıtma birimi **component değil, component + mapping'leri**: envelope L1'e yazılır *ve* içindeki `.csx` mapping'leri derlenerek script tip cache'ine girer.

### 2. Startup ısıtması + readiness kapısı

```
pod kalkar → warm-up hosted service (Kademe A) → readiness: ready → trafik
                                               └ Kademe B arka planda sürer
```

Pod trafiğe **sıcak** girer; startup gap'i sıfır. Okuma **L2'den**, DB'den değil: ilk pod DB'yi ısıtır, kalanı Redis'ten ms'lerle alır. Bedel pod başlama süresine birkaç saniye — rolling deploy'da `maxSurge` örter.

### 3. Publish fan-out (tüm pod'lar nasıl haber alır)

| yaklaşım | gap | not |
|---|---|---|
| **A. Generation polling** | interval/2 (5 sn → ~2,5 sn) | en ucuz; `generationProvider.GetAsync` zaten var |
| **B. Dapr pub/sub broadcast** | ~ms | **consumerID pod-başına benzersiz olmalı** — bugünkü sabit değer bunu engelliyor |
| C. Redis pub/sub | ~ms | gerçek fan-out, Dapr dışına çıkar |

**Seçim: A + B melez.** B anlık sinyali verir; A güvenlik ağıdır (kaçan mesaj, yeni katılan pod, restart sonrası). B tek başına yeterli değil çünkü consumerID değişikliği operasyonel bir bağımlılık; A tek başına yeterli değil çünkü gap saniyelere çıkar.

## Boğulmayı önleyen dört mekanizma

1. **L2 önce ısınır** — publish eden pod Redis'i zaten dolduruyor (`WarmResolutionsAsync`); N pod DB'ye değil Redis'e gider. DB thundering herd'ü tasarımdan yok.
2. **Delta ısıtma** — sinyal *hangi component* değişti bilgisini taşır: 1 publish = 1 component × N pod, 200 component × N pod değil. **En büyük kaldıraç.**
3. **Jitter** — sinyalden sonra `rand(0,T)` bekle; N pod, T≈2 sn → istekler yayılır, Redis'e eşzamanlı yığılma olmaz.
4. **Concurrency cap** — pod içinde eşzamanlı ısıtma sayısı sınırlı (`SemaphoreSlim`, ör. 4). Roslyn derlemesi CPU-bound; sınırsız paralellik pod'u kendi ısıtmasıyla boğar.

## Ölçüm (nasıl bileceğiz)

Mevcut telemetriden okunur, yeni sinyal gerekmez:

- `vnext.script.compile.count / miss.count / total_ms` — ısıtma sonrası ilk kullanıcı akışında **miss = 0** beklenir.
- `cache.l1.hit` — ısıtma sonrası ilk okumada `true`.
- GetState çağrı hacmi — L1 dolu olduğunda düşer (L1 kapalı/açık karşılaştırması: 330 vs 166 çağrı).
- Publish → tüm pod'larda miss=0 arası süre = **gerçekleşen fan-out gap'i**.

## Doğrulama stratejisi — iki aşama

Ölçülecek davranışlar: (i) startup ısıtması sonrası ilk akışta miss=0, (ii) publish sonrası tüm pod'larda yeni sürümün *hemen* geçerli olması, (iii) fan-out gap'inin süresi, (iv) N pod'un aynı anda ısınmasının Redis/CPU'yu boğmaması.

### Aşama 1 — 3 süreçli lokal koşum (k8s'siz)

L1 ve script tip cache **süreç-içi** olduğundan, 3 ayrı süreç 3 pod'u sadık biçimde temsil eder. Paylaşılan Redis + Postgres, farklı portlar. Avantajı: **branch'ten taze derlenen kod** koşar (yayınlanmış image değil — bkz. `CLAUDE.local.md` image politikası). Isıtma, fan-out, sürüm tutarlılığı ve gap'in tamamı burada ölçülebilir.

### Aşama 2 — k8s / helm, 3 replika

Yalnız Aşama 1'in kapsayamadığı iki şey için: **Dapr pub/sub broadcast semantiği** (per-pod `consumerID` gerçekten fan-out veriyor mu) ve **readiness gating + rolling deploy** davranışı. `vnext-helm-charts/charts/vnext/values.yaml` üzerinden `replicaCount: 3` + `extraEnvConfig` ile ısıtma ayarları; readiness probe ısıtma bitene dek başarısız dönmeli.

## Açık kararlar

1. **consumerID pod-başına benzersiz** yapılacak mı? (Dapr broadcast'in ön koşulu; operasyonel etkisi var — inbox/outbox worker'larının mevcut consumer-group davranışı korunmalı.)
2. Kademe A'nın kapsamı: "aktif tüm workflow'lar" mı, yoksa son N gün trafiği görmüş **hot set** mi?
3. Readiness ısıtmayı bekleyecek mi (deploy süresi ↑, gap = 0) yoksa beklemeyecek mi (deploy hızlı, ilk akış kısmen soğuk)?
4. Kademe B varsayılan açık mı?
