---
name: add-query-param-filter
description: Use when adding query parameter filters to a monitor list endpoint. Covers the 3-class pattern (FilterInput / FilterDescriptor / Filter), bracket-notation binding with [eq]/[contains] operators, conflict validation, type-discriminated 400 validation, and in-memory LINQ apply — all derived from the component filter implementation.
---

# Skill: Monitor List Endpoint — Query Param Filter Ekleme

Bir monitor list endpoint'ine query param filter eklerken kullanılır.
Referans implementasyon: `components?type=` endpoint'i.
Davranış rehberi: `docs/features/query-param-filter.md`

---

## Operatör Modeli

### Lookup mode vs Filter mode

| Parametre | Mod | Dönen tip |
|---|---|---|
| `?key=abc` (plain) | Lookup — tek kayıt exact | `MonitorComponentDetailResponse` (pagination yok) |
| `?key[eq]=abc` | Filter mode — exact | `MonitorPagedResponse`, 0–1 item |
| `?key[contains]=order` | Filter mode — partial | `MonitorPagedResponse`, 0–N item |
| `?name[eq/contains]=...` | Filter mode | `MonitorPagedResponse` |
| `?flowVersion[eq/contains]=...` | Filter mode | `MonitorPagedResponse` |
| `?version[eq/contains]=...` | Filter mode | `MonitorPagedResponse` |
| `?tags[contains]=...` | Filter mode | `MonitorPagedResponse` |
| `definitionType`, `renderer`, `display`, `scope` | Plain exact — operatör yok | `MonitorPagedResponse` |
| `createdAt[gte/lte]`, `modifiedAt[gte/lte]` | Range — değişmez | `MonitorPagedResponse` |

**Kural:** Aynı field için hem `[eq]` hem `[contains]` → `400 ValidationProblemDetails`.

---

## Karar ağacı

```
Filtreler tüm tip/senaryolarda aynı mı?
  Evet → FilterInput + Filter yeterli; Descriptor gerekmez
  Hayır (type'a göre izinli alanlar değişiyor) → 3 sınıfın tamamı
```

---

## 3-Sınıf Mimari

| Sınıf | Konum | Görev |
|---|---|---|
| `Monitor{X}FilterInput` | `Application/{X}/Filters/` | Nullable field'lar, `IsEmpty`, `SetFields()` |
| `{X}FilterDescriptor` | `Application/{X}/Filters/` | Type → izinli alanlar registry (opsiyonel) |
| `Monitor{X}Filter` | `Application/{X}/Filters/` | Pure static `Apply(items, filter)` |

---

## Adım 1 — `Monitor{X}FilterInput`

Her filter field **nullable**. `SetFields()` — yalnızca dolu field'ların **canonical** adlarını yield eder.
`[eq]` ve `[contains]` varyantları aynı canonical adı yield eder (ör. `FlowVersionEq` ve `FlowVersionContains` → `"flowVersion"`).

```csharp
namespace BBT.Workflow.Monitor.{X}.Filters;

/// <summary>Optional filter parameters for {X} list endpoint.</summary>
public sealed class Monitor{X}FilterInput
{
    /// <summary>Lower bound (inclusive).</summary>
    public DateTime? CreatedAtGte { get; set; }

    /// <summary>Upper bound (inclusive).</summary>
    public DateTime? CreatedAtLte { get; set; }

    /// <summary>Tag list-contains filter (case-insensitive).</summary>
    public string? TagsContains { get; set; }

    /// <summary>Flow-stream version exact-match filter.</summary>
    public string? FlowVersionEq { get; set; }

    /// <summary>Flow-stream version contains filter.</summary>
    public string? FlowVersionContains { get; set; }

    /// <summary>Component key exact-match filter.</summary>
    public string? KeyEq { get; set; }

    /// <summary>Component key contains filter.</summary>
    public string? KeyContains { get; set; }

    // Plain exact-match fields (no operators):
    public string? DefinitionType { get; set; }
    public string? Renderer { get; set; }
    public string? Scope { get; set; }

    /// <summary>Returns true when no filter field has been set.</summary>
    public bool IsEmpty => !SetFields().Any();

    /// <summary>Returns canonical field names of all set properties.</summary>
    internal IEnumerable<string> SetFields()
    {
        if (CreatedAtGte.HasValue)           yield return "createdAt";
        if (CreatedAtLte.HasValue)           yield return "createdAt";
        if (TagsContains is not null)        yield return "tags";
        if (FlowVersionEq is not null)       yield return "flowVersion";
        if (FlowVersionContains is not null) yield return "flowVersion";
        if (KeyEq is not null)               yield return "key";
        if (KeyContains is not null)         yield return "key";
        if (DefinitionType is not null)      yield return "definitionType";
        if (Renderer is not null)            yield return "renderer";
        if (Scope is not null)               yield return "scope";
    }
}
```

**Kural:** `SetFields()` property adı değil **canonical** (query) adı döner. Hem `Eq` hem `Contains` varyantı aynı canonical adı verir; descriptor tek "flowVersion" kontrolü yapar.

---

