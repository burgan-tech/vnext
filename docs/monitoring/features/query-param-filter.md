# Monitor API — Query Param Filter Rehberi

Bu doküman, Monitor API'nin `GET {domain}/components` endpoint'inde query param filtrelerinin nasıl çalıştığını açıklar. Yeni filtre eklerken veya mevcut davranışı anlamak için referans alınır.

---

## Parametre Davranış Tablosu

| Parametre | Davranış | Dönen format |
|---|---|---|
| `?key=abc` | **Lookup mode** — exact match, tek kayıt arama | Tek item (pagination yok, `MonitorComponentDetailResponse`) |
| `?key[eq]=abc` | **Filter mode** — exact match | `MonitorPagedResponse`, 0–1 item |
| `?key[contains]=order` | **Filter mode** — kısmi eşleşme | `MonitorPagedResponse`, 0–N item |
| `?name[eq]=...` | Filter mode — exact match | `MonitorPagedResponse` |
| `?name[contains]=...` | Filter mode — kısmi eşleşme | `MonitorPagedResponse` |
| `?flowVersion[eq]=...` | Filter mode — exact match | `MonitorPagedResponse` |
| `?flowVersion[contains]=...` | Filter mode — kısmi eşleşme | `MonitorPagedResponse` |
| `?version[eq]=...` | Filter mode — exact match | `MonitorPagedResponse` |
| `?version[contains]=...` | Filter mode — kısmi eşleşme | `MonitorPagedResponse` |
| `?tags[contains]=...` | Filter mode — liste-içerir | `MonitorPagedResponse` |
| `definitionType`, `renderer`, `display`, `scope` | Plain, exact match — operatör yok | `MonitorPagedResponse` |
| `createdAt[gte/lte]`, `modifiedAt[gte/lte]` | Range filter — değişmez | `MonitorPagedResponse` |

---

## Lookup Mode vs Filter Mode

### Lookup Mode — `?key=abc`

- `key` plain (operatörsüz) verildiğinde tek bileşen aranır.
- Diğer filtre parametreleri (`page`, `pageSize`, `tags[contains]` vb.) **görmezden gelinir** — yalnızca `type`, `key`, `version` geçerlidir.
- Bileşen bulunursa `MonitorComponentDetailResponse` (tek obje, pagination wrapper yok) döner.
- Bulunamazsa `404` döner.

### Filter Mode — `?key[eq]=abc` veya `?key[contains]=order`

- Bracket operatörlü `key` filter mode'dur; normal liste pipeline'ından geçer.
- `MonitorPagedResponse<MonitorComponentSummaryItem>` döner (pagination metadata dahil).
- `key[eq]` unique bir key için 0 veya 1 item döndürür; `key[contains]` 0–N item döndürür.

---

## Operator Kuralları

### `[eq]` — Tam Eşleşme

```
?flowVersion[eq]=1.0.0   → FlowVersion tam olarak "1.0.0" olan bileşenler
?version[eq]=1.0.0       → Version tam olarak "1.0.0" olan bileşenler
?key[eq]=my-flow         → Key tam olarak "my-flow" olan bileşenler
?name[eq]=Order Scripts  → Name tam olarak "Order Scripts" olan bileşenler
```

Tüm eşleştirmeler `OrdinalIgnoreCase` — büyük/küçük harf duyarsız.

### `[contains]` — Kısmi Eşleşme

```
?flowVersion[contains]=1.0   → "1.0.0", "1.0.5" gibi 1.0 içerenleri bulur
?version[contains]=1.0        → Version "1.0" içerenleri bulur
?key[contains]=order          → Key içinde "order" geçenleri bulur
?name[contains]=order         → Name içinde "order" geçenleri bulur
?tags[contains]=production    → Tags listesinde "production" olan bileşenler
```

### Plain — Operatörsüz (sadece exact match)

```
?definitionType=F      → Type "F" olan bileşenler
?renderer=default      → Renderer "default" olan view bileşenleri
?display=form          → Display "form" olan view bileşenleri
?scope=global          → Scope "global" olan function/extension bileşenleri
```

Bu field'lar operatör almaz — always exact match.

---

## Çakışma Kuralı

**Aynı field için hem `[eq]` hem `[contains]` verilirse `400 ValidationProblemDetails` döner.**

```
?key[eq]=order-flow&key[contains]=order       → 400
?flowVersion[eq]=1.0.0&flowVersion[contains]=1.0  → 400
?version[eq]=1.0.0&version[contains]=1.0      → 400
?name[eq]=Scripts&name[contains]=order        → 400
```

Hata formatı:
```json
{
  "errors": {
    "key": ["Cannot use both '[eq]' and '[contains]' operators for 'key'."]
  }
}
```

---

## Type-Discriminated Validasyon

Bazı filtreler yalnızca belirli `type` değerleri için geçerlidir. Desteklenmeyen bir filtre gönderildiğinde `400` döner.

| Field | Geçerli tipler |
|---|---|
| `createdAt[gte/lte]`, `modifiedAt[gte/lte]`, `tags[contains]`, `flowVersion[eq/contains]`, `key[eq/contains]`, `version[eq/contains]` | Tüm tipler (common) |
| `definitionType` | sys-flows, sys-tasks, sys-schemas, sys-views, sys-extensions |
| `renderer`, `display` | sys-views |
| `scope` | sys-functions, sys-extensions |
| `name[eq]`, `name[contains]` | sys-mappings |

Örnek geçersiz istek:
```
GET /monitor/{domain}/components?type=sys-flows&renderer=default  → 400
```

---

## `/components/definition` Özel Kuralı

`GET {domain}/components/definition` endpoint'inde `key` verildiğinde `page` veya `pageSize` parametresi kabul edilmez → `400`.

```
?type=sys-flows&key=my-flow&page=2   → 400
```

Liste modu (key yok) için normal pagination geçerlidir.

---

## Referans Implementasyon

| Dosya | Açıklama |
|---|---|
| `vnext/monitoring/BBT.Workflow.Monitor.Application/Components/Filters/MonitorComponentFilterInput.cs` | Field tanımları ve `SetFields()` canonical adlar |
| `vnext/monitoring/BBT.Workflow.Monitor.Application/Components/Filters/ComponentFilterDescriptor.cs` | Type → izinli alan registry |
| `vnext/monitoring/BBT.Workflow.Monitor.Application/Components/Filters/MonitorComponentFilter.cs` | LINQ apply zinciri |
| `vnext/monitoring/BBT.Workflow.Monitor.HttpApi.Host/Controllers/MonitorComponentController.cs` | Bracket binding, conflict check, disallowed check |
