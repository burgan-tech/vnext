# Schema-Driven Filtering

## Overview

Workflow'un JSON Schema tanımında bulunan custom extension'lar (`x-filterOperators`, `x-sortable`, `x-displayFormat`) kullanarak, instance listelerinde hangi alanların filtrelenebilir/sıralanabilir olduğunu, hangi operatorlerin kullanılabileceğini ve alan tipine gore nasil karsilastirilacagini kontrol eden mekanizma.

## Schema Contract

Workflow'un master schema'sinda her property icin asagidaki extension'lar tanimlanabilir:

| Extension | Tip | Zorunlu | Aciklama |
|---|---|---|---|
| `x-filterOperators` | `string[]` | Hayir | Izin verilen filtre operatorleri. Bos veya yok ise alan filtrelenemez |
| `x-sortable` | `boolean` | Hayir | `true` ise alan siralanabilir. Yok ise siralanabilir degil |
| `x-displayFormat` | `string` | Hayir | UI'a yonelik format bilgisi (orn: `yyyy-MM-dd'T'HH:mm:ssXXX`) |

### Kurallar

1. `x-filterOperators` mevcut ve dolu ise alan filtrelenebilir. Bos veya yok ise alan filtrelenemez
2. `x-sortable: true` ise alan siralanabilir. Tanimli degilse siralanabilir degil
3. Filtrelenemez bir alan sorgulandiginda veya izin verilmeyen bir operator kullanildiginda `SchemaFilterValidationException` firlatilir

### Tip-Operator Iliskisi

| Schema `type` | Operator kategorisi | SQL davranisi |
|---|---|---|
| `number` / `integer` | gt, lt, gte, lte, between | `accessor::numeric {op} @param` |
| `string` + gt/lt/gte/lte/between | tarih karsilastirma | `accessor::timestamptz {op} @param` |
| `string` + eq/contains/startsWith/endsWith | metin karsilastirma | `accessor ILIKE @param` |
| `boolean` | eq, neq | equality |