## Adım 2 — `{X}FilterDescriptor` (type-discriminated ise)

```csharp
namespace BBT.Workflow.Monitor.{X}.Filters;

internal static class {X}FilterDescriptor
{
    private static readonly IReadOnlySet<string> CommonFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "createdAt", "modifiedAt", "tags", "flowVersion", "key", "version" };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> TypeSpecificFields =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [MonitorComponentTypes.Views]      = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "display", "renderer", "definitionType" },
            [MonitorComponentTypes.Functions]  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scope" },
            [MonitorComponentTypes.Mappings]   = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" },
            // ...
        };

    public static IReadOnlySet<string> AllowedFor(string type)
    {
        var specific = TypeSpecificFields.GetValueOrDefault(type, new HashSet<string>());
        return CommonFields.Union(specific, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> FindDisallowed(string type, Monitor{X}FilterInput filter)
    {
        var allowed = AllowedFor(type);
        return filter.SetFields()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !allowed.Contains(f))
            .ToList();
    }
}
```

---

## Adım 3 — `Monitor{X}Filter`

In-memory, pure static. Items cache/DB'den yüklendi; projection sonrası, pagination öncesi uygulanır.

```csharp
internal static class Monitor{X}Filter
{
    public static IEnumerable<Monitor{X}Item> Apply(
        IEnumerable<Monitor{X}Item> items,
        Monitor{X}FilterInput filter)
    {
        if (filter.CreatedAtGte.HasValue)
            items = items.Where(x => x.CreatedAt.HasValue && x.CreatedAt.Value >= filter.CreatedAtGte.Value);

        if (filter.TagsContains is not null)
            items = items.Where(x => x.Tags is not null &&
                x.Tags.Contains(filter.TagsContains, StringComparer.OrdinalIgnoreCase));

        // [eq] — exact match
        if (filter.FlowVersionEq is not null)
            items = items.Where(x => string.Equals(x.FlowVersion, filter.FlowVersionEq, StringComparison.OrdinalIgnoreCase));

        // [contains] — partial match
        if (filter.FlowVersionContains is not null)
            items = items.Where(x => x.FlowVersion is not null &&
                x.FlowVersion.Contains(filter.FlowVersionContains, StringComparison.OrdinalIgnoreCase));

        // plain exact (no operators):
        if (filter.Renderer is not null)
            items = items.Where(x => string.Equals(x.Renderer, filter.Renderer, StringComparison.OrdinalIgnoreCase));

        // JsonElement type discriminator:
        if (filter.DefinitionType is not null)
            items = items.Where(x => MatchStringElement(x.Type, filter.DefinitionType));

        return items;
    }

    private static bool MatchStringElement(JsonElement? el, string value)
    {
        if (el is null) return false;
        var e = el.Value;
        return e.ValueKind == JsonValueKind.String &&
               string.Equals(e.GetString(), value, StringComparison.OrdinalIgnoreCase);
    }
}
```

**Kural:** Tüm string karşılaştırmaları `OrdinalIgnoreCase`. `JsonElement` için önce `ValueKind == String` kontrol et.

---

## Adım 4 — Controller Wiring

### Bracket notation binding

```csharp
// [eq] / [contains] operatörlü field'lar:
[FromQuery(Name = "flowVersion[eq]")]       string? flowVersionEq       = null,
[FromQuery(Name = "flowVersion[contains]")] string? flowVersionContains = null,
[FromQuery(Name = "key[eq]")]               string? keyEq               = null,
[FromQuery(Name = "key[contains]")]         string? keyContains         = null,
[FromQuery(Name = "tags[contains]")]        string? tagsContains        = null,

// Plain field'lar (operatörsüz):
[FromQuery] string? definitionType = null,
[FromQuery] string? renderer       = null,
```

### Conflict validation ([eq] + [contains] aynı anda → 400)

```csharp
var conflicts = new List<string>();
if (flowVersionEq is not null && flowVersionContains is not null) conflicts.Add("flowVersion");
if (keyEq is not null && keyContains is not null)                 conflicts.Add("key");
// ... diğer [eq]/[contains] çiftleri
if (conflicts.Count > 0)
{
    var errors = conflicts.ToDictionary(
        f => f,
        f => new[] { $"Cannot use both '[eq]' and '[contains]' operators for '{f}'." });
    return BadRequest(new ValidationProblemDetails { Errors = errors });
}
```

### Type-discriminated validasyon (descriptor varsa)

```csharp
var filter = new Monitor{X}FilterInput
{
    FlowVersionEq       = string.IsNullOrWhiteSpace(flowVersionEq)       ? null : flowVersionEq.Trim(),
    FlowVersionContains = string.IsNullOrWhiteSpace(flowVersionContains) ? null : flowVersionContains.Trim(),
    Renderer            = string.IsNullOrWhiteSpace(renderer)            ? null : renderer.Trim(),
    // ...
};

var disallowed = {X}FilterDescriptor.FindDisallowed(input.Type, filter);
if (disallowed.Count > 0)
{
    var errors = disallowed.ToDictionary(
        f => f,
        f => new[] { $"'{f}' is not supported for type '{input.Type}'." });
    return BadRequest(new ValidationProblemDetails { Errors = errors });
}
```

