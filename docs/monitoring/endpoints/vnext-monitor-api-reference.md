# vNext Monitor API — Endpoint Reference

Base URL: `http://localhost:4203`  
Route prefix: `api/v1.0/monitor`  
Tüm instance endpoint'leri `{domain}/workflows/{workflow}/` scope'unda çalışır.  
Tüm response gövdeleri JSON, camelCase, null alanlar atlanır.

---

## Ortak Kavramlar

### Path Parametreleri

| Parametre  | Açıklama                                             |
|------------|------------------------------------------------------|
| `{domain}` | Tenant/domain anahtarı (ör. `core`)                 |
| `{workflow}` | Workflow (flow) anahtarı (ör. `lifecycle-transitions-test-workflow`) |
| `{instance}` | Instance GUID'i veya business key'i               |

### Instance Status Değerleri

| Değer      | Açıklama                        |
|------------|----------------------------------|
| `Active`   | Bekliyor, geçiş bekleniyor      |
| `Busy`     | Pipeline çalışıyor              |
| `Completed` | Tamamlandı                     |
| `Faulted`  | Hata ile sonlandı               |
| `Passive`  | Devre dışı bırakıldı            |

### State Type Değerleri

| Değer         | Kod | Açıklama                |
|---------------|-----|-------------------------|
| `Initial`     | 1   | Başlangıç state         |
| `Intermediate`| 2   | Ara state               |
| `Finish`      | 3   | Bitiş state             |
| `SubFlow`     | 4   | Alt akış state          |
| `Wizard`      | 5   | Sihirbaz state          |

### TriggerType Değerleri

| Değer       | Kod | Açıklama          |
|-------------|-----|-------------------|
| `Manual`    | 0   | Kullanıcı tetikli |
| `Automatic` | 1   | Kural ile otomatik|
| `Scheduled` | 2   | Zamanlı           |
| `Event`     | 3   | Olay tetikli      |

### Component Type Değerleri

| `type` param     | İçerik              |
|------------------|---------------------|
| `sys-flows`      | Workflow tanımları  |
| `sys-tasks`      | Task tanımları      |
| `sys-schemas`    | Schema tanımları    |
| `sys-extensions` | Extension tanımları |
| `sys-functions`  | Function tanımları  |
| `sys-views`      | View tanımları      |

---

## 1. Instance Endpoint'leri

### 1.1 Instance Listesi

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances
```

Sayfalı, filtrelenebilir instance listesi döner.

#### Query Parametreleri

| Parametre      | Tip      | Varsayılan | Açıklama                                          |
|----------------|----------|------------|---------------------------------------------------|
| `page`         | int      | 1          | Sayfa numarası (1–1000)                           |
| `pageSize`     | int      | 10         | Sayfa boyutu (1–100)                              |
| `filter`       | string   | —          | GraphQL-tarzı JSON filtre nesnesi                 |
| `sort`         | string   | —          | `{"field":"modifiedAt","direction":"desc"}`       |
| `groupBy`      | string   | —          | `{"field":"attributes.kategori"}` — JSON path     |
| `aggregations` | string   | —          | `{"count":true}` — groupBy ile birlikte kullanılır |

#### Filter Operatörleri

`eq`, `ne`, `gt`, `ge`, `lt`, `le`, `between`, `like`, `startswith`, `endswith`, `in`, `nin`, `isnull`  
Mantıksal: `and`, `or`, `not`

Filtrelenebilir alanlar: `status`, `currentState`, `key`, `createdAt`, `modifiedAt`, `attributes.*`

**Filter Örnekleri:**
```json
// Durum filtresi
{"status":{"eq":"Faulted"}}

// Tarih aralığı + durum
{"and":[{"status":{"eq":"Active"}},{"createdAt":{"gt":"2026-06-01T00:00:00Z"}}]}

// JSON data içinde arama
{"attributes":{"category":{"eq":"finance"}}}

// İç içe JSON path
{"attributes":{"payment":{"amount":{"ge":1000}}}}
```

#### Response — 200 OK

```json
{
  "items": [
    {
      "id": "8e298c72-457c-4cd2-b3f2-e94fd5bf5a41",
      "key": "INST-0042",
      "flow": "lifecycle-transitions-test-workflow",
      "flowVersion": "1.0.0",
      "domain": "core",
      "tags": ["vip"],
      "metadata": {
        "currentState": "in-review",
        "effectiveState": "in-review",
        "status": "Active",
        "effectiveStateType": "Intermediate",
        "effectiveStateSubType": "Human",
        "completedAt": null,
        "duration": null,
        "createdAt": "2026-06-10T08:00:00Z",
        "modifiedAt": "2026-06-10T09:30:00Z",
        "createdBy": "user-001",
        "createdByBehalfOf": null,
        "modifiedBy": "user-002",
        "modifiedByBehalfOf": null
      },
      "activeCorrelations": []
    }
  ],
  "links": {
    "self": "...",
    "next": "...",
    "prev": null
  }
}
```

`groupBy` + `aggregations` kullanıldığında `items` yerine gruplama sonuçları gelir.

---

### 1.2 Instance Detayı

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}
```

Tek bir instance'ın meta verisi ve aktif correlation'larını döner.

