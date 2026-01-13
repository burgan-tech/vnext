# Instance Filtreleme Kılavuzu

## Genel Bakış

vNext workflow sistemi, instance'ları sorgulamak için güçlü filtreleme yetenekleri sağlar. Hem **Instance tablo kolonları** hem de **JSON veri alanları** üzerinde legacy format veya GraphQL-stil JSON format kullanarak filtreleme yapabilirsiniz.

## Desteklenen Route'lar

### 1. Function/Data Route
```
GET /{domain}/function/{workflow}/data
```

### 2. Workflow Instances Route
```
GET /{domain}/workflows/{workflow}/instances
```

Her iki route da `filter` query parametresi ile aynı filtreleme yeteneklerini destekler.

---

## Filtre Formatları

### Legacy Format
Basit anahtar-değer formatı: `field=operator:value`

### GraphQL Format (Önerilen)
Mantıksal operatör desteği olan JSON tabanlı format: `{"field":{"operator":"value"}}`

---

## Filtrelenebilir Alanlar

### Instance Tablo Kolonları
Doğrudan veritabanı kolonları:

| Kolon | Tip | Açıklama | Desteklenen Operatörler |
|-------|-----|----------|-------------------------|
| `key` | string | Instance anahtarı | eq, ne, like, startswith, endswith, in, nin |
| `flow` | string | Workflow adı | eq, ne, like, startswith, endswith, in, nin |
| `status` | string | Instance durumu | eq, ne, in, nin |
| `currentState` (veya `state`) | string | Mevcut state | eq, ne, like, startswith, endswith, in, nin |
| `createdAt` | DateTime | Oluşturulma zamanı | eq, ne, gt, ge, lt, le, between |
| `modifiedAt` | DateTime | Değiştirilme zamanı | eq, ne, gt, ge, lt, le, between |
| `completedAt` | DateTime | Tamamlanma zamanı | eq, ne, gt, ge, lt, le, between |
| `isTransient` | boolean | Geçici işaret | eq, ne |

### JSON Veri Alanları (attributes)
Instance'ın JSON verisinde saklanan herhangi bir alan `attributes` prefix'i kullanılarak filtrelenebilir.

---

## Desteklenen Operatörler

| Operatör | Açıklama | Örnek Değer |
|----------|----------|-------------|
| `eq` | Eşittir | `"1111"` |
| `ne` | Eşit değildir | `"test"` |
| `gt` | Büyüktür | `"100"` |
| `ge` | Büyük veya eşittir | `"100"` |
| `lt` | Küçüktür | `"100"` |
| `le` | Küçük veya eşittir | `"100"` |
| `between` | Arasında (dahil) | `["2024-01-01", "2024-12-31"]` |
| `like` | İçerir (büyük/küçük harf duyarsız) | `"workflow"` |
| `startswith` | İle başlar | `"payment"` |
| `endswith` | İle biter | `"flow"` |
| `in` | Listede | `["Active", "Busy"]` |
| `nin` | Listede değil | `["Completed", "Faulted"]` |
| `isnull` | Null veya null değil | `true` veya `false` |

---

## Status Değerleri

`status` alanı hem kod hem de isim kabul eder:

| Status İsmi | Kod | Açıklama |
|-------------|-----|----------|
| `Active` | `A` | Instance aktif |
| `Busy` | `B` | Instance işlem yapıyor |
| `Completed` | `C` | Instance başarıyla tamamlandı |
| `Faulted` | `F` | Instance hata aldı |
| `Passive` | `P` | Instance pasif |

---

## GraphQL Format Örnekleri

