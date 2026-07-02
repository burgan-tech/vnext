---
description: Monitor API gelistirmede ogrenilen hatalar (4 baslik), zorunlu dokuman referanslari
globs:
alwaysApply: true
---

# Monitoring — Ogrenilen dersler ve referanslar

Bu dosya `vnext/monitoring` uzerindeki calismalarda tekrarlanan hatalari **ozet + kaynak** ile biriktirmek icindir. Sadece cache degil; orchestrasyon/monitor uyumu, rota, DI, okuma modeli vb. tum monitoring konusu kapsanir.

## Ilgili dokumanlar (baglama gore)

Asagidakiler **ilgili ozellige gore** zorunlu referans olarak kullanilmalidir; varsayim yapmadan once okunmali veya rule icindeki kayitlara atilmalidir.

| Konu | Kaynak |
|------|--------|
| Bilesen / tanim cache katmanlari, snapshot vs backend | `vnext/docs/features/caching-strategy.md` |
| Mimari, host rolleri | `vnext/docs/architecture/overview.md`, kok `CLAUDE.md` |
| Monitor kisitlari | `.cursor/rules/monitor-constraints.md` |

**Tanim ve bilesen sorgulari** tasarlanirken `caching-strategy.md` ile **celisen** varsayimdan kacinin (or. “liste her zaman snapshot’ta vardir”, “tek katman yeter”). Katman ozeti: lokal snapshot → dagitik cache → runtime backend (`IRuntimeService` / `ICacheBackend`).

---

## Yeni olay eklendiginde kullanilan sablon

Her monitoring ile ilgili hata, yanlis varsayim veya operasyonel tuzak icin asagidaki **dort baslik** ile kisa bir kayit eklenir (en yeniler ustte). Kaynak: ilgili **dokuman bolumu** ve/veya **kod yolu**; gerekirse birkac satir kod alintisi.

### Sablon (kopyala-yapistir)

```markdown
### [Kisa baslik] — [YYYY-MM veya surum]

**1. Ne hata yapildi?**
**2. Neye sebep oldu?**
**3. Nasil cozuldu?**
**4. Neden boyle cozuldu?** (hangi dokuman veya kalip ile uyum / trade-off)

**Referans:** `yol/dokuman.md` ve/veya `vnext/monitoring/.../Dosya.cs`
```

---

## Kayitli olaylar

### `SerializerOptions`'da `JsonStringEnumConverter` eksikliği — `/components` 500 hatası — 2026-06

**1. Ne hata yapıldı?**
`LoadLatestWithMetadataAsync` içinde kullanılan yerel `SerializerOptions`'a `JsonStringEnumConverter` eklenmedi.

**2. Neye sebep oldu?**
`Serialize<Extension>()` çağrısı `ExtensionScope` enum'ını integer olarak serialize etti (`"scope": 0`). Sonrasında `ProjectToSummary` içindeki `scopeEl.GetString()` integer JSON element üzerinde çağrılınca `InvalidOperationException` fırlattı. Bu kod `try-catch` bloğunun **dışında** olduğundan uncaught → 500.
- `sys-flows`'da sorun görünmüyordu: blob'da `scope` field'ı yok → `TryGetProperty` false → `GetString()` hiç çağrılmıyor.
- `sys-extensions` ve `sys-functions`'da `scope` field'ı var → hata tetikleniyor.

**3. Nasıl çözüldü?**
`SerializerOptions`'a `new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` eklendi. Enum'lar artık camelCase string serialize edilir. Ek olarak `ProjectToSummary`'deki `GetString()` çağrıları `ValueKind == JsonValueKind.String` guard'ı ile korundu.

**4. Neden böyle çözüldü?**
`GetString()` yalnızca `JsonValueKind.String` elementler üzerinde güvenlidir; integer/boolean/object elementlerde fırlatır. `JsonStringEnumConverter` eklemek root cause'u çözüyor, guard ise defensive safety sağlıyor.

**KURAL:** `Serialize<T>()` için kullanılan yerel `JsonSerializerOptions`'a her zaman `JsonStringEnumConverter` ekle. Yoksa entity'deki her enum field `ProjectToSummary`'de `GetString()` çağrısında 500'e neden olur. Aynı şekilde `ProjectToSummary`'deki tüm `GetString()` çağrıları `ValueKind == String` check'i ile korunmalı.