## Ornek Schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "properties": {
    "advisor": {
      "type": "string",
      "x-filterOperators": ["eq", "neq", "contains", "startsWith", "endsWith", "in", "nin"],
      "x-sortable": true
    },
    "advisorType": {
      "type": "string"
    },
    "startDateTime": {
      "type": "string",
      "format": "date-time",
      "x-filterOperators": ["eq", "gt", "gte", "lt", "lte", "between"],
      "x-sortable": true,
      "x-displayFormat": "yyyy-MM-dd'T'HH:mm:ssXXX"
    },
    "endDateTime": {
      "type": "string",
      "format": "date-time",
      "x-filterOperators": ["eq", "gt", "gte", "lt", "lte", "between"],
      "x-sortable": true,
      "x-displayFormat": "yyyy-MM-dd'T'HH:mm:ssXXX"
    },
    "amount": {
      "type": "number",
      "x-filterOperators": ["eq", "gt", "gte", "lt", "lte", "between"],
      "x-sortable": true
    },
    "customerName": {
      "type": "string",
      "x-filterOperators": ["eq", "contains", "startsWith", "endsWith"],
      "x-sortable": true
    },
    "isActive": {
      "type": "boolean",
      "x-filterOperators": ["eq", "neq"]
    }
  }
}
```

Bu schema'ya gore:

- `advisor` -- metin filtreleri ve siralama desteklenir
- `advisorType` -- `x-filterOperators` yok, **filtrelenemez ve siralanamaz**
- `startDateTime` / `endDateTime` -- tarih karsilastirma (`gt`, `lt`, `between`) ve siralama desteklenir
- `amount` -- numeric karsilastirma desteklenir
- `customerName` -- metin aramasi desteklenir, range operatorleri (`gt`, `lt`) **izin verilmez**
- `isActive` -- sadece esitlik kontrolu, range veya metin aramalari **izin verilmez**

## Operator Isimlendirmesi

Schema'daki operator isimleri ile filter API'sindeki operator isimleri arasinda mapping vardir:

| Schema operator | Filter API operator | Aciklama |
|---|---|---|
| `eq` | `eq` | Esit |
| `neq` | `ne` | Esit degil |
| `gt` | `gt` | Buyuktur |
| `gte` | `ge` | Buyuk esit |
| `lt` | `lt` | Kucuktur |
| `lte` | `le` | Kucuk esit |
| `between` | `between` | Aralik |
| `contains` | `like` / `match` | Icerir (ILIKE) |
| `startsWith` | `startswith` | Ile baslar |
| `endsWith` | `endswith` | Ile biter |
| `in` | `in` | Listede var |
| `nin` | `nin` | Listede yok |
| `isNull` | `isnull` | Null kontrolu |

## Ornek Filtre Sorgulari

### Tarih karsilastirma (string + gt)

Belirli bir tarihten sonra baslayan kayitlar:

```
GET /api/v1/morph-touch/workflows/absence-entry/instances?page=1&pageSize=10&filter=...
```

Filter JSON:
```json
{
  "attributes": {
    "startDateTime": {
      "gt": "2026-04-18T23:59:59+03:00"
    }
  }
}
```

Uretilen SQL:
```sql
SELECT s.*
FROM "absence_entry"."Instances" s
WHERE s."Id" IN (
    SELECT "InstanceId"
    FROM "absence_entry"."InstancesData"
    WHERE "IsLatest" = true
      AND ("Data" ->> 'startDateTime')::timestamptz > @p0
)
ORDER BY s."CreatedAt" DESC
```

### Tarih araligi (between)

Iki tarih arasindaki kayitlar:

```json
{
  "attributes": {
    "startDateTime": {
      "between": ["2026-04-01T00:00:00Z", "2026-04-30T23:59:59Z"]
    }
  }
}
```

Uretilen SQL:
```sql
... AND ("Data" ->> 'startDateTime')::timestamptz BETWEEN @p0 AND @p1
```

### Metin arama (contains + eq)

Belirli bir advisor'un kayitlari:

```json
{
  "attributes": {
    "advisor": {
      "eq": "touch.portfolio-manager.pm-001"
    }
  }
}
```

Advisor ismi icerik aramasiyla:

```json
{
  "attributes": {
    "advisor": {
      "like": "pm-001"
    }
  }
}
```

### Numeric karsilastirma

Tutari 1000'den buyuk kayitlar:

```json
{
  "attributes": {
    "amount": {
      "gt": 1000
    }
  }
}
```

Uretilen SQL:
```sql
... AND ("Data" ->> 'amount')::numeric > @p0
```

### Birden fazla kosul (AND)

Tarih filtresi + advisor filtresi birlikte:

```json
{
  "and": [
    {
      "attributes": {
        "startDateTime": {
          "gt": "2026-04-17T23:59:59Z"
        }
      }
    },
    {
      "attributes": {
        "advisor": {
          "eq": "touch.portfolio-manager.pm-001"
        }
      }
    }
  ]
}
```

### Filtre + GroupBy birlikte

Belirli bir tarihten sonra baslayan kayitlari advisor'a gore grupla:

```
GET /api/v1/.../instances?filter=...&groupBy=...
```

Filter:
```json
{
  "attributes": {
    "startDateTime": {
      "gt": "2026-04-17T23:59:59Z"
    }
  }
}
```

GroupBy:
```json
{
  "field": "advisor",
  "aggregations": {
    "count": true
  }
}
```

Beklenen response:
```json
{
  "groups": [
    {
      "keys": { "advisor": "touch.portfolio-manager.pm-001" },
      "aggregations": { "count": 45 }
    },
    {
      "keys": { "advisor": "touch.portfolio-manager.pm-002" },
      "aggregations": { "count": 32 }
    }
  ]
}
```

### Siralama (orderBy)

`startDateTime`'a gore sirala (azalan):

```
GET /api/v1/.../instances?sort={"field":"attributes.startDateTime","direction":"desc"}
```

Schema'da `x-sortable: true` olmayan alanlar icin siralama istegi sessizce yok sayilir.

## Hata Durumlari

### Filtrelenemez alan sorgulandida

`advisorType` icin filtre denendiginde (schema'da `x-filterOperators` yok):

```json
{
  "attributes": {
    "advisorType": {
      "eq": "portfolio-manager"
    }
  }
}
```

Response:
```json
{
  "error": {
    "code": "Validation:900010",
    "message": "Field 'advisorType' is not filterable."
  }
}
```

### Izin verilmeyen operator kullanildiginda

`isActive` alaninda `gt` operatoru denendiginde (schema sadece `eq` ve `neq` izin verir):

```json
{
  "attributes": {
    "isActive": {
      "gt": true
    }
  }
}
```

Response:
```json
{
  "error": {
    "code": "Validation:900010",
    "message": "Operator 'gt' is not allowed for field 'isActive'."
  }
}
```

## Workflow'a Schema Baglama

Workflow JSON'inda `schema` referansi eklenmelidir:

```json
{
  "key": "absence-entry",
  "flow": "sys-flows",
  "domain": "morph-touch",
  "version": "1.0.0",
  "attributes": {
    "type": "S",
    "schema": {
      "key": "absence-entry",
      "domain": "morph-touch",
      "version": "1.0.0"
    },
    "labels": [...]
  }
}
```

Schema bagli degilse (`schema: null`), tum alanlar mevcut davranisla filtrelenebilir (geriye uyumlu).

## Fallback Davranisi

| Durum | Davranis |
|---|---|
| Workflow'da schema tanimli degil | Tum alanlar filtrelenebilir, tum operatorler izinli, `gt`/`lt` sadece numeric (mevcut davranis) |
| Orchestration `Workflow:InstanceFiltering:EnforceMasterSchemaFiltering` = `false` | Workflow'da schema bagli olsa bile tum alanlar filtrelenebilir kabul edilir, range karsilastirmalari `::numeric`, attribute `orderBy` icin `x-sortable` kontrolu yapilmaz (Orchestration `appsettings`) |
| Schema tanimli ama alan schema'da yok | Alan filtrelenemez (`SchemaFilterValidationException`) |
| Schema tanimli, alan var, `x-filterOperators` bos/yok | Alan filtrelenemez |
| Schema tanimli, alan var, operator listede yok | Operator izinli degil (`SchemaFilterValidationException`) |

## Mimari

```
InstanceQueryAppService
    |
    |-- 1. componentCacheStore.GetFlowAsync(domain, workflow)
    |-- 2. componentCacheStore.GetSchemaAsync(workflow.Schema)
    |-- 3. SchemaFilterMetadataResolver.Resolve(schema.Schema) --> SchemaFilterContext
    |
    |-- 4. Workflow:InstanceFiltering:EnforceMasterSchemaFiltering false ise schemaContext = null
    |
    |-- 5a. parsedRequest.SchemaContext = schemaContext  (GraphQLFilterRequest path)
    |-- 5b. schemaContext parametresi ile repo cagirma    (string filter path)
    |
    v