### 1. Basit Instance Kolon Filtresi

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"key":{"eq":"payment-12345"}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "flow": "payment-workflow",
      "flowVersion": "1.0.0",
      "domain": "banking",
      "status": {
        "code": "A",
        "description": "Active"
      },
      "attributes": {
        "amount": 1000,
        "currency": "USD",
        "customerId": "CUST-123"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 2. Çoklu Instance Kolon Filtreleri (AND Mantığı)

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"status":{"eq":"Active"},"createdAt":{"gt":"2024-01-01"}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "flow": "payment-workflow",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 500,
        "customerId": "CUST-124"
      }
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440002",
      "key": "payment-12347",
      "flow": "payment-workflow",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 750,
        "customerId": "CUST-125"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 3. JSON Veri Alanı Filtresi (attributes)

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"attributes":{"customerId":{"eq":"CUST-123"}}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "attributes": {
        "amount": 1000,
        "currency": "USD",
        "customerId": "CUST-123",
        "transactionDate": "2024-01-15"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 4. Karışık Filtre (Instance + JSON Alanları)

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"key":{"like":"payment"},"status":{"eq":"Active"},"attributes":{"amount":{"gt":"500"}}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 1000,
        "customerId": "CUST-124"
      }
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440002",
      "key": "payment-big-12347",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 750,
        "customerId": "CUST-125"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 5. Tarih Aralığı Filtresi

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"createdAt":{"between":["2024-01-01","2024-01-31"]}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440003",
      "key": "payment-jan-001",
      "createdAt": "2024-01-15T10:30:00Z",
      "status": {"code": "C", "description": "Completed"},
      "attributes": {
        "amount": 250,
        "transactionDate": "2024-01-15"
      }
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440004",
      "key": "payment-jan-002",
      "createdAt": "2024-01-20T14:45:00Z",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 890,
        "transactionDate": "2024-01-20"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 6. Status IN Filtresi

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"status":{"in":["Active","Busy"]}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440005",
      "key": "payment-12348",
      "status": {"code": "A", "description": "Active"},
      "attributes": {"amount": 100}
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440006",
      "key": "payment-12349",
      "status": {"code": "B", "description": "Busy"},
      "attributes": {"amount": 200}
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 7. Mantıksal Operatörler - AND

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"and":[{"status":{"eq":"Active"}},{"attributes":{"amount":{"gt":"500"}}}]}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440007",
      "key": "payment-12350",
      "status": {"code": "A", "description": "Active"},
      "attributes": {
        "amount": 1500,
        "customerId": "CUST-200"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 8. Mantıksal Operatörler - OR

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"or":[{"key":{"eq":"payment-12345"}},{"key":{"eq":"payment-12346"}}]}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "attributes": {"amount": 1000}
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "attributes": {"amount": 500}
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 9. Mantıksal Operatörler - NOT

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"not":{"status":{"in":["Completed","Faulted"]}}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440008",
      "key": "payment-12351",
      "status": {"code": "A", "description": "Active"},
      "attributes": {"amount": 300}
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440009",
      "key": "payment-12352",
      "status": {"code": "B", "description": "Busy"},
      "attributes": {"amount": 450}
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

---

## Group By ve Aggregations

### 10. Group By ile Count

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"groupBy":{"field":"attributes.status","aggregations":{"count":true}}}
```

**Cevap:**
```json
{
  "groups": [
    {
      "name": "pending",
      "count": 45
    },
    {
      "name": "approved",
      "count": 123
    },
    {
      "name": "rejected",
      "count": 12
    }
  ]
}
```

### 11. Group By ile Çoklu Aggregation

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"groupBy":{"field":"attributes.currency","aggregations":{"count":true,"sum":"attributes.amount","avg":"attributes.amount","min":"attributes.amount","max":"attributes.amount"}}}
```

**Cevap:**
```json
{
  "groups": [
    {
      "name": "USD",
      "count": 150,
      "sum": 450000,
      "avg": 3000,
      "min": 10,
      "max": 50000
    },
    {
      "name": "EUR",
      "count": 75,
      "sum": 180000,
      "avg": 2400,
      "min": 50,
      "max": 25000
    },
    {
      "name": "GBP",
      "count": 30,
      "sum": 90000,
      "avg": 3000,
      "min": 100,
      "max": 15000
    }
  ]
}
```