#### Response — 200 OK

```json
{
  "id": "8e298c72-457c-4cd2-b3f2-e94fd5bf5a41",
  "key": "INST-0042",
  "flow": "lifecycle-transitions-test-workflow",
  "flowVersion": "1.0.0",
  "domain": "core",
  "tags": ["vip"],
  "metadata": {
    "currentState": "in-review",
    "effectiveState": "in-review",
    "status": "Active",
    "effectiveStateType": "Intermediate",
    "effectiveStateSubType": "Human",
    "completedAt": null,
    "duration": null,
    "createdAt": "2026-06-10T08:00:00Z",
    "modifiedAt": "2026-06-10T09:30:00Z",
    "createdBy": "user-001",
    "createdByBehalfOf": null,
    "modifiedBy": null,
    "modifiedByBehalfOf": null
  },
  "activeCorrelations": [
    {
      "id": "cc001",
      "parentState": "approval",
      "subFlowInstanceId": "sub-inst-001",
      "subFlowDomain": "core",
      "subFlowName": "document-check",
      "subFlowVersion": "1.0.0",
      "subFlowType": "S",
      "subFlowCurrentState": "reviewing"
    }
  ]
}
```

`subFlowType`: `S` = SubFlow, `P` = SubProcess

**HTTP 404** — Instance bulunamadığında `ProblemDetails` döner.

---

### 1.3 Instance Data

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/data
```

Instance'ın en güncel verisi ve tüm versiyon geçmişini döner.

#### Query Parametreleri

| Parametre | Tip    | Açıklama                                                                 |
|-----------|--------|--------------------------------------------------------------------------|
| `version` | string | Belirtilirse yalnızca o versiyonun verisi döner (`data` alanı dolu, `versionHistory` null) |

#### Response — 200 OK (tüm geçmiş, `version` yokken)

```json
{
  "data": null,
  "latestData": {
    "customerId": "cust-123",
    "amount": 5000,
    "category": "finance"
  },
  "versionHistory": [
    {
      "version": "1.0.0",
      "enteredAt": "2026-06-10T08:00:00Z",
      "data": { "customerId": "cust-123", "amount": 0 }
    },
    {
      "version": "1.0.1",
      "enteredAt": "2026-06-10T09:00:00Z",
      "data": { "customerId": "cust-123", "amount": 5000, "category": "finance" }
    }
  ]
}
```

#### Response — 200 OK (`?version=1.0.0` ile)

```json
{
  "data": { "customerId": "cust-123", "amount": 0 },
  "latestData": null,
  "versionHistory": null
}
```

**HTTP 404** — Instance veya istenen versiyon bulunamadığında.

---

### 1.4 Instance View

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/view
```

Instance'ın mevcut state'ine bağlı view tanımını döner.

#### Query Parametreleri

| Parametre       | Tip    | Açıklama                                          |
|-----------------|--------|---------------------------------------------------|
| `transitionKey` | string | Belirtilirse o transition'ın view'u döner         |
| `role`          | string | Rol bazlı view seçimi için                        |
| `version`       | string | Belirli workflow versiyonu                        |

#### Response — 200 OK

```json
{
  "viewKey": "review-form",
  "viewType": "json",
  "content": {
    "fields": ["customerId", "amount", "category"]
  },
  "display": "full-page",
  "labels": [
    { "language": "tr", "label": "İnceleme Formu" },
    { "language": "en", "label": "Review Form" }
  ]
}
```

`viewType` değerleri: `json`, `html`, `markdown`, `deepLink`, `http`, `urn`

**HTTP 204** — Mevcut state veya transition için view tanımlanmamışsa.  
**HTTP 404** — Instance, workflow veya transition bulunamadığında.

---

### 1.5 Instance Timeline (Birleşik)

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/timeline
```

Instance'ın geçiş geçmişini döner. Parametreye göre farklı modlarda çalışır:

| Parametreler           | Mod                    | Dönen Yapı                       |
|------------------------|------------------------|----------------------------------|
| (hiçbiri)              | Tam akış               | `transitions[]` — tüm geçişler  |
| `?includeTasks=true`   | Tam akış + task'lar    | `transitions[].tasks[]` dolu     |
| `?transitionId={guid}` | Tek geçiş              | `transitions[]` — tek eleman    |
| `?transitionId=...&includeTasks=true` | Tek geçiş + task'lar | `transitions[0].tasks[]` dolu |
| `?taskId={guid}`       | Tek task (öncelikli)   | `task` alanı dolu, `transitions` boş |

#### Query Parametreleri

| Parametre      | Tip  | Açıklama                                             |
|----------------|------|------------------------------------------------------|
| `transitionId` | Guid | Belirtilirse yalnızca o geçiş döner                 |
| `taskId`       | Guid | Belirtilirse yalnızca o task döner (`transitionId`'ye göre öncelikli) |
| `includeTasks` | bool | Her geçişe ait task'ları gömer (task modunda yoksayılır) |

#### Response — 200 OK (tam akış)

```json
{
  "transitions": [
    {
      "id": "b739cc23-45d2-4371-af6a-3c29b83eac13",
      "transitionId": "submit",
      "fromState": "draft",
      "toState": "in-review",
      "startedAt": "2026-06-10T08:05:00Z",
      "finishedAt": "2026-06-10T08:05:02Z",
      "durationSeconds": 2.0,
      "triggerType": "Manual",
      "createdBy": "user-001",
      "createdByBehalfOf": null,
      "tasks": null
    }
  ],
  "task": null
}
```

#### Response — 200 OK (`?taskId=...`)

```json
{
  "transitions": [],
  "task": {
    "id": "660e8400-e29b-41d4-a716-446655440111",
    "transitionId": "b739cc23-45d2-4371-af6a-3c29b83eac13",
    "taskId": "send-notification",
    "status": "Completed",
    "businessStatus": "Success",
    "startedAt": "2026-06-10T08:05:01Z",
    "finishedAt": "2026-06-10T08:05:01.5Z",
    "durationSeconds": 0.5,
    "request": { "to": "user@example.com" },
    "response": { "messageId": "msg-001" }
  }
}
```

**HTTP 400** — `transitionId` veya `taskId` boş string olarak verilirse.  
**HTTP 404** — Instance, transition veya task bulunamadığında.

---

### 1.6 Instance State

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/state
```

