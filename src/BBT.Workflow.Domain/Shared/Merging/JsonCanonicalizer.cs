using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// Instance-data kanonikleştirmesinde sayı yazım politikası.
/// </summary>
public enum JsonNumberPolicy
{
    /// <summary>
    /// Tarihsel davranış: <c>TryGetInt32</c> başarılıysa int, aksi hâlde <c>GetDouble</c>. int64
    /// aralığındaki tamsayılar ve 15+ haneli ondalıklar hassasiyet kaybeder; bu politika var olan
    /// satırlarla byte-parite için korunur ve varsayılandır.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Kayıpsız yazım: int64'e sığan tamsayılar birebir, aksi hâlde decimal'e sığan değerler düz
    /// (üstel gösterimsiz, trailing-zero'suz) ondalık olarak yazılır; hiçbirine sığmayan değerler
    /// <see cref="Legacy"/> gibi double'a düşer.
    ///
    /// <see cref="Legacy"/>'den sapan ÜÇ değer sınıfı vardır (flag açıldığındaki bir kerelik hash
    /// churn'ü tam bu üçüyle sınırlıdır):
    ///  1. <b>Hassasiyet kaybı olan değerler</b> — int64 aralığındaki tamsayılar, 2^53'ü aşan
    ///     tamsayılar ve 15+ haneli ondalıklar artık haneleri korunarak yazılır.
    ///  2. <b>E-gösterimli değerler</b> — kanonik form üstel gösterim İÇERMEZ, bu yüzden bugün
    ///     <c>1E-05</c> / <c>1E+18</c> olarak yazılan değerler düz ondalığa geçer. Kayıp yoktur,
    ///     metin (dolayısıyla hash) değişir (bilinçli; bkz. spec §1).
    ///  3. <b>Ondalık noktalı negatif sıfır</b> — <see cref="decimal"/>'de negatif sıfır yoktur, bu
    ///     yüzden <c>-0.0</c> ve <c>-0.00</c> <c>0</c> olarak yazılır (<see cref="Legacy"/>'de
    ///     <c>-0</c>). Yine tek-temsil kuralı, kayıp değil. TAMSAYI biçimi <c>-0</c> ETKİLENMEZ —
    ///     her iki politika da onu <c>0</c>'a çözer.
    ///
    /// <b>Sınır:</b> decimal 28-29 anlamlı hane taşır ve <c>TryGetDecimal</c> bunu aşan girdide
    /// YUVARLAYIP <c>true</c> döner. Yani bu politika, çok yüksek hassasiyetli değerler için kaybı
    /// AZALTIR (17 hane yerine 28), tümüyle ortadan kaldırmaz. decimal'in ARALIĞINI (~7.9e28) aşan
    /// değerlerde ise davranış <see cref="Legacy"/> ile birebir aynı kalır (double yolu).
    /// </summary>
    PreservePrecision = 1
}