**Referans:** `vnext/monitoring/BBT.Workflow.Monitor.Application/Components/MonitorComponentQueryService.cs`

---

### `Workflow` tip adı namespace ile çakışıyor — CS0118 derleme hatası

**1. Ne hata yapıldı?**  
`BBT.Workflow.Monitor.*` altında yeni bir sınıf yazılırken parametre veya değişken tipi olarak doğrudan `Workflow` kullanıldı.

**2. Neye sebep oldu?**  
`BBT.Workflow` **hem bir namespace hem de bir tip adıdır.** Dosyanın kendi namespace'i `BBT.Workflow.Monitor.Components` (veya herhangi bir `BBT.Workflow.*` alt-namespace'i) olduğunda, derleyici `Workflow` sözcüğünü önce namespace olarak çözümler ve `CS0118: 'Workflow' is a namespace but is used like a type` hatasını verir. `using BBT.Workflow.Definitions;` eklemek sorunu **çözmez** — çakışma using'den değil namespace adından kaynaklanır.

**3. Nasıl çözüldü?**  
`using` alias ile tip adı yeniden adlandırıldı:
```csharp
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;
// Artık tüm dosyada WorkflowDefinition kullanılır
public static MonitorDependencyResponse Extract(WorkflowDefinition flow) { ... }
```

**4. Neden böyle çözüldü?**  
Alias en az invazif yöntemdir; namespace değiştirilmez, tip adı kısalır, çakışma derleme zamanında ortadan kalkar.

**KURAL:** `BBT.Workflow.Monitor.*` namespace'i altında yazılan her dosyada `BBT.Workflow.Definitions.Workflow` tipini **doğrudan `Workflow` olarak kullanma**. Her zaman `using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;` alias'ını ekle veya tam nitelikli adı (`Definitions.Workflow`) yaz.

**Referans:** `vnext/monitoring/BBT.Workflow.Monitor.Application/Components/DependencyExtractor.cs`

---

### Bilesen listesi 200 OK ama `items` bos — Monitor `GET .../components`

**1. Ne hata yapildi?**  
`type=sys-flows` (ve benzeri) ile **anahtar vermeden** liste istendiginde API **200** donuyor ancak `items` **surekli bos**; orchestrator tanimlari calistirabiliyorken monitor listeleyemiyormus izlenimi.

**2. Neye sebep oldu?**  
- (A) Servis katmaninda `key` yokken once **liste hic cache/DB’ye gitmeden** bos donuyordu (eksik implementasyon).  
- (B) `GetAllByDomainAsync` **yalnizca bellek snapshot**’ini tarar; pod soguk / snapshot bos ise liste bos kalir — bkz. `caching-strategy.md` (cok katmanli model).  
- (C) Istemci URL’i yanlis olabiliyor: dogru rota **`api/v{version}/monitor/{domain}/components`**.

**3. Nasil cozuldu?**  
- `key` yokken once `IDomainCacheContext` `GetAllByDomainAsync`; bos ise `IRuntimeService` + domain filtresi + key basina en guncel surum + `IComponentCacheStore.SetAsync` ile cache isitma.  
- Monitor DI: `IRuntimeService` kaydi (`AddMonitorApplicationModule`).  
- Postman / endpoint haritasi rotalari duzeltildi.

**4. Neden boyle cozuldu?**  
`caching-strategy.md` tekil okumada miss sonrasi backend’i tanimlar; **domain geneli liste** icin snapshot tek basina kaynak gercek degil. Monitor’un guvenilir listesi icin backend yuklemesi ve cache warm-up stratejiyle uyumludur.

**Referans:** `vnext/docs/features/caching-strategy.md`; kod: `vnext/monitoring/BBT.Workflow.Monitor.Application/Components/MonitorComponentQueryService.cs`.

---

## Ajan / gelistirici kontrol listesi (kisa)

- [ ] Ilgili ozellik icin yukaridaki **referans tablosundan** dokuman okundu mu veya bu dosyada benzer kayit var mi?
- [ ] Tanim/bilesen sorgusu: `caching-strategy.md` ile celisen varsayim var mi?
- [ ] Monitor rota/DI: `monitor-endpoint-map.md`, `monitor-constraints.md`, `CLAUDE.md` ile uyumlu mu?
- [ ] Yeni bir ders cikti mi? Bu dosyada **dort baslik** ile kayit eklendi mi?