Instance'ın anlık durumu ve mevcut state'ten yapılabilecek geçişleri döner.

#### Response — 200 OK

```json
{
  "currentState": "in-review",
  "stateType": "Intermediate",
  "stateSubType": "Human",
  "status": "Active",
  "effectiveState": "in-review",
  "availableTransitions": [
    {
      "key": "approve",
      "target": "approved",
      "triggerType": "Manual",
      "roles": ["morph-idm.approver"]
    },
    {
      "key": "reject",
      "target": "rejected",
      "triggerType": "Manual",
      "roles": ["morph-idm.approver"]
    }
  ],
  "activeCorrelations": []
}
```

`availableTransitions` tanım bazlı döner; kural değerlendirmesi yapılmaz.

**HTTP 404** — Instance bulunamadığında.

---

### 1.7 Instance Faults

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/faults
```

Faulted bir instance'ın kök hata kaynağını döner: tamamlanmayan geçiş ve başarısız task'lar.

#### Response — 200 OK

```json
{
  "lastKnownState": "processing",
  "effectiveState": "processing",
  "status": "Faulted",
  "faultedTransition": {
    "id": "b739cc23-45d2-4371-af6a-3c29b83eac13",
    "transitionId": "process-payment",
    "fromState": "approved",
    "toState": null,
    "startedAt": "2026-06-10T10:00:00Z",
    "triggerType": "Automatic"
  },
  "faultedTasks": [
    {
      "id": "660e8400-e29b-41d4-a716-446655440111",
      "transitionId": "b739cc23-45d2-4371-af6a-3c29b83eac13",
      "taskId": "call-payment-api",
      "status": "Faulted",
      "businessStatus": "Failed",
      "startedAt": "2026-06-10T10:00:01Z",
      "finishedAt": "2026-06-10T10:00:03Z",
      "durationSeconds": 2.0,
      "request": { "amount": 5000 },
      "response": { "error": "timeout" }
    }
  ]
}
```

**HTTP 404** — Instance bulunamadığında.

---

### 1.8 Instance Data Diff

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/data/diff?from={version}&to={version}
```

İki data versiyonu arasındaki alan düzeyinde farkı döner.

#### Query Parametreleri (Zorunlu)

| Parametre | Tip    | Açıklama               |
|-----------|--------|------------------------|
| `from`    | string | Baz versiyon (ör. `1.0.0`) |
| `to`      | string | Hedef versiyon (ör. `1.0.1`) |

#### Response — 200 OK

```json
{
  "fromVersion": "1.0.0",
  "toVersion": "1.0.1",
  "added": [
    { "path": "category", "value": "finance" }
  ],
  "removed": [],
  "changed": [
    { "path": "amount", "oldValue": "0", "newValue": "5000" }
  ],
  "unchangedCount": 1
}
```

`path` dot-notation formatındadır (ör. `payment.amount`). String değerler tırnaksız, diğerleri ham JSON metni olarak gelir.

**HTTP 404** — Instance veya istenen versiyon bulunamadığında.

---

### 1.9 Instance Hierarchy

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/hierarchy
```

Instance'ın alt-akış/alt-süreç ağacını özyinelemeli olarak döner.

#### Response — 200 OK

```json
{
  "instanceId": "8e298c72-457c-4cd2-b3f2-e94fd5bf5a41",
  "key": "INST-0042",
  "flow": "lifecycle-transitions-test-workflow",
  "domain": "core",
  "flowVersion": "1.0.0",
  "currentState": "approval",
  "status": "Active",
  "subFlowType": null,
  "parentState": null,
  "isCompleted": false,
  "completedAt": null,
  "children": [
    {
      "instanceId": "sub-inst-001",
      "key": "SUB-001",
      "flow": "document-check",
      "domain": "core",
      "flowVersion": "1.0.0",
      "currentState": "reviewing",
      "status": "Active",
      "subFlowType": "S",
      "parentState": "approval",
      "isCompleted": false,
      "completedAt": null,
      "children": []
    }
  ]
}
```

`subFlowType`: `S` = SubFlow (parent'a resume eder), `P` = SubProcess (fire-and-forget)

**HTTP 404** — Instance bulunamadığında.

---

### 1.10 Instance Parent

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/parent
```

