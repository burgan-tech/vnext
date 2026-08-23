# Katman 1 — Script Compiler Hit-Yolu Optimizasyonları (Design Spec)

**Tarih:** 2026-08-23 · **Durum:** Onaylandı (brainstorming oturumu) · **Branch:** `feature/script-perf-katman0` (tüm kapsam tek branch — kullanıcı kararı)
**Baz:** `ai-docs/script-perf-analysis-2026-08-23.md` (A1-A4, A7) · Mikro baseline: `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md` · Makro baseline: vnext-example `core/Workflows/script-perf-lab/README.md` § Sonuçlar

## Amaç

Compile-cache **hit** yolunun çağrı başına sabit bedelini (~12.5 µs / ~98.8 KB alloc × ~33 çağrı/instance) ve helper'lı yolun çözümleme ön-işini, **davranışı hiç değiştirmeden** ortadan kaldırmak. Cold compile, eviction, ALC yaşam döngüsü ve #888 yarış-fix yapısı bu katmanın DIŞINDADIR ve dokunulmaz.

## Alınan kararlar

| Karar | Seçim |
|---|---|
| Executor çift-lookup çözümü | **Yaklaşım A — minimal memo**: `TaskExecutorBase.GetOrCompileMappingAsync<T>` helper'ı, `TaskExecutorContext` ömrü scope'u. Tam şablon-metot refactor'ı (B) bilinçli ertelendi — ayrı refactor işi. |
| Memo içeriği | **Type-level memo + her fazda taze instance** (factory delegate ile). Instance paylaşımı reddedildi: kullanıcı .csx'i field tutuyorsa input'ta yazılan state output'ta görünür olurdu — davranış değişikliği, no-breaking-change politikasına aykırı. |
| Bileşen-çözümleme memo ilkesi (kullanıcı düzeltmesi) | **Authored referans tuple'ı ASLA cache kimliği değildir.** Versiyon referansları floating çözülür (1.0 → en yüksek 1.0.x; 1.0.0 dahi en yüksek eşleşmeye gider); yanlış memo, kullanıcının hotfix'ini "prod'da çözülmedi" algısına çevirir. Her bileşen-memo'su ya component-cache'ten dönen OBJE/İÇERİK kimliğine bağlanır ya da generation-token ile bekçilenir. |

---

## 1. ScriptIdentity — hit yolunda anahtar üretimini öldürmek (A1+A2)

### 1.1 `ScriptCode` memo alanları (Domain)

`ScriptCode` immutable ve component cache üzerinden paylaşımlı. İki lazy-memo eklenir (emsal: `JsonData.NormalizedJson`):

- `DecodedCode` — bugün her erişimde Base64 decode + UTF8 string; bir kez çözülüp saklanır.
- `ContentHash` — çözülmüş kaynak üzerinden SHA256 hex; bir kez.

Benign-race yazım yeterli (iki thread aynı değeri üretir); `Lazy<T>` veya null-check field, dosyadaki mevcut desene göre plan seçer. Değer eşitliği (`GetAtomicValues`) etkilenmez — memo alanları atomic values'a GİRMEZ.

### 1.2 Evaluator — önceden hesaplanmış anahtar yolu

- `CSharpEvaluator.GenerateCacheKey` mantığı **taşınarak** (kopyalanmadan) dışa açılır: `public string ComputeCacheKey(code, targetType, extraReferences, usingDirectives, sandboxGrant, loadContext)` (scope'u loadContext'ten kendisi türetir — IEvaluator'a eklenir).
- `CompileToInstanceAsync<T>`'ye opsiyonel `string? precomputedCacheKey = null` parametresi: verildiğinde anahtar üretimi (StringBuilder + tam-kaynak SHA256 + OrderBy'lar) tamamen atlanır; verilmediğinde bugünkü yol aynen. Ham-string API sözleşmesi değişmez.
- Fast-path/`GetOrAdd`/`Lazy`/eviction yapısı (#888) BYTE-BYTE korunur; yalnız anahtarın nereden geldiği değişir.

### 1.3 Engine — kimlik memo'su

- `ScriptEngine`'de süreç-geneli statik memo: `ConditionalWeakTable<ScriptCode, ConcurrentDictionary<CacheKeyDiscriminator, string>>`; discriminator = (targetType, loadContext-scope kimliği, grant kimliği). Final evaluator anahtarı mapping başına bir kez `ComputeCacheKey` ile hesaplanır, sonra memo'dan okunur.
- **Yaşam döngüsü güvencesi:** CWT anahtarı `ScriptCode` OBJESİ — component cache yeni publish'te yeni obje servis ettiğinden memo doğal düşer; authored tuple hiçbir yerde anahtar değildir. (Plan aşamasında L1'in "aynı içerik için aynı objeyi mi yoksa yeni objeyi mi verdiği" teyit edilir; her iki durumda da yanlışlık değil en fazla isabet kaybı olur.)
- Ham string ile çağrılan yol (ScriptCode objesi yok) memo'suz, bugünkü gibi devam eder.