/// <summary>
/// Merge + kanonikleştirme + veri hash'ini TEK yazım geçişinde üretir (B9). Çıktı, eski
/// Merge→NormalizedJson→ComputeDataHash zinciriyle BYTE-parite hedefler; parite
/// <c>JsonCanonicalizerParityTests</c>'teki oracle korpusuyla pinlidir.
///
/// Bu sınıf, TDD/parite-oracle sürecinde keşfedilen iki İMZASIZ tuzağı (plan metninde
/// belgelenmemişti) birebir replike eder — legacy yol, obje+obje kökte, HER İKİ tarafı da
/// baştan <see cref="System.Dynamic.ExpandoObject"/>/<c>List&lt;object?&gt;</c>/primitive ağacına
/// deserialize eder (bkz. <c>JsonElementMergeStrategy</c> + <c>ExpandoObjectJsonConverter</c>),
/// SONRA birleşik ağacı TEK SEFERDE geri serialize eder. Bu iki geçiş şu YAN ETKİLERİ üretir ve
/// merge SONUCUNUN HER KÖŞESİNE uygulanır (yalnız delta'dan gelen değerlere değil — hedefte
/// dokunulmamış değerlere de):
///  1. Sayılar <c>ExpandoObjectJsonConverter.ReadValue</c>'nun
///     <c>reader.TryGetInt32(out var i) ? i : reader.GetDouble()</c> kuralıyla YENİDEN biçimlenir
///     — ham lexical metin (1.0, 1e5, -0, 0.10) KORUNMAZ; "1", "100000", "0", "0.1" olur. Bu
///     merdiven artık <see cref="JsonNumberPolicy.Legacy"/>'ye (varsayılan) bağlıdır;
///     <see cref="JsonNumberPolicy.PreservePrecision"/> altında sayı yazımı int64/decimal
///     üzerinden kayıpsızdır (bkz. <see cref="JsonNumberPolicy"/>) — parite garantisi yalnız
///     <see cref="JsonNumberPolicy.Legacy"/> için geçerlidir.
///  2. Obje anahtarları <see cref="JsonNamingPolicy.CamelCase"/> ile dönüştürülür
///     (<c>ExpandoObjectJsonConverter.Write</c>'ın <c>DictionaryKeyPolicy</c> önko şulu) —
///     çakışma olursa (örn. "Z" ve "z" aynı objede) SON (iterasyon sırasına göre) kazanır, birebir
///     o converter'ın "last-wins" ön-normalize adımı gibi.
/// Bu iki dönüşüm SIRALAMA/DELTA kararlarından TAMAMEN bağımsızdır — merge kararı hangi tarafın
/// değeri kazanacağını seçer, dönüşüm kazanan değere (hangi taraftan gelmiş olursa olsun) uygulanır.
///
/// Kurallar (eski koddan birebir, ObjectMerger/ExpandoObjectMergeStrategy/CollectionMergeStrategy
/// okunarak doğrulandı):
///  - obje+obje: anahtar bazında derin merge; delta anahtarı kazanır.
///  - delta değeri JSON null VE anahtar zaten hedefte var ⇒ hedef DEĞİŞMEZ (silinmez, null da
///    olmaz) — <c>ObjectMerger.MergeValues</c>'un <c>if (source == null) return target;</c>
///    kısa devresi. Anahtar yalnız delta'da varsa (hedefte yok) null DOĞRUDAN yazılır.
///  - hedef değeri null VE delta doldurulmuşsa ⇒ delta kazanır (<c>if (target == null) return
///    source;</c>).
///  - dizi+dizi: delta dizisi TÜMÜYLE yerine geçer (hedef dizinin elemanlarıyla birleşmez).
///  - tip çakışması (obje/dizi/leaf karışık): delta kazanır, hedef alt-ağacı bütünüyle atılır.
///  - kanonik yazım: obje anahtarları (camelCase sonrası) StringComparer.Ordinal sıralı; diziler
///    pozisyonel; encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping (eski NormalizeJson
///    options'ı birebir).
///  - hash: SHA1, lowercase hex (InstanceData.ComputeDataHash ile aynı — PlanAppend dedupe'u buna
///    bağlı; <c>JsonCanonicalizerParityTests.DataHash_MatchesRealComputeDataHash</c> gerçek metotla
///    çapraz doğrular).
///
/// Kapsam notu: production çağıranı (PlanAppend) baz/delta'yı HER ZAMAN JSON obje olarak sağlar
/// (head yoksa bile base = "{}"). Kök obje+obje DEĞİLSE (array/leaf kök) legacy yol Expando
/// dönüşümüne HİÇ girmez — ham kazanan JsonElement'i (yalnız NormalizeJson'ın anahtar SIRALAMASIYLA,
/// camelCase/sayı-yeniden-biçimleme OLMADAN) döndürür; bu dal test korpusunca hiç tetiklenmez ama
/// savunmacı olarak <see cref="WriteSortedRaw"/> ile o davranış ayrıca korunur.
/// </summary>
public static class JsonCanonicalizer
{
    public readonly record struct CanonicalResult(string NormalizedJson, string DataHash);