Bir alt-akış instance'ından parent instance'a ters navigasyon yapar.

#### Response — 200 OK (alt-akış instance'ı)

```json
{
  "parent": {
    "parentInstanceId": "8e298c72-457c-4cd2-b3f2-e94fd5bf5a41",
    "key": "INST-0042",
    "flow": "lifecycle-transitions-test-workflow",
    "domain": "core",
    "parentState": "approval",
    "correlationType": "S"
  }
}
```

#### Response — 200 OK (root instance)

```json
{
  "parent": null
}
```

`correlationType`: `S` = SubFlow, `P` = SubProcess

**HTTP 404** — Instance bulunamadığında.

---

### 1.11 Instance Tasks (Liste)

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/tasks
```

Instance'ın çalıştırdığı tüm task'ları, StartedAt'a göre artan sırada döner.

#### Response — 200 OK

```json
{
  "items": [
    {
      "id": "660e8400-e29b-41d4-a716-446655440111",
      "taskDefinitionKey": "send-notification",
      "status": "Completed",
      "businessStatus": "Success",
      "startedAt": "2026-06-10T08:05:01Z",
      "finishedAt": "2026-06-10T08:05:01.5Z",
      "durationMs": 500
    },
    {
      "id": "770e8400-e29b-41d4-a716-446655440222",
      "taskDefinitionKey": "call-payment-api",
      "status": "Faulted",
      "businessStatus": "Failed",
      "startedAt": "2026-06-10T10:00:01Z",
      "finishedAt": "2026-06-10T10:00:03Z",
      "durationMs": 2000
    }
  ],
  "total": 2
}
```

**HTTP 404** — Instance bulunamadığında.

---

### 1.12 Instance Task Detayı

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/tasks/{taskId}
```

Tek bir task çalıştırmasının tam detayını döner: tanım, tetikleyici konum, girdi/çıktı ve hata bilgisi.

#### Path Parametreleri

| Parametre | Tip  | Açıklama              |
|-----------|------|-----------------------|
| `taskId`  | Guid | Task entity GUID'i    |

#### Response — 200 OK

```json
{
  "id": "660e8400-e29b-41d4-a716-446655440111",
  "taskDefinitionKey": "call-payment-api",
  "status": "Faulted",
  "businessStatus": "Failed",
  "startedAt": "2026-06-10T10:00:01Z",
  "finishedAt": "2026-06-10T10:00:03Z",
  "durationMs": 2000,
  "triggerContext": {
    "triggerLocation": "OnExecute",
    "contextType": "Transition",
    "contextKey": "process-payment",
    "order": 0,
    "mappingScript": null
  },
  "definition": {
    "key": "call-payment-api",
    "type": "Http",
    "version": "1.0.0",
    "config": {
      "url": "https://payment-service/api/pay",
      "method": "POST"
    }
  },
  "input": { "amount": 5000 },
  "output": { "error": "timeout" },
  "faultedByTaskId": null,
  "error": {
    "message": "Request timeout after 2000ms",
    "exceptionType": "HttpRequestException",
    "stackTrace": "..."
  },
  "invocationResult": {
    "isSuccess": false,
    "statusCode": 504,
    "executionDurationMs": 2000,
    "headers": { "content-type": "application/json" },
    "body": { "error": "timeout" }
  },
  "actions": [
    {
      "id": "act-001",
      "status": "Failed",
      "startedAt": "2026-06-10T10:00:01Z",
      "finishedAt": "2026-06-10T10:00:03Z",
      "durationMs": 2000,
      "detail": { "attempt": 1 }
    }
  ]
}
```

`triggerLocation` değerleri: `OnExecute`, `OnExit`, `OnEntry`  
`definition` ve `triggerContext` best-effort'tur; tanım cache'de yoksa `null` döner.  
`error` her zaman dolu bir nesne döner; hata yoksa tüm alanlar `null`'dır.

**HTTP 404** — Instance veya task bulunamadığında.

---

## 2. Component Endpoint'leri

### 2.1 Component Özet Listesi / Detayı

```
GET api/v1.0/monitor/{domain}/components?type={type}[&key={key}][&version={version}]
```

`key` verilmezse tüm component'ların hafif özetini; `key` verilirse tek component'ın detayını döner.

#### Query Parametreleri

| Parametre | Zorunlu | Açıklama                                    |
|-----------|---------|---------------------------------------------|
| `type`    | Evet    | `sys-flows`, `sys-tasks`, `sys-schemas`, `sys-extensions`, `sys-functions`, `sys-views` |
| `key`     | Hayır   | Verilirse tek component; yoksa liste        |
| `version` | Hayır   | Versiyona göre filtre; yoksa en güncel      |

#### Response — 200 OK (`key` yokken — özet liste)

```json
{
  "componentType": "sys-flows",
  "items": [
    {
      "key": "lifecycle-transitions-test-workflow",
      "version": "1.2.0",
      "domain": "core",
      "labels": [
        { "language": "tr", "label": "Yaşam Döngüsü Test Akışı" },
        { "language": "en", "label": "Lifecycle Test Workflow" }
      ],
      "type": { "value": 1 },
      "comment": "Test akışı, prod'a gitmez"
    }
  ]
}
```

