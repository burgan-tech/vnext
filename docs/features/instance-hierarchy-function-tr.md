# Instance Hierarchy Fonksiyonu

## Genel Bakış

Hierarchy fonksiyonu, bir workflow instance'ının runtime hiyerarşisini özyinelemeli (recursive) ağaç yapısı olarak döndürür. Hem doğrudan hem de dolaylı alt subflow ve subprocess instance'larını içerir; iç içe geçmiş workflow ilişkilerinin tam görünürlüğünü sağlar.

## API Uç Noktaları

### Tek Instance Hiyerarşisi

Belirli bir instance için tam hiyerarşi ağacını döndürür.

```
GET /api/v1/{domain}/workflows/{workflow}/instances/{instance}/functions/hierarchy
```

| Parametre  | Konum   | Açıklama                              |
|------------|---------|----------------------------------------|
| `domain`   | Route   | Domain adı                             |
| `workflow` | Route   | Workflow (flow) adı                    |
| `instance` | Route   | Instance key veya GUID                 |

### Liste Hiyerarşisi

Sayfalanmış instance listesi için hiyerarşi ağaçları döndürür (sayfadaki her instance için bir ağaç).

```
GET /api/v1/{domain}/workflows/{workflow}/functions/hierarchy
```

Standart liste parametrelerini destekler: `page`, `pageSize`, `filter`, `sort`, `orderBy`.

## Yanıt Yapısı

### GetInstanceHierarchyOutput

| Özellik  | Tip                    | Açıklama                               |
|----------|------------------------|-----------------------------------------|
| `root`   | InstanceHierarchyNode  | Kök düğüm (talep edilen instance)      |

### InstanceHierarchyNode

| Özellik       | Tip                    | Açıklama                                                      |
|---------------|------------------------|----------------------------------------------------------------|
| `id`          | Guid                   | Instance ID                                                   |
| `key`         | string?                | Okunabilir instance anahtarı                                  |
| `flow`        | string                 | Workflow (flow) adı                                            |
| `domain`      | string                 | Domain                                                         |
| `flowVersion` | string?                | Flow versiyonu                                                |
| `currentState`| string?                | Mevcut state anahtarı                                         |
| `status`      | InstanceStatus?        | Instance durumu (Active, Completed, Faulted, vb.)             |
| `subFlowType` | SubFlowType?           | SubFlow (S) veya SubProcess (P). Kök instance için null.      |
| `isCompleted` | bool                   | Subflow/subprocess korelasyonunun tamamlanıp tamamlanmadığı  |
| `completedAt` | DateTime?              | Subflow/subprocess tamamlanma zamanı                          |
| `parentState` | string?                | Bu subflow'un başlatıldığı parent state                       |
| `children`    | List\<InstanceHierarchyNode\> | İç içe alt subflow/subprocess instance'ları          |

## Örnek Yanıt

```json
{
  "root": {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "key": "order-123",
    "flow": "OrderWorkflow",
    "domain": "sales",
    "flowVersion": "1.0.0",
    "currentState": "ApprovalPending",
    "status": { "code": "A", "description": "Active" },
    "subFlowType": null,
    "isCompleted": false,
    "completedAt": null,
    "parentState": null,
    "children": [
      {
        "id": "7cb85f64-5717-4562-b3fc-2c963f66b0a7",
        "key": "approval-sub-1",
        "flow": "ApprovalSubflow",
        "domain": "sales",
        "flowVersion": "1.0.0",
        "currentState": "Completed",
        "status": { "code": "C", "description": "Completed" },
        "subFlowType": { "code": "S", "description": "Sub Flow" },
        "isCompleted": true,
        "completedAt": "2025-03-02T10:30:00Z",
        "parentState": "ApprovalPending",
        "children": [
          {
            "id": "9db85f64-5717-4562-b3fc-2c963f66c1b8",
            "key": "notification-task",
            "flow": "NotificationSubprocess",
            "domain": "sales",
            "flowVersion": "1.0.0",
            "currentState": "Sent",
            "status": { "code": "C", "description": "Completed" },
            "subFlowType": { "code": "P", "description": "Sub Process" },
            "isCompleted": true,
            "completedAt": "2025-03-02T10:25:00Z",
            "parentState": "Review",
            "children": []
          }
        ]
      }
    ]
  }
}
```

## Kullanım Alanları

- **Runtime görselleştirme**: Admin UI veya dashboard'larda instance hiyerarşisini gösterme
- **Denetim ve izlenebilirlik**: İç içe workflow'lar arasında yürütme akışını takip etme
- **Hata ayıklama**: Geliştirme sırasında parent-child ilişkilerini anlama
- **Raporlama**: Hiyerarşi seviyelerinde metrikleri toplama

## Multi-Schema Desteği

Alt instance'lar farklı workflow'larda (schema'larda) bulunabilir. Hierarchy fonksiyonu ağacı gezerken schema context'ini otomatik olarak değiştirir:

- Korelasyonlar parent'ın schema'sında sorgulanır
- Alt instance'lar alt flow'un schema'sından yüklenir (SubFlowName/SubFlowDomain)

## Kapsam

- **Dahil edilen**: Hem aktif hem tamamlanmış alt korelasyonlar (tam tarihsel hiyerarşi)
- **Özyinelemeli**: Sınırsız iç içe geçiş—subflow'un subflow'u tam olarak taranır
- **Her iki tip**: SubFlow (bloklayıcı) ve SubProcess (bloklamayan) dahil edilir

## İlgili Belgeler

- [Domain Models](./architecture/domain-models.md) — Instance ve InstanceCorrelation entity'leri
- [Multi-Schema](./architecture/multi-schema.md) — Schema değiştirme ve çözümleme