### 12. Çoklu Alan ile Group By

**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"groupBy":{"fields":["attributes.currency","attributes.status"],"aggregations":{"count":true,"sum":"attributes.amount"}}}
```

**Cevap:**
```json
{
  "groups": [
    {
      "name": "USD_pending",
      "count": 30,
      "sum": 90000
    },
    {
      "name": "USD_approved",
      "count": 100,
      "sum": 300000
    },
    {
      "name": "EUR_pending",
      "count": 15,
      "sum": 36000
    },
    {
      "name": "EUR_approved",
      "count": 50,
      "sum": 120000
    }
  ]
}
```

---

## Function/Data Route Örnekleri

Function/Data route, instance'ları sorgulamak için basitleştirilmiş bir arayüz sağlar.

### 13. Function/Data - Basit Filtre

**İstek:**
```bash
GET /banking/function/payment-workflow/data?filter={"attributes":{"customerId":{"eq":"CUST-123"}}}
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "key": "payment-12345",
      "flow": "payment-workflow",
      "domain": "banking",
      "attributes": {
        "customerId": "CUST-123",
        "amount": 1000,
        "currency": "USD",
        "status": "approved"
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 14. Function/Data - Extension'lı Kompleks Filtre

**İstek:**
```bash
GET /banking/function/payment-workflow/data?filter={"status":{"eq":"Active"},"attributes":{"amount":{"gt":"500"}}}&extensions=customerInfo,transactionHistory
```

**Cevap:**
```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "key": "payment-12346",
      "attributes": {
        "amount": 1500,
        "customerId": "CUST-200"
      },
      "extensions": {
        "customerInfo": {
          "name": "Ahmet Yılmaz",
          "email": "ahmet.yilmaz@example.com",
          "tier": "gold"
        },
        "transactionHistory": {
          "totalTransactions": 45,
          "totalAmount": 67500,
          "averageAmount": 1500
        }
      }
    }
  ],
  "currentPage": 1,
  "pageSize": 20,
  "hasNext": false
}
```

### 15. Function/Data - Group By

**İstek:**
```bash
GET /banking/function/payment-workflow/data?filter={"groupBy":{"field":"attributes.paymentMethod","aggregations":{"count":true,"sum":"attributes.amount"}}}
```

**Cevap:**
```json
{
  "groups": [
    {
      "name": "credit_card",
      "count": 250,
      "sum": 750000
    },
    {
      "name": "bank_transfer",
      "count": 150,
      "sum": 900000
    },
    {
      "name": "paypal",
      "count": 100,
      "sum": 300000
    }
  ]
}
```

---

## En İyi Uygulamalar

### 1. Kompleks Sorgular için GraphQL Format Kullanın
GraphQL formatı daha okunabilir ve mantıksal operatörleri destekler.

**İyi:**
```json
{
  "and": [
    {"status": {"eq": "Active"}},
    {"attributes": {"amount": {"gt": "500"}}}
  ]
}
```

**Aynı Zamanda Çalışır (Legacy):**
```
status=eq:Active&attributes=amount=gt:500
```

### 2. Daha İyi Performans için Spesifik Alanlar Kullanın
Mümkün olduğunda indekslenmiş Instance kolonlarını filtreleyin.

**Daha İyi Performans:**
```json
{"key": {"eq": "payment-12345"}}
```

**Daha Yavaş:**
```json
{"attributes": {"indekslenmemişAlan": {"eq": "değer"}}}
```

### 3. Okunabilirlik için Status İsimleri Kullanın
```json
{"status": {"eq": "Active"}}
```
şuna eşittir:
```json
{"status": {"eq": "A"}}
```

### 4. Filtreleri Verimli Bir Şekilde Birleştirin
Kısıtlayıcı filtreler için AND, kapsayıcı filtreler için OR kullanın.

**Kısıtlayıcı (daha az sonuç):**
```json
{
  "and": [
    {"status": {"eq": "Active"}},
    {"createdAt": {"gt": "2024-01-01"}},
    {"attributes": {"amount": {"gt": "1000"}}}
  ]
}
```

**Kapsayıcı (daha fazla sonuç):**
```json
{
  "or": [
    {"status": {"eq": "Active"}},
    {"status": {"eq": "Busy"}}
  ]
}
```

### 5. Analitik için Group By Kullanın
İstatistiklere ihtiyacınız olduğunda, tüm kayıtları çekmek yerine group by kullanın.

**Verimli:**
```json
{
  "groupBy": {
    "field": "attributes.status",
    "aggregations": {
      "count": true,
      "sum": "attributes.amount"
    }
  }
}
```

---

## Hata Yönetimi

### Geçersiz Filtre Syntax
**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={geçersiz json}
```

**Cevap:**
```json
{
  "error": {
    "code": "invalid_filter",
    "message": "Geçersiz filtre sözdizimi. Geçerli JSON bekleniyor.",
    "details": "Pozisyon 1'de beklenmeyen karakter"
  }
}
```

### Desteklenmeyen Operatör
**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"status":{"regex":".*Active.*"}}
```

**Cevap:**
```json
{
  "error": {
    "code": "unsupported_operator",
    "message": "'regex' operatörü desteklenmiyor",
    "supportedOperators": ["eq", "ne", "gt", "ge", "lt", "le", "between", "like", "startswith", "endswith", "in", "nin"]
  }
}
```

### Geçersiz Kolon Adı
**İstek:**
```bash
GET /banking/workflows/payment-workflow/instances?filter={"gecersizKolon":{"eq":"deger"}}
```

**Cevap:**
```json
{
  "error": {
    "code": "invalid_column",
    "message": "'gecersizKolon' geçerli bir Instance kolonu değil. JSON alanları için 'attributes.alanAdi' kullanın.",
    "validColumns": ["key", "flow", "status", "currentState", "createdAt", "modifiedAt", "completedAt", "isTransient"]
  }
}
```

---

## Performans İpuçları

1. **Sayfalama Kullanın**: Daima `page` ve `pageSize` parametrelerini kullanın
   ```
   ?page=1&pageSize=20
   ```

2. **İndeksli Kolonlarda Filtreleyin**: Daha iyi performans için `key`, `status`, `createdAt` tercih edin

3. **Group By Alanlarını Sınırlayın**: Optimal performans için maksimum 2-3 alanda group by yapın

4. **Tarih Aralıklarını Akıllıca Kullanın**: Dar tarih aralıkları sorgu performansını artırır
   ```json
   {"createdAt": {"between": ["2024-01-01", "2024-01-31"]}}
   ```

5. **Büyük Veri Setlerinde Wildcard Aramadan Kaçının**: Mümkün olduğunda `like` yerine `startswith` veya `endswith` kullanın

---

## Özet

- **Instance Kolonları**: Doğrudan tablo kolonları (key, status, createdAt, vb.)
- **JSON Alanları**: JSON verileri için `attributes` prefix kullanın
- **Format**: Kompleks sorgular için GraphQL JSON formatı önerilir
- **Operatörler**: 13 operatör desteklenir (eq, ne, gt, ge, lt, le, between, like, startswith, endswith, in, nin, isnull)
- **Mantıksal Operatörler**: Kompleks koşullar için AND, OR, NOT
- **Group By**: count, sum, avg, min, max aggregation'ları ile analitik
- **Route'lar**: Hem `/function/{workflow}/data` hem de `/workflows/{workflow}/instances` filtrelemeyi destekler

Daha fazla örnek ve dokümantasyon için vNext Runtime dokümantasyonunu ziyaret edin.