#### Response — 200 OK (`key` verilince — flat detay)

```json
{
  "key": "lifecycle-transitions-test-workflow",
  "version": "1.2.0",
  "domain": "core",
  "flow": "sys-flows",
  "labels": [
    { "language": "tr", "label": "Yaşam Döngüsü Test Akışı" }
  ],
  "type": { "value": 1 },
  "comment": null,
  "versions": ["1.2.0", "1.1.0", "1.0.0"]
}
```

**HTTP 400** — Bilinmeyen component type.  
**HTTP 404** — `key` verildi ama bulunamadı.

---

### 2.2 Component Tam Tanım

```
GET api/v1.0/monitor/{domain}/components/definition?type={type}[&key={key}][&version={version}]
```

Component'ların tam JSON tanımını döner. `key` verilmezse o type'a ait tüm tanımlar gelir.

#### Query Parametreleri

| Parametre | Zorunlu | Açıklama                               |
|-----------|---------|----------------------------------------|
| `type`    | Evet    | Component type                         |
| `key`     | Hayır   | Tek component; yoksa tümü             |
| `version` | Hayır   | Exact versiyon; yoksa en güncel        |

#### Response — 200 OK

```json
{
  "componentType": "sys-flows",
  "items": [
    {
      "key": "lifecycle-transitions-test-workflow",
      "version": "1.2.0",
      "flow": "sys-flows",
      "states": [...],
      "transitions": [...],
      "_comment": "..."
    }
  ]
}
```

Her `items` elemanı component'ın ham JSON tanımıdır; yapı component type'a göre değişir.

**HTTP 400** — Bilinmeyen component type.  
**HTTP 404** — `key` verildi ama bulunamadı.

---

### 2.3 Component İstatistikleri

```
GET api/v1.0/monitor/{domain}/stats/components
```

Domain'deki her tip için yayınlanmış component sayısını döner.

#### Response — 200 OK

```json
{
  "flows": 12,
  "tasks": 45,
  "schemas": 18,
  "views": 22,
  "functions": 8,
  "extensions": 5,
  "total": 110
}
```

---

### 2.4 Workflow Bağımlılıkları

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/dependencies[?version={version}]
```

Bir workflow tanımının kullandığı tüm component bağımlılıklarını döner.

#### Query Parametreleri

| Parametre | Açıklama                          |
|-----------|-----------------------------------|
| `version` | Belirli versiyon; yoksa en güncel |

#### Response — 200 OK

```json
{
  "workflow": {
    "key": "lifecycle-transitions-test-workflow",
    "version": "1.2.0",
    "domain": "core"
  },
  "dependencies": {
    "tasks": [
      {
        "key": "send-notification",
        "version": "1.0.0",
        "domain": "core",
        "referencedFrom": "transition:submit/onExecute"
      }
    ],
    "schemas": [
      {
        "key": "application-schema",
        "version": "2.0.0",
        "domain": "core",
        "referencedFrom": "global"
      }
    ],
    "views": [],
    "functions": [
      {
        "key": "get-customer",
        "version": "1.0.0",
        "domain": "core",
        "referencedFrom": "state:approved/onEntry"
      }
    ],
    "extensions": [],
    "subFlows": [
      {
        "key": "document-check",
        "version": "1.0.0",
        "domain": "core",
        "referencedFrom": "state:approval"
      }
    ]
  }
}
```

**HTTP 404** — Workflow tanımı bulunamadığında.

---

## 3. Stats Endpoint'leri

### 3.1 Workflow Instance Sayaçları

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/stats/instances[?version={version}]
```

Belirli bir workflow'daki instance'ların durum bazlı sayılarını döner.

#### Query Parametreleri

| Parametre | Açıklama                                      |
|-----------|-----------------------------------------------|
| `version` | Belirli workflow versiyonu; yoksa tüm versiyonlar |

#### Response — 200 OK

```json
{
  "active": 42,
  "busy": 3,
  "completed": 1205,
  "faulted": 7,
  "passive": 0,
  "total": 1257
}
```

---

### 3.2 Domain Instance Sayaçları

```
GET api/v1.0/monitor/{domain}/stats/instances
```

Domain'deki tüm workflow'ların instance sayılarını toplar. Paralel schema taraması yapılır; snapshot boşsa runtime backend'e düşer.

#### Response — 200 OK

Yapı `3.1` ile aynıdır:

```json
{
  "active": 183,
  "busy": 12,
  "completed": 9421,
  "faulted": 34,
  "passive": 2,
  "total": 9652
}
```

---

### 3.3 State Dağılımı

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/stats/states[?version={version}]
```

Aktif instance'ların workflow state'lerine göre dağılımını döner. Dashboard heat-map widget'ı için.

#### Query Parametreleri

| Parametre | Açıklama                             |
|-----------|--------------------------------------|
| `version` | Belirli workflow versiyonu           |

#### Response — 200 OK

```json
{
  "states": [
    { "stateKey": "draft",     "total": 15, "active": 10, "busy": 2, "faulted": 3 },
    { "stateKey": "in-review", "total": 28, "active": 25, "busy": 3, "faulted": 0 },
    { "stateKey": "approved",  "total": 8,  "active": 7,  "busy": 1, "faulted": 0 }
  ],
  "totalActiveInstances": 42
}
```

**HTTP 404** — Workflow tanımı cache'de bulunamadığında.

---

### 3.4 Hata İstatistikleri

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/stats/faults
```

