# vNext Monitor — GraphQL Filter Rehberi

> Instance list endpoint'i (`GET {domain}/workflows/{workflow}/instances`) **GraphQL-tarzı JSON nesnesi** filter alır. Bu altyapı zaten çalışır; faulted listesi, SLA aşımı, state-bazlı arama, JSON data içi arama gibi pek çok ihtiyaç **yeni endpoint olmadan** buradan karşılanır.
>
> İlgili `.http` örnekleri: [endpoints/vnext-monitor-filter-guide.http](../../endpoints/vnext-monitor-filter-guide.http)

---

## 1. Söz Dizimi (ÖNEMLİ)

`filter` parametresi bir **JSON nesnesidir**, eski `field op 'value'` string formu **değildir**.

```
✅ filter={"status":{"eq":"Faulted"}}
❌ filter=status eq 'Faulted'
```

Genel biçim: `{ "<alan>": { "<operatör>": <değer> } }`. JSON data alanları `attributes` altında iç içe verilir.

---

## 2. Filtrelenebilir Alanlar

### Instance kolonları (first-class)
`key`, `flow`, `currentState`, `state` (→ effectiveState), `status`, `stateType`, `stateSubType`, `createdAt`, `modifiedAt`, `completedAt`, `isTransient`

### JSON data alanları
`attributes.` öneki ile iç içe path: `attributes.customerId`, `attributes.payment.amount` …

---

## 3. Operatörler

| Operatör | Anlam |
|----------|-------|
| `eq`, `ne` | eşit / eşit değil |
| `gt`, `ge`, `lt`, `le` | büyüktür / büyük-eşit / küçüktür / küçük-eşit |
| `between` | aralık |
| `like`, `match`, `startswith`, `endswith` | metin eşleşmeleri |
| `in`, `nin` | liste içinde / değil |
| `isNull` | null kontrolü |

**Mantıksal birleşim:** `and`, `or`, `not`.

> **Kısıt:** `status` alanı yalnızca `eq, ne, in, nin` destekler.

---

## 4. Örnek Sorgular

### Faulted instance'lar (en son değişene göre)
```
filter={"status":{"eq":"Faulted"}}&sort={"field":"modifiedAt","direction":"desc"}
```

### JSON data içinde arama
```
filter={"attributes":{"category":{"eq":"finance"}}}
```

### Tarih aralığı + durum (and)
```
filter={"and":[{"status":{"eq":"Active"}},{"createdAt":{"gt":"2026-06-08T00:00:00Z"}}]}
```

### İç içe JSON path
```
filter={"attributes":{"payment":{"amount":{"ge":1000}}}}
```

### SLA aşımı — uzun süredir Active kalan instance'lar
```
filter={"and":[{"status":{"eq":"Active"}},{"createdAt":{"lt":"2026-06-08T00:00:00Z"}}]}
```

---

## 5. Gruplama ve Toplama (groupBy / aggregations)

```
groupBy={"field":"attributes.category"}&aggregations={"count":true}
```

> **ÖNEMLİ KISIT:** `groupBy` yalnızca **JSON data path** (`attributes.*`) üzerinde çalışır; **instance kolonları (`status`, `currentState`) üzerinde çalışmaz**.
>
> Bu yüzden status bazlı sayaçlar ve state bazlı dağılım için ayrı endpoint'ler vardır:
> - Status sayaçları: `GET {domain}/workflows/{workflow}/stats/instances`
> - State dağılımı: `GET {domain}/workflows/{workflow}/stats/states`

---

## 6. Sayfalama ve Sıralama

- Sayfalama: `?page=1&pageSize=10` (max pageSize=100).
- Sıralama: `sort={"field":"createdAt","direction":"desc"}` — instance kolonu veya JSON data path üzerinde.