`ProducesResponseType` ekle:
```csharp
[ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
```

### Key + pagination kuralı (key-only endpoint'ler için)

`key` plain lookup modundaysa `page`/`pageSize` reddedilmeli:

```csharp
if (input.Key is not null)
{
    var disallowed = new[] { "page", "pageSize" }
        .Where(p => HttpContext.Request.Query.ContainsKey(p))
        .ToList();
    if (disallowed.Count > 0)
    {
        var errors = disallowed.ToDictionary(
            p => p,
            p => new[] { $"'{p}' is not allowed when 'key' is provided." });
        return BadRequest(new ValidationProblemDetails { Errors = errors });
    }
    // tek kayıt döndür
}
```

---

## Adım 5 — Service Wiring

`MonitorGetXInput`'a `Filter` property ekle:

```csharp
/// <summary>Optional filter. Ignored when Key is provided.</summary>
public Monitor{X}FilterInput? Filter { get; set; }
```

Service'te projection sonrası, pagination öncesi uygula:

```csharp
var projected = source.Select(e => ProjectToItem(e));

var allItems = (input.Filter is not null && !input.Filter.IsEmpty)
    ? Monitor{X}Filter.Apply(projected, input.Filter).ToList()
    : projected.ToList();

var pagedItems = allItems.Skip((input.Page - 1) * input.PageSize).Take(input.PageSize).ToList();
```

---

## Adım 6 — Testler

`Application.Tests/{X}/` altında iki test dosyası:

- **`{X}FilterDescriptorTests.cs`** — `[Theory] [InlineData(...)]` ile tüm type'lar için common field'lar, type-specific field'lar, disallowed field'lar
- **`Monitor{X}FilterTests.cs`** — Her filter field için bir `[Fact]`, boş filter, combined filter

Her `[eq]`/`[contains]` çifti için ikişer test: exact match ve partial match. Testler infrastructure'a ihtiyaç duymaz; tüm objeler `new` ile oluşturulur.

---

## Adım 7 — Dokümantasyon (§7.1 zorunlu senkronizasyon)

Her yeni filtre parametresi için dört dosya güncellenir:
1. `endpoints/vnext-monitor.http` — yeni varyant örnekler + conflict 400 örneği
2. `endpoints/vnext-monitor-endpoints.postman_collection.json` — faz klasörüne ekle
3. `endpoints/vnext-monitor.postman_collection.json` — uygun senaryo klasörüne ekle (400 varsa `9-Negatif`'e de ekle)
4. `docs/features/monitoring-features.md` — filtre tablosu güncelle

---

## Checklist

- [ ] `Monitor{X}FilterInput` — `[eq]`/`[contains]` çiftleri, `IsEmpty`, `SetFields()` (canonical adlar, her çift aynı canonical adı yield eder)
- [ ] `{X}FilterDescriptor` — CommonFields + TypeSpecificFields; `"key"` ve `"version"` common'a dahil
- [ ] `Monitor{X}Filter.Apply` — her `[eq]` için `string.Equals(...OrdinalIgnoreCase)`, her `[contains]` için `.Contains(...OrdinalIgnoreCase)`, `JsonElement` field'lar için `ValueKind == String` guard
- [ ] Controller: bracket notation `[FromQuery(Name = "field[eq]")]` binding
- [ ] Controller: conflict validation — her `[eq]`/`[contains]` çifti için `conflicts.Add(...)`
- [ ] Controller: `FindDisallowed` → `ValidationProblemDetails` ile 400
- [ ] Controller: key-only mode'da `page`/`pageSize` reddi (`Request.Query.ContainsKey`)
- [ ] `[ProducesResponseType(typeof(ValidationProblemDetails), 400)]` eklendi
- [ ] `MonitorGetXInput`'a `Filter` property eklendi
- [ ] Service: `!filter.IsEmpty` kontrolü ile conditional apply
- [ ] Descriptor testleri (theory) + Filter testleri (fact per [eq]/[contains] çifti)
- [ ] §7.1 senkronizasyonu tamamlandı (http + 2 postman + features.md)

---

## Referans Dosyalar

| Dosya | İçerik |
|---|---|
| `docs/features/query-param-filter.md` | Tüm operatör davranış tablosu ve kurallar |
| `vnext/monitoring/.../Components/Filters/MonitorComponentFilterInput.cs` | FilterInput örneği |
| `vnext/monitoring/.../Components/Filters/ComponentFilterDescriptor.cs` | Descriptor örneği |
| `vnext/monitoring/.../Components/Filters/MonitorComponentFilter.cs` | Filter Apply örneği |
| `vnext/monitoring/.../Controllers/MonitorComponentController.cs` | Controller wiring (conflict check, disallowed check, key+page guard) |
| `vnext/test/.../Components/ComponentFilterDescriptorTests.cs` | Descriptor test örneği |
| `vnext/test/.../Components/MonitorComponentFilterTests.cs` | Filter test örneği |