Faulted instance sayılarını, state ve task bazında gruplar; zaman pencereli trend döner.

#### Response — 200 OK

```json
{
  "totalFaulted": 7,
  "byState": [
    { "key": "processing", "count": 5 },
    { "key": "in-review",  "count": 2 }
  ],
  "byTask": [
    { "key": "call-payment-api",  "count": 4 },
    { "key": "send-notification", "count": 1 }
  ],
  "trend": {
    "last1h":  2,
    "last24h": 7,
    "last7d":  23
  }
}
```

---

### 3.5 Task İstatistikleri

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/stats/tasks
```

Her task key'i için çalıştırma sayısı ve başarı/hata oranlarını döner.

#### Response — 200 OK

```json
{
  "byTask": [
    {
      "taskKey": "send-notification",
      "executionCount": 1205,
      "successRate": 0.994,
      "failureRate": 0.006
    },
    {
      "taskKey": "call-payment-api",
      "executionCount": 980,
      "successRate": 0.959,
      "failureRate": 0.041
    }
  ]
}
```

---

### 3.6 Tamamlanma Süresi İstatistikleri

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/stats/duration
```

Tamamlanmış instance'lar için ortalama/min/max süre istatistiği döner (ms cinsinden).

#### Response — 200 OK

```json
{
  "avgMs": 125400.5,
  "minMs": 3200.0,
  "maxMs": 892000.0,
  "completedCount": 1205
}
```

---

### 3.7 Geçiş İstatistikleri

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/stats/transitions
```

Her geçiş key'i için çalıştırma sayısı, tamamlanma oranı ve tetikleyici tipi dağılımını döner. Flow density (akış yoğunluğu) da içerir.

#### Response — 200 OK

```json
{
  "byTransition": [
    {
      "transitionKey": "submit",
      "count": 1205,
      "completionRate": 0.997,
      "triggerTypeBreakdown": {
        "manual": 1200,
        "automatic": 0,
        "scheduled": 5,
        "event": 0
      }
    },
    {
      "transitionKey": "approve",
      "count": 980,
      "completionRate": 0.985,
      "triggerTypeBreakdown": {
        "manual": 980,
        "automatic": 0,
        "scheduled": 0,
        "event": 0
      }
    }
  ],
  "flowDensity": [
    { "fromState": "draft",     "toState": "in-review", "count": 1205 },
    { "fromState": "in-review", "toState": "approved",  "count": 980 },
    { "fromState": "in-review", "toState": "rejected",  "count": 218 }
  ]
}
```

---

## 4. Yetkilendirme Endpoint'leri

### 4.1 Workflow Yetki Matrisi

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/permissions[?version={version}][&role={role}]
```

Workflow'un tüm yetki matrisini döner: query rolleri, state görünüm izinleri, geçiş yürütme izinleri, function çağırma izinleri.

#### Query Parametreleri

| Parametre | Açıklama                                                  |
|-----------|-----------------------------------------------------------|
| `version` | Belirli workflow versiyonu                                |
| `role`    | Verilirse yalnızca o rolün göründüğü kayıtlar filtrelenir |

#### Response — 200 OK

```json
{
  "workflowKey": "lifecycle-transitions-test-workflow",
  "version": "1.2.0",
  "queryRoles": [
    { "role": "morph-idm.viewer", "grant": "allow" }
  ],
  "states": [
    {
      "key": "in-review",
      "queryRoles": [
        { "role": "morph-idm.reviewer", "grant": "allow" }
      ]
    }
  ],
  "transitions": [
    {
      "key": "approve",
      "from": "in-review",
      "target": "approved",
      "roles": [
        { "role": "morph-idm.approver", "grant": "allow" }
      ]
    },
    {
      "key": "reject",
      "from": "in-review",
      "target": "rejected",
      "roles": [
        { "role": "morph-idm.approver", "grant": "allow" }
      ]
    }
  ],
  "functions": [
    {
      "key": "get-customer",
      "roles": [
        { "role": "morph-idm.maker", "grant": "allow" }
      ]
    }
  ]
}
```

`grant` değerleri: `allow`, `deny`. DENY her zaman ALLOW'u geçersiz kılar.

**HTTP 404** — Workflow bulunamadığında.

---