    /// <summary>
    /// Merge + kanonikleştirme + hash. <paramref name="numberPolicy"/> yalnız sayı yazımını etkiler;
    /// varsayılan <see cref="JsonNumberPolicy.Legacy"/> bugünkü byte-parite çıktısını korur.
    /// </summary>
    public static CanonicalResult MergeAndCanonicalize(
        JsonElement baseDoc,
        JsonElement delta,
        JsonNumberPolicy numberPolicy = JsonNumberPolicy.Legacy)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            if (baseDoc.ValueKind == JsonValueKind.Object && delta.ValueKind == JsonValueKind.Object)
            {
                var merged = MergeObjects(baseDoc, delta);
                WriteObjectLevel(writer, merged, numberPolicy);
            }
            else
            {
                // Unreached by the domain's real usage (PlanAppend always passes objects) — kept
                // for defensiveness, mirrors legacy's raw (un-Expando'd) passthrough + sort-only.
                WriteSortedRaw(writer, delta);
            }
        }
        var bytes = buffer.WrittenSpan;
        var normalized = Encoding.UTF8.GetString(bytes);
        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        return new CanonicalResult(normalized, hash);
    }

    // ---- Merge phase: operates on ORIGINAL (pre-camelCase) keys, preserving the exact
    // first-occurrence-position / last-value-wins semantics that ExpandoObjectJsonConverter.Read
    // produces when it deserializes each side's raw JSON text into a dictionary. ----

    /// <summary>
    /// A merged property value: either a plain <see cref="JsonElement"/> (one side's subtree,
    /// copied through as-is — still needs the recursive camelCase+number TRANSFORM, but no further
    /// merge decision), or a nested <see cref="MergedObject"/> (both sides were objects at this key,
    /// so merging continued one level deeper).
    /// </summary>
    private abstract class MergedValue;

    private sealed class PassThrough(JsonElement element) : MergedValue
    {
        public JsonElement Element { get; } = element;
    }

    private sealed class MergedObject(List<(string Key, MergedValue Value)> properties) : MergedValue
    {
        public List<(string Key, MergedValue Value)> Properties { get; } = properties;
    }

    private static MergedObject MergeObjects(JsonElement target, JsonElement source)
    {
        var (targetOrder, targetValues) = DistinctPropertiesInOrder(target);
        var (sourceOrder, sourceValues) = DistinctPropertiesInOrder(source);

        var merged = new List<(string Key, MergedValue Value)>(targetOrder.Count + sourceOrder.Count);
        foreach (var key in targetOrder)
        {
            var tv = targetValues[key];
            merged.Add((key, sourceValues.TryGetValue(key, out var sv)
                ? MergeValue(tv, sv)
                : new PassThrough(tv)));
        }
        foreach (var key in sourceOrder)
        {
            if (targetValues.ContainsKey(key)) continue; // already handled via the target-order pass
            merged.Add((key, new PassThrough(sourceValues[key])));
        }
        return new MergedObject(merged);
    }

    private static MergedValue MergeValue(JsonElement target, JsonElement source)
    {
        // ObjectMerger.MergeValues: null-source keeps target; null-target lets source win.
        if (source.ValueKind == JsonValueKind.Null) return new PassThrough(target);
        if (target.ValueKind == JsonValueKind.Null) return new PassThrough(source);

        if (target.ValueKind == JsonValueKind.Object && source.ValueKind == JsonValueKind.Object)
            return MergeObjects(target, source);

        // Array+array (CollectionMergeStrategy: whole-array replace) and any type mismatch
        // (DefaultMergeStrategy) both resolve to "source wins, target subtree discarded".
        return new PassThrough(source);
    }

    /// <summary>
    /// Mirrors the dictionary-assignment semantics of <c>ExpandoObjectJsonConverter.Read</c>:
    /// walking an object's properties in document order and doing <c>dictionary[name] = value</c>
    /// for each — a repeated key keeps its FIRST position but ends up holding its LAST value.
    /// </summary>
    private static (List<string> Order, Dictionary<string, JsonElement> Values) DistinctPropertiesInOrder(
        JsonElement obj)
    {
        var order = new List<string>();
        var values = new Dictionary<string, JsonElement>();
        foreach (var prop in obj.EnumerateObject())
        {
            if (!values.ContainsKey(prop.Name)) order.Add(prop.Name);
            values[prop.Name] = prop.Value;
        }
        return (order, values);
    }

    // ---- Write phase: camelCase every object's keys (collision ⇒ last iteration-order entry
    // wins, mirroring ExpandoObjectJsonConverter.Write's DictionaryKeyPolicy pre-normalize step),
    // then sort ordinally (mirrors NormalizeJson's final pass) and emit. ----

    private static void WriteObjectLevel(Utf8JsonWriter writer, MergedObject obj, JsonNumberPolicy policy)
    {
        var byCamelKey = new Dictionary<string, MergedValue>(StringComparer.Ordinal);
        foreach (var (key, value) in obj.Properties)
        {
            var camelKey = JsonNamingPolicy.CamelCase.ConvertName(key);
            byCamelKey[camelKey] = value; // overwrite ⇒ last (by iteration order) wins
        }

        writer.WriteStartObject();
        foreach (var camelKey in byCamelKey.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.WritePropertyName(camelKey);
            WriteMergedValue(writer, byCamelKey[camelKey], policy);
        }
        writer.WriteEndObject();
    }

    private static void WriteMergedValue(Utf8JsonWriter writer, MergedValue value, JsonNumberPolicy policy)
    {
        switch (value)
        {
            case MergedObject nested:
                WriteObjectLevel(writer, nested, policy);
                break;
            case PassThrough passThrough:
                TransformAndWrite(writer, passThrough.Element, policy);
                break;
        }
    }

    /// <summary>
    /// Pure recursive transform for a subtree that was copied through from exactly one side
    /// (untouched target key, new delta key, or a whole array/object that a type-mismatch or
    /// array-replace decision handed wholesale to one side). No further merge decisions are made
    /// here — only the two universal side effects of the legacy Expando round-trip: object keys get
    /// camelCased (using the subtree's OWN natural document order for collision resolution) and
    /// numbers get reformatted according to <paramref name="policy"/> (see
    /// <see cref="WriteNumber"/>: <see cref="JsonNumberPolicy.Legacy"/> keeps the historical
    /// TryGetInt32-else-GetDouble ladder, <see cref="JsonNumberPolicy.PreservePrecision"/> writes
    /// int64/decimal losslessly).
    /// </summary>
    private static void TransformAndWrite(Utf8JsonWriter writer, JsonElement element, JsonNumberPolicy policy)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObjectLevel(writer, ToPassThroughObject(element), policy);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) TransformAndWrite(writer, item, policy);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, element, policy);
                break;
            default:
                element.WriteTo(writer); // string / true / false / null — passthrough
                break;
        }
    }

    /// <summary>
    /// Trailing-zero'suz, üstel gösterimsiz decimal formatı. decimal en fazla 28 ondalık basamak
    /// taşır, bu yüzden 28 '#' hiçbir hane kaybetmez.
    /// </summary>
    private const string PlainDecimalFormat = "0.############################";

    /// <summary>
    /// Sayı yazımı. <see cref="JsonNumberPolicy.Legacy"/> tarihsel merdiveni birebir korur.
    /// <see cref="JsonNumberPolicy.PreservePrecision"/> önce int64, sonra decimal dener; decimal'i
    /// üstel gösterimsiz ve trailing-zero'suz sabit bir formatla yazar (kanonik form), böylece
    /// 1.0 → 1 ve 2.50 → 2.5 tarihsel çıktıyla aynı kalır. Hiçbirine sığmayan değer (decimal
    /// aralığı dışı, ör. 1e40) tarihsel double yoluna düşer.
    /// </summary>
    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element, JsonNumberPolicy policy)
    {
        if (policy == JsonNumberPolicy.Legacy)
        {
            if (element.TryGetInt32(out var legacyInt)) writer.WriteNumberValue(legacyInt);
            else writer.WriteNumberValue(element.GetDouble());
            return;
        }

        if (element.TryGetInt64(out var exactInt))
        {
            writer.WriteNumberValue(exactInt);
            return;
        }

        if (element.TryGetDecimal(out var exactDecimal))
        {
            // Trailing zero'lar düşer (2.50 → 2.5), üstel gösterim ASLA kullanılmaz (0.00001 →
            // 0.00001), kültür sabittir. WriteRawValue: metni sayı token'ı olarak yazar.
            writer.WriteRawValue(
                exactDecimal.ToString(PlainDecimalFormat, CultureInfo.InvariantCulture),
                skipInputValidation: false);
            return;
        }

        writer.WriteNumberValue(element.GetDouble());
    }

    private static MergedObject ToPassThroughObject(JsonElement obj)
    {
        var (order, values) = DistinctPropertiesInOrder(obj);
        var properties = new List<(string Key, MergedValue Value)>(order.Count);
        foreach (var key in order)
            properties.Add((key, new PassThrough(values[key])));
        return new MergedObject(properties);
    }

    /// <summary>
    /// Defensive fallback for a non-object root (never exercised by the domain's real callers):
    /// legacy's JsonElementMergeStrategy hands back the winning side's RAW JsonElement with no
    /// Expando round-trip at all, so only NormalizeJson's later recursive key-sort applies — no
    /// camelCase, no number reformatting. <see cref="JsonNumberPolicy"/> therefore does NOT reach
    /// this branch: raw number tokens are emitted verbatim under both policies (verbatim is already
    /// lossless, so PreservePrecision has nothing to fix here).
    /// </summary>
    private static void WriteSortedRaw(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(p.Name);
                    WriteSortedRaw(writer, p.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteSortedRaw(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