IInstanceRepository / EfCoreInstanceRepository
    |
    v
UnifiedFilterService --> GraphQLJsonFilterService
    |
    |-- BuildFieldConditions: IsFieldFilterable + IsOperatorAllowed kontrolleri
    |-- BuildComparisonCondition: type'a gore numeric/datetime/text SQL cast
    |-- BuildOrderByClause: IsFieldSortable kontrolu
```

### Ilgili Dosyalar

| Dosya | Rol |
|---|---|
| `Domain/Definitions/Schemas/SchemaFieldMetadata.cs` | Tek bir alanin filter/sort metadata modeli |
| `Domain/Definitions/Schemas/SchemaFilterContext.cs` | Alan metadata map'i + validasyon metodlari |
| `Domain/Definitions/Schemas/SchemaFilterMetadataResolver.cs` | JSON Schema parser (x-extension'lari okur) |
| `Domain/QueryExtensions/GraphQL/GraphQLJsonFilterService.cs` | SQL uretimi, tip-bazli karsilastirma, validasyon |
| `Domain/QueryExtensions/GraphQL/GraphQLFilterModels.cs` | `GraphQLFilterRequest.SchemaContext` property |
| `Domain/QueryExtensions/GraphQL/UnifiedFilterService.cs` | Filter routing, schema context threading |
| `Domain/ExceptionHandling/SchemaFilterValidationException.cs` | Validasyon hatasi exception |
| `Domain/WorkflowErrorCodes.cs` | `SchemaFilterValidation = "Validation:900010"` |
| `Application/Instances/InstanceQueryAppService.cs` | Schema yukleme ve context olusturma |
| `Application/Instances/InstanceFilteringOptions.cs` | `Workflow:InstanceFiltering:EnforceMasterSchemaFiltering` (Orchestration yapilandirmasi) |
| `Infrastructure/Instances/EfCoreInstanceRepository.cs` | Schema context'i SQL pipeline'a aktarma |

## Performans

### SQL Yapisal Optimizasyonu

Filtreli sorgularda CTE + JOIN yerine subquery IN yapisi kullanilir. Bu PostgreSQL optimizer'in dusuk LIMIT degerlerinde Nested Loop yerine Semi Join secmesini saglar:

```sql
-- Optimized: Semi Join (162ms @ LIMIT 5)
SELECT s.*
FROM "schema"."Instances" s
WHERE s."Id" IN (
    SELECT "InstanceId"
    FROM "schema"."InstancesData"
    WHERE "IsLatest" = true AND (json filter conditions)
)
ORDER BY s."CreatedAt" DESC

-- Onceki: CTE + JOIN (22s @ LIMIT 5, Nested Loop problemi)
WITH FilteredData AS (...)
SELECT s.* FROM "Instances" s JOIN FilteredData d ON s."Id" = d."InstanceId"
```

### Onerilen Index'ler

```sql
-- GIN index: eq, ne, in operatorleri icin JSONB containment (@>)
CREATE INDEX IX_InstancesData_Data_GIN 
ON "{schema}"."InstancesData" 
USING GIN ("Data" jsonb_path_ops)
WHERE "IsLatest" = true;

-- Advisor text index: eq, contains, startsWith icin
CREATE INDEX IX_InstancesData_Advisor 
ON "{schema}"."InstancesData" 
(("Data" ->> 'advisor'))
WHERE "IsLatest" = true;

-- CTE/subquery performansi icin composite index
CREATE INDEX IX_InstancesData_IsLatest_InstanceId_EnteredAt 
ON "{schema}"."InstancesData" 
("InstanceId", "EnteredAt" DESC)
WHERE "IsLatest" = true;

-- Instances default ORDER BY icin
CREATE INDEX ix_instances_createdat 
ON "{schema}"."Instances" 
("CreatedAt" DESC);
```

### PostgreSQL Optimizer Ayarlari (SSD icin)

```sql
ALTER DATABASE "dbname" SET random_page_cost = 1.1;
ALTER DATABASE "dbname" SET work_mem = '16MB';
```