### 4.2 Instance İzin Görünümü

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/permissions[&role={role}]
```

Instance'ın mevcut state'ine göre izin görünümü döner. Workflow matrisinin canlı state'e odaklanmış hali.

#### Query Parametreleri

| Parametre | Açıklama                                    |
|-----------|---------------------------------------------|
| `role`    | Verilirse o role göre filtreli sonuç döner  |

#### Response — 200 OK

```json
{
  "workflowKey": "lifecycle-transitions-test-workflow",
  "version": "1.2.0",
  "queryRoles": [
    { "role": "morph-idm.viewer", "grant": "allow" }
  ],
  "state": {
    "key": "in-review",
    "queryRoles": [
      { "role": "morph-idm.reviewer", "grant": "allow" }
    ]
  },
  "transitions": [
    {
      "key": "approve",
      "from": "in-review",
      "target": "approved",
      "roles": [{ "role": "morph-idm.approver", "grant": "allow" }]
    }
  ],
  "functions": [
    {
      "key": "get-customer",
      "roles": [{ "role": "morph-idm.maker", "grant": "allow" }]
    }
  ]
}
```

**HTTP 404** — Instance veya workflow bulunamadığında.

---

### 4.3 Geçiş İzinleri Alt Görünümü

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/permissions/transitions[?version={version}]
```

Yalnızca geçiş düzeyi yetkilendirme kayıtlarını döner.

#### Response — 200 OK

```json
{
  "transitions": [
    {
      "key": "submit",
      "from": "draft",
      "target": "in-review",
      "roles": [{ "role": "morph-idm.maker", "grant": "allow" }]
    },
    {
      "key": "approve",
      "from": "in-review",
      "target": "approved",
      "roles": [{ "role": "morph-idm.approver", "grant": "allow" }]
    }
  ]
}
```

**HTTP 404** — Workflow bulunamadığında.

---

### 4.4 Function İzinleri Alt Görünümü

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/permissions/functions[?version={version}]
```

Yalnızca function düzeyi yetkilendirme kayıtlarını döner.

#### Response — 200 OK

```json
{
  "functions": [
    {
      "key": "get-customer",
      "roles": [{ "role": "morph-idm.maker", "grant": "allow" }]
    }
  ]
}
```

**HTTP 404** — Workflow bulunamadığında.

---

## 5. Function Endpoint'leri

### 5.1 Domain-Scope Function Tanımları

```
GET api/v1.0/monitor/{domain}/functions/scope
```

Domain'e kayıtlı, herhangi bir workflow'dan explicit kayıt olmaksızın çağrılabilen (Domain scope) function tanımlarını döner.

#### Response — 200 OK

```json
{
  "items": [
    {
      "key": "get-customer",
      "version": "1.0.0",
      "scope": "Domain",
      "taskCount": 2,
      "roles": [
        { "role": "morph-idm.maker", "grant": "allow" }
      ]
    },
    {
      "key": "get-account",
      "version": "2.1.0",
      "scope": "Domain",
      "taskCount": 1,
      "roles": []
    }
  ],
  "total": 2
}
```

---

### 5.2 Instance Workflow Function Tanımları

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/instances/{instance}/functions/scope
```

Instance'ın çalıştığı workflow versiyonunda kayıtlı function tanımlarını döner. Instance'ın başlatıldığı versiyon kullanılır.

#### Response — 200 OK

Yapı `5.1` ile aynıdır. Yalnızca o workflow'a özel (`Flow` scope) function'lar listelenir.

**HTTP 404** — Instance veya workflow tanımı bulunamadığında.

---

## 6. Job Endpoint'leri

### 6.1 Workflow Aktif Job'ları

```
GET api/v1.0/monitor/{domain}/workflows/{workflow}/jobs
```

Belirli bir workflow'daki aktif scheduled job'ları ve timer'ları döner.

#### Response — 200 OK

```json
{
  "jobs": [
    {
      "jobId": "job-001",
      "name": "payment-reminder-timer",
      "instanceId": "8e298c72-457c-4cd2-b3f2-e94fd5bf5a41",
      "flow": "lifecycle-transitions-test-workflow",
      "domain": "core",
      "isActive": true,
      "createdAt": "2026-06-10T08:00:00Z",
      "modifiedAt": "2026-06-10T09:00:00Z"
    }
  ]
}
```

---

### 6.2 Domain Aktif Job'ları

```
GET api/v1.0/monitor/{domain}/jobs
```

Domain genelindeki aktif job'ları döner. Best-effort: yalnızca çözümlenen (resolved) schema taranır.

#### Response — 200 OK

Yapı `6.1` ile aynıdır; birden fazla workflow'un job'larını içerebilir.

---

## 7. Yapılandırma Endpoint'i

### 7.1 Runtime Config

```
GET api/v1.0/config
```

Monitor host'un gizli bilgi içermeyen çalışma zamanı yapılandırmasını döner. Bağlantı stringleri ve secret'lar hariç tutulur.

#### Response — 200 OK

```json
{
  "runtimeVersion": "1.5.0+build.42",
  "monitor": {
    "redisMode": "Standalone",
    "tracingEnabled": true,
    "metricsEnabled": true,
    "vaultEnabled": false
  }
}
```

---

## 8. Sağlık Endpoint'leri

### 8.1 Detaylı Health Check

```
GET monitor/health/detail
```

Her kayıtlı bileşen (PostgreSQL, Redis, vNext Orchestrator, self) için ayrı durum raporunu döner.