### 1.4 Doğruluk sigortası

Yanlış anahtar = yanlış script çalıştırma. Sigortalar: (a) anahtar hesabı TEK kaynakta (evaluator'ın kendi metodu; engine mantık kopyalamaz), (b) property-tarzı eşitlik testi — precomputed yol ile hesaplamalı yol aynı girdilerde aynı derlenmiş Type'ı döndürür, (c) makro labda `miss=+0` regresyon kontrolü, (d) helper-hotfix el doğrulaması (§5).

---

## 2. Sabit profiller (A3) + generation-token'lı helper-set memo'su (A7)

### 2.1 A3 — profil memo'ları

- `MergeDefaultGrant`'ın eager `Concat+Distinct+ToArray`'i grant listesi başına memoize: `ConditionalWeakTable<IReadOnlyList<string>, string[]>`. Güvenli çünkü `AllowedAssemblies` listesi Workflow tanım objesinin parçasıdır ve yeni publish yeni obje getirir (obje-kimlik ilkesi).
- Referans/using birleştirmeleri: helper'sız yolda sonuç sabittir (`DefaultReferences`/`DefaultUsings`) — bir kez hesaplanıp sabit dizi olarak geçilir; helper'lı yolda helper-set çözümü başına bir kez hesaplanıp set ile birlikte saklanır.

### 2.2 A7 — helper-set memo'su (generation-token bekçili)

Bugün helper'lı HER çağrıda: N × `GetMappingAsync` + tüm helper kaynakları üzerinden `HashOf` SHA256 (registry fast-path'inden önce). Değişiklik:

- Memo: anahtar = authored ref listesinden türetilen kimlik string'i; değer = `{ çözülmüş somut sürümler, CompiledHelpers, loadContext, namespaces, registry hash, generationToken }`.
- **Her kullanımda**: dictionary hit + component cache'in güncel generation-token'ı ile ucuz karşılaştırma (#898 L1 mekanizması; tam API plan aşamasında koddan teyit edilir). Token değiştiyse (herhangi bir publish) memo düşer, tam yeniden çözümleme koşar — yeni içerik yeni ALC/scope üretir, bağlı mapping'ler yeniden derlenir. **Publish görünürlüğü bugünkü davranışla birebir**; değişiklik-yokken maliyet sıfıra iner.
- Token global tek sayaçsa memo her publish'te düşer — doğru ama daha az isabetli; kabul edilir, iyileştirme notu düşülür.
- REF'li mapping çözümlemesi (`ResolveReferencedCodeAsync`) için YENİ memo YOK — her çağrıda L1'den geçmeye devam eder (L1 zaten token-bekçili); floating çözümleme orada da bozulmaz.

---

## 3. Per-task memo + factory delegate

### 3.1 `TaskExecutorBase.GetOrCompileMappingAsync<T>`

- Korumalı helper; `context.OnExecuteTask.Mapping`'i derler/çözer, **compile-lookup sonucunu** (Type düzeyinde) `TaskExecutorContext` ömrüne bağlı küçük bir sözlükte `(mapping kimliği, typeof(T))` anahtarıyla saklar; her fazda factory ile **taze instance** üretir (Services enjekte).
- `PrepareInputAsync` + `ProcessOutputAsync`/`InvokeAsync` aynı yürütmede tek engine çağrısını paylaşır → instance başına compile-API çağrısı ~33 → ~23.
- Tüm executor'lardaki `scriptEngine.CompileToInstanceAsync<T>(...)` çağrıları helper'a yönlendirilir (mekanik, derleyici-güdümlü; Katman 0 ctor süpürmesi emsali). Metrik hunileri (Katman 0) yerinde kalır — bu katman metrik yerleşimine dokunmaz.
- Engine dönüşü bugün instance verdiği için "Type-level memo"nun mekanik şekli planda netleşir (ör. helper ilk çağrıda instance'ı alır ve tipini + factory'yi memo'lar; sonraki fazda factory'den üretir). Şart: **her faz taze instance** ve engine'e tek çağrı.

### 3.2 A4 — factory delegate

- `CreateAndInjectServices<T>`: `Activator.CreateInstance` yerine `ConcurrentDictionary<Type, Func<object>>` altında `Expression.Lambda` ile derlenmiş parametresiz-ctor delegate'i; `ScriptBase.Services` enjeksiyonu tip başına bir kez hazırlanan setter delegate'iyle. Tip başına tek kurulum, sonrası allocation'sız üretim. Evaluator cache yapısına dokunulmaz.

---

## 4. Kapsam dışı

- Cold compile / SandboxedReferenceSet cache'i (A6), warmup/IL-cache → Katman 3 adayı.
- Tam şablon-metot executor refactor'ı (Yaklaşım B) ve ScriptTask `task-output` metrik boşluğu → ayrı iş; bu katman kapatmaz.
- Serialization kalemleri (B1-B10) → Katman 2.
- Metrik yüzeyinde HİÇBİR değişiklik.

## 5. Doğrulama

- **Unit:** ScriptCode memo determinizmi; **anahtar eşitliği property testi** (precomputed ⟺ hesaplamalı yol aynı Type); helper-set memo — token sabitken ikinci çağrıda `GetMappingAsync` çağrılmaz (mock sayaç), token bump'ında tam yeniden çözümleme; per-task memo — aynı context'te tek engine çağrısı, farklı context'te yeni, her fazda farklı instance; factory — Services enjekteli taze instance'lar.
- **Mikro:** mevcut `CompileHitPath` (ham-string) AYNEN kalır ve değişmediğini kanıtlar; yeni `CompileHitPathIdentity` suite'i precomputed-key yolunu ölçer. Beklenti (rapor, hard hedef değil): 12.5 µs / 98.8 KB → ~1-2 µs / <2 KB.
- **Makro:** Faz A lab aynı parametrelerle; beklenen imza: hit/instance 33 → ~23, **`miss=+0` KORUNUR**, p50/p95 ve alloc düşer. **Helper-hotfix el doğrulaması:** çalışan process'te helper yeni versiyonla publish edilir → bir sonraki instance yeni stamp üretmeli (floating çözümleme + token bekçisinin kanıtı).
- **Regresyon:** Domain+Application+Infrastructure isim-diff (master worktree yöntemi); vnext-example chain-busy + fan-out suite'lerinin lokal koşusu.

## 6. Başarı kriterleri

- [ ] Makro imza gerçekleşti (hit 33→~23, miss=+0, latency/alloc düşüşü kayıtlı — önce/sonra tablosu README + baselines'a işlendi).
- [ ] Mikro: identity yolunda alloc/süre düşüşü kayıtlı; ham-string yolu değişmedi.
- [ ] Helper-hotfix doğrulaması geçti (publish görünürlüğü birebir).
- [ ] Hiçbir dış sözleşme değişmedi (API, metrik, davranış); tüm testler isim-diff temiz.

## 7. Riskler

- **Yanlış cache anahtarı = yanlış script** — en kritik; mitigasyon §1.4.
- CWT yaşam döngüsü varsayımı (yeni publish → yeni obje) — plan aşamasında L1 kodundan teyit; aksi durumda memo generation-token bekçisine geçirilir (§2.2 deseni).
- #888 yarış-fix invariantları — evaluator'ın cache yapısına dokunulmaz; yalnız anahtar girdisi değişir; mevcut `CSharpEvaluatorConcurrencyTests` + `SandboxedScriptingTests` yeşil kalmalı.
- Executor süpürmesi (~15 dosya) mekanik ama geniş — Katman 0 emsalindeki derleyici-güdümlü yöntem + isim-diff regresyonu.
