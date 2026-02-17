# Breaking Change: Instance Filter — Tek String (Dizi Yok)

## Özet

Instance listesi ve Data function API’leri ile **GetInstances** task’ı ve ilgili binding’ler artık **tek bir filter string** kabul ediyor; filter dizisi kabul edilmiyor. Bir istekte birden fazla ayrı filter ifadesi desteklenmiyor; karmaşık koşullar tek bir filter içinde ifade edilmeli (ör. `and`/`or` içeren tek bir GraphQL tarzı JSON).

## Etkilenen Alanlar

### 1. HTTP API

| Endpoint / kullanım | Önce | Sonra |
|---------------------|------|--------|
| `GET .../instances` query parametresi | `filter` → `string[]` (örn. `?filter=x&filter=y`) | `filter` → tek `string` (örn. `?filter=<tek değer>`) |
| Data function `GET .../functions/data` | query/body’de `filter` dizi | `filter` tek string |

- **Controller:** `InstanceController.GetInstanceListAsync` — parametre `[FromQuery] string[]? filter` iken artık `[FromQuery] string? filter`.
- **Query model:** `FunctionListQueryParameters.Filter` — tip `string[]?` iken artık `string?`.

### 2. Application / DTO’lar

- **GetInstanceListInput.Filter** — tip `string[]?` iken artık `string?`. Çağıranlar tek filter string veya `null` geçmeli.

### 3. GetInstances Task (workflow tanımı)

- **GetInstancesTask.Filter** — property tipi `string[]?` iken artık `string?`.
- **SetFilter** — imza `SetFilter(string[]? filter)` iken artık `SetFilter(string? filter)`.
- **Task JSON config:**  
  - **Önce:** `"filter": ["expr1", "expr2"]`  
  - **Sonra:** `"filter": "expr"` (tek string). Geriye dönük uyumluluk için **tek elemanlı dizi** hâlâ desteklenir ve o tek filter string olarak işlenir.  
  - Dizide birden fazla eleman artık desteklenmiyor; eskiden pratikte yalnızca ilk eleman kullanılıyordu; artık API açıkça tek string.

### 4. GetInstances Binding (çalışma zamanı)

- **GetInstancesBinding.Filter** — tip `string[]?` iken artık `string?`. Remote invoker’lar ve gateway’ler query string’i tek `filter` parametresi ile oluşturur.

### 5. Domain / altyapı

- **IInstanceRepository** — Filter alan overload’lar `GetPagedResultsAsync` ve `GetPagedResultsWithGroupsAsync` artık `string[]? filters` yerine `string? filter` alıyor.
- **FilterFormatDetector** — `DetectFormat(string[]?)` overload’u kaldırıldı; `CombineFilters` ve `ConvertLegacyToGraphQL` artık `string?` (tek filter) alıyor.
- **UnifiedFilterService.ApplyFilters** — parametre `string[]? filters` iken artık `string? filter`.
- **PostgreSqlJsonFilterService.ApplyJsonFilters** — parametre `string[] filters` iken artık `string? filter`.
- **FilterSpecification&lt;T&gt;** — constructor `(string[]? filters, ...)` iken artık `(string? filter, ...)`.
- **InstanceFilterSpecification** — constructor `(string[]? filters)` iken artık `(string? filter)`.
- **InputValidator** — `ValidateFilters(string? filter)` overload’u eklendi; mevcut `ValidateFilters(string[]? filters)` dahili kullanım için duruyor.

## Geçiş

### İstemciler (HTTP)

- Birden fazla `filter` query parametresi gönderiyorsanız, mantığı **tek** bir filter string’te toplayın (ör. `and`/`or` düğümleri içeren tek GraphQL tarzı JSON veya backend’in desteklediği tek legacy string).
- Örnek: `?filter={"a":"eq:1"}&filter={"b":"eq:2"}` yerine tek filter gönderin, örn.  
  `?filter={"and":[{"attributes":{"a":{"eq":"1"}}},{"attributes":{"b":{"eq":"2"}}}]}` (sözdizimi API sözleşmenize göre değişebilir).

### Workflow / task tanımları

- Task config’te diziyi tek string’e geçirin:
  - **Önce:** `"filter": ["status=Active", "flow=my-flow"]`
  - **Sonra:** `"filter": "status=Active"` (veya tek birleşik ifade).  
  Birden fazla koşul için tüm koşulu ifade eden tek bir GraphQL tarzı JSON string kullanın (örn. `and`/`or` ile).
- **SetFilter** çağıran tüm kodları `string[]?` yerine `string?` geçecek şekilde güncelleyin.

### GetInstanceListInput / repository / filter servislerini kullanan kod

- Filter için tek bir `string?` (veya `null`) geçirin; `string[]?` kullanmayın.
- Filter dizisi oluşturan veya üzerinde döngü kuran mantığı tek filter string oluşturacak / geçirecek şekilde güncelleyin.

## Geriye dönük uyumluluk (yalnızca task config)

- **GetInstancesTask** JSON yapılandırmasında `"filter"` hâlâ **dizi** verilmişse, uyumluluk için şöyle yorumlanır:
  - Tek eleman: o eleman tek filter string olarak kullanılır.
  - Birden fazla eleman: yalnızca **ilk** eleman filter string olarak kullanılır.  
  Mümkünse tek `"filter": "..."` string’e veya tek birleşik ifadeye geçin.

## Sürüm / tarih

Bu değişiklik, listelenen API ve tiplerde tek-string filter’ı getiren commit/sürüm itibarıyla geçerlidir. Kesin sürüm için repository geçmişine veya release notlarına bakın.