> Not: Bu endpoint API versiyon segmenti içermez (`/api/v1.0/` prefix'i yoktur).

#### Response — 200 OK

```json
{
  "status": "Healthy",
  "totalDurationMs": 45.2,
  "entries": [
    {
      "name": "postgresql",
      "status": "Healthy",
      "durationMs": 12.5,
      "description": "PostgreSQL connection OK",
      "exception": null,
      "data": {}
    },
    {
      "name": "redis",
      "status": "Healthy",
      "durationMs": 3.1,
      "description": "Redis connection OK",
      "exception": null,
      "data": {}
    },
    {
      "name": "self",
      "status": "Healthy",
      "durationMs": 0.1,
      "description": null,
      "exception": null,
      "data": {}
    }
  ]
}
```

**HTTP 503** — Bir veya daha fazla bileşen degraded/unhealthy olduğunda; yanıt gövdesi aynı formattadır.

### 8.2 Standart Probe Endpoint'leri

| Route       | Açıklama                          |
|-------------|-----------------------------------|
| `GET /health` | Tüm health check'lerin özet sonucu |
| `GET /ready`  | Readiness probe (trafik almaya hazır mı?) |
| `GET /live`   | Liveness probe (process sağlıklı mı?)    |
| `GET /version`| Uygulama versiyon bilgisi        |
| `GET /metrics`| Prometheus formatında metrikler  |

---

## Hızlı Referans — Tüm Endpoint'ler

| # | Method | Route | Açıklama |
|---|--------|-------|----------|
| 1 | GET | `monitor/{d}/workflows/{w}/instances` | Instance listesi (sayfalı, filtrelenebilir) |
| 2 | GET | `monitor/{d}/workflows/{w}/instances/{i}` | Instance detayı |
| 3 | GET | `monitor/{d}/workflows/{w}/instances/{i}/data` | Data + versiyon geçmişi |
| 4 | GET | `monitor/{d}/workflows/{w}/instances/{i}/view` | Mevcut state view'u |
| 5 | GET | `monitor/{d}/workflows/{w}/instances/{i}/timeline` | Birleşik timeline |
| 6 | GET | `monitor/{d}/workflows/{w}/instances/{i}/state` | Anlık durum + yapılabilir geçişler |
| 7 | GET | `monitor/{d}/workflows/{w}/instances/{i}/faults` | Hata kök nedeni |
| 8 | GET | `monitor/{d}/workflows/{w}/instances/{i}/data/diff` | Data versiyon farkı |
| 9 | GET | `monitor/{d}/workflows/{w}/instances/{i}/hierarchy` | Alt-akış ağacı |
| 10 | GET | `monitor/{d}/workflows/{w}/instances/{i}/parent` | Parent instance (ters navigasyon) |
| 11 | GET | `monitor/{d}/workflows/{w}/instances/{i}/tasks` | Çalıştırılan task listesi |
| 12 | GET | `monitor/{d}/workflows/{w}/instances/{i}/tasks/{t}` | Tek task detayı |
| 13 | GET | `monitor/{d}/components` | Component özet listesi veya detayı |
| 14 | GET | `monitor/{d}/components/definition` | Component tam tanımı |
| 15 | GET | `monitor/{d}/stats/components` | Component tip sayıları |
| 16 | GET | `monitor/{d}/workflows/{w}/dependencies` | Workflow bağımlılıkları |
| 17 | GET | `monitor/{d}/workflows/{w}/stats/instances` | Workflow instance sayaçları |
| 18 | GET | `monitor/{d}/stats/instances` | Domain instance sayaçları |
| 19 | GET | `monitor/{d}/workflows/{w}/stats/states` | State dağılımı |
| 20 | GET | `monitor/{d}/workflows/{w}/stats/faults` | Hata istatistikleri |
| 21 | GET | `monitor/{d}/workflows/{w}/stats/tasks` | Task çalıştırma istatistikleri |
| 22 | GET | `monitor/{d}/workflows/{w}/stats/duration` | Tamamlanma süresi istatistikleri |
| 23 | GET | `monitor/{d}/workflows/{w}/stats/transitions` | Geçiş istatistikleri + akış yoğunluğu |
| 24 | GET | `monitor/{d}/workflows/{w}/permissions` | Workflow yetki matrisi |
| 25 | GET | `monitor/{d}/workflows/{w}/instances/{i}/permissions` | Instance izin görünümü |
| 26 | GET | `monitor/{d}/workflows/{w}/permissions/transitions` | Geçiş izinleri alt görünümü |
| 27 | GET | `monitor/{d}/workflows/{w}/permissions/functions` | Function izinleri alt görünümü |
| 28 | GET | `monitor/{d}/functions/scope` | Domain-scope function tanımları |
| 29 | GET | `monitor/{d}/workflows/{w}/instances/{i}/functions/scope` | Instance workflow function tanımları |
| 30 | GET | `monitor/{d}/workflows/{w}/jobs` | Workflow aktif job'ları |
| 31 | GET | `monitor/{d}/jobs` | Domain aktif job'ları |
| 32 | GET | `config` | Runtime yapılandırması |
| 33 | GET | `monitor/health/detail` | Detaylı health check raporu |
| 34 | GET | `/health` `/ready` `/live` `/version` `/metrics` | Standart probe endpoint'leri |

> Tüm route'lar `api/v1.0/` prefix'i alır; **8.1 (health/detail)** ve **8.2 (probe'lar)** hariç.
