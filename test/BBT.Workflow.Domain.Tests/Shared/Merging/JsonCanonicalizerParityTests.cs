using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Workflow;
using BBT.Workflow.Instances;
using Xunit;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// Parity-oracle tests for <see cref="JsonCanonicalizer"/> (B9 core). The ORACLE — today's
/// multi-pass pipeline (<see cref="JsonData.Merge"/> → <see cref="JsonData.NormalizedJson"/> →
/// SHA1) — is always right. If a corpus or randomized case ever diverges, the canonicalizer is
/// adapted to match the oracle; the oracle itself and the corpus are never changed to "fix" a
/// failure.
/// </summary>
public class JsonCanonicalizerParityTests
{
    /// <summary>Eski yol, PlanAppend'in bugünkü akışının birebir kopyası (test-oracle).</summary>
    private static (string NormalizedJson, string Hash) Oracle(string baseJson, string deltaJson)
    {
        var merged = new JsonData(baseJson).Merge(new JsonData(deltaJson));
        var normalized = merged.NormalizedJson;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return (normalized, hash);
    }

    public static IEnumerable<object[]> Corpus()
    {
        // Kenar durumları: sayı biçimleri ham korunur; ordinal anahtar sıralaması; dizi REPLACE;
        // null-delta anahtar silmez; unicode/escape; derin iç içe; boş obje/dizi; case-sensitive anahtarlar.
        yield return new object[] { """{"a":1}""", """{"b":2}""" };
        yield return new object[] { """{"a":{"x":1,"y":[1,2]}}""", """{"a":{"y":[9],"z":null}}""" };
        yield return new object[] { """{"n1":1.0,"n2":1e5,"n3":-0,"n4":0.10}""", """{"n5":2.50}""" };
        yield return new object[] { """{"tr":"şğüİı","esc":"a\"b\\c"}""", """{"emoji":"🙂"}""" };
        yield return new object[] { """{"Z":1,"a":2,"A":3}""", """{"m":{"Z":1,"a":2}}""" };
        yield return new object[] { """{"deep":{"deep":{"deep":{"v":[{"k":1},{"k":2}]}}}}""", """{"deep":{"deep":{"deep":{"v":[]}}}}""" };
        yield return new object[] { """{}""", """{"first":true}""" };
        yield return new object[] { """{"keep":1}""", """{}""" };
        yield return new object[] { """{"arr":[{"a":1}]}""", """{"arr":[{"b":2},{"c":3}]}""" };
        yield return new object[] { """{"s":"1.0","n":1.0}""", """{"s2":"2","n2":2}""" };
        // Duplicate-key trap (documented in the plan): oracle's NormalizeJson catches the
        // ToDictionary throw on a duplicate key and falls back to the un-normalized, but still
        // merged, serialize. The canonicalizer must reproduce whatever that fallback actually is.
        yield return new object[] { """{"a":1,"a":2}""", """{"b":3}""" };
        // Empirically-discovered trap (not in the plan's documented list): a delta value of null
        // on a KEY THAT ALREADY EXISTS in base does NOT null out the key — ObjectMerger.MergeValues
        // short-circuits on `source == null` and returns the target unchanged. Only a null delta on
        // a key absent from base (covered above) actually writes null.
        yield return new object[] { """{"keep":{"x":1},"lit":5}""", """{"keep":null,"lit":null}""" };
        // Coordinator-requested additions (spec review, before Task 3 wires persistence):
        // (1) Long-integral beyond int32 — 3000000000 exceeds Int32.MaxValue but fits Int64/double
        // exactly; 9007199254740993 is 2^53+1, beyond double's exact-integer range, so it exercises
        // double-rounding on top of the TryGetInt32-else-GetDouble reformat. Oracle decides the
        // exact digits.
        yield return new object[] { """{"big1":3000000000,"lit":1}""", """{"big2":9007199254740993}""" };
        // (2) Signed/uppercase exponents + fraction-exponent lexical forms.
        yield return new object[] { """{"e1":1e-5,"e2":1E+5,"e3":1.5e3}""", """{"x":1}""" };
        // (3) High-precision decimal beyond double's ~15-17 significant digits — precision loss is
        // expected; oracle's actual output (whatever double rounds it to) is what must be matched.
        yield return new object[] { """{"hp":1.2345678901234567890123}""", """{"x":1}""" };
        // (4) Unicode object keys, incl. a camelCase-policy interaction check: what does
        // JsonNamingPolicy.CamelCase do to a leading 'İ' (Turkish dotted capital I)?
        yield return new object[] { """{"şğü":1,"ölçü":{"İç":2}}""", """{"İstanbul":3}""" };
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CanonicalizeMerge_ByteParity_WithLegacyPipeline(string baseJson, string deltaJson)
    {
        var expected = Oracle(baseJson, deltaJson);
        var actual = JsonCanonicalizer.MergeAndCanonicalize(
            new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement);

        Assert.Equal(expected.NormalizedJson, actual.NormalizedJson); // BYTE-parite
        Assert.Equal(expected.Hash, actual.DataHash);
    }

    /// <summary>
    /// Cross-check against the real production hash formula. InternalsVisibleTo grants
    /// Domain.Tests access to the internal <see cref="InstanceData.ComputeDataHash"/>, so this
    /// calls the actual method rather than only a locally re-implemented SHA1 formula.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void DataHash_MatchesRealComputeDataHash(string baseJson, string deltaJson)
    {
        var merged = new JsonData(baseJson).Merge(new JsonData(deltaJson));
        var realHash = InstanceData.ComputeDataHash(merged);

        var actual = JsonCanonicalizer.MergeAndCanonicalize(
            new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement);

        Assert.Equal(realHash, actual.DataHash);
    }

    [Fact]
    public void RandomizedParity_SmallGeneratedDocuments()
    {
        // Deterministik tohumlu üretici: 200 rastgele (base, delta) çifti — obje/dizi/sayı/string/null
        // karışımı, max derinlik 4. Her biri için oracle == canonicalizer.
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var baseJson = RandomJson(rng, depth: 0, NumberPool.All);
            var deltaJson = RandomJson(rng, depth: 0, NumberPool.All);
            var expected = Oracle(baseJson, deltaJson);
            var actual = JsonCanonicalizer.MergeAndCanonicalize(
                new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement);
            Assert.Equal(expected.NormalizedJson, actual.NormalizedJson);
        }
    }

    /// <summary>
    /// Sıradan havuz: <see cref="JsonNumberPolicy.PreservePrecision"/> kanonik formu
    /// GENİŞLETMEMELİ. <see cref="NumberPool.Ordinary"/> yalnız stil 0/1/2 üretir ve hiçbir
    /// lexical formda negatif sıfır üretmez (bkz. <see cref="NumberPool"/> DİKKAT notu) — bu ikisi
    /// sağlanınca iki politika birebir aynı metni yazmalıdır.
    /// </summary>
    [Fact]
    public void RandomizedOrdinaryValues_PreservePrecision_MatchesLegacy()
    {
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var baseJson = RandomJson(rng, depth: 0, NumberPool.Ordinary);
            var deltaJson = RandomJson(rng, depth: 0, NumberPool.Ordinary);
            var legacy = JsonCanonicalizer.MergeAndCanonicalize(
                new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement,
                JsonNumberPolicy.Legacy).NormalizedJson;
            var preserved = JsonCanonicalizer.MergeAndCanonicalize(
                new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement,
                JsonNumberPolicy.PreservePrecision).NormalizedJson;
            Assert.Equal(legacy, preserved);
        }
    }

    // RandomJson: yerel statik üretici — obje kökü; anahtarlar {a..h, A..D}; değerler:
    // int/decimal-metin ("1.50")/string/bool/null/alt-obje/dizi. Kod JSON'u TEXT olarak üretir
    // (obje serialize etmez) — böylece 1.0/1e2 gibi lexical sayı biçimleri gerçekten ortaya çıkar.

    /// <summary>
    /// <see cref="RandomDecimalLexical"/>'in üretebileceği sayı havuzu.
    /// <see cref="Ordinary"/>: yalnız stil 0,1,2 (1.50 / 1e2 / 1.0) — <see cref="JsonNumberPolicy.Legacy"/>
    /// ve <see cref="JsonNumberPolicy.PreservePrecision"/> iki modda AYNI metni üretir.
    /// <see cref="All"/>: bugünkü 6 stil (parite testleri Legacy modunda bunu kullanmaya devam eder).
    ///
    /// DİKKAT (Task 1 bulgusu + spec incelemesi): ondalıklı negatif sıfır iki modda FARKLI yazılır
    /// (Legacy "-0", PreservePrecision "0" — decimal negatif sıfır taşımaz; bkz. spec §1 ikinci
    /// sonuç) ve mevcut üreticide DÖRT stille üretilebiliyordu: 0 ("-0.00"), 2 ("-0.0"), 1 ("-0e0"),
    /// 4 ("-0E+5"). <see cref="Ordinary"/> havuzu bu değerlerin HİÇBİRİNİ üretmemelidir; tek noktadan
    /// garanti etmenin en sağlam yolu, negatif işaretin yalnız sıfır-olmayan bir değere
    /// uygulanmasıdır — üretilen metnin sayısal değeri sıfırsa işaret asla "-" olmasın.
    /// <see cref="RandomDecimalLexical"/> bunu STİL BAZLI patch olarak değil, her stil için önce
    /// büyüklüğün (magnitude) sıfır olup olmadığını hesaplayıp işareti ona göre seçerek yapar —
    /// böylece hem <see cref="Ordinary"/> hem (savunmacı olarak) <see cref="All"/> için geçerlidir.
    /// Aksi hâlde aşağıdaki invaryant testi tohuma bağlı olarak kırmızıya döner (seed 42 / 200
    /// çiftte isabet neredeyse kesin).
    /// </summary>
    private enum NumberPool { Ordinary, All }

    private static readonly string[] KeyPool =
    [
        "a", "b", "c", "d", "e", "f", "g", "h",
        "A", "B", "C", "D"
    ];

    private static string RandomJson(Random rng, int depth, NumberPool pool)
    {
        // Root ve tüm iç içe obje seviyeleri obje köküdür (görev: "obje root").
        return RandomObject(rng, depth, pool);
    }

    private static string RandomObject(Random rng, int depth, NumberPool pool)
    {
        var keyCount = rng.Next(0, 5); // 0..4 anahtar (boş obje dahil)
        var usedKeys = new HashSet<string>();
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        for (var i = 0; i < keyCount; i++)
        {
            var key = KeyPool[rng.Next(KeyPool.Length)];
            if (!usedKeys.Add(key))
                continue; // aynı anahtarı iki kez seçme (kasıtlı duplicate ayrı bir testte ele alınıyor)

            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(key).Append("\":");
            sb.Append(RandomValue(rng, depth + 1, pool));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string RandomArray(Random rng, int depth, NumberPool pool)
    {
        var itemCount = rng.Next(0, 4); // 0..3 eleman (boş dizi dahil)
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < itemCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(RandomValue(rng, depth + 1, pool));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string RandomValue(Random rng, int depth, NumberPool pool)
    {
        // Derinlik sınırı 4 — sınıra ulaşınca yalnız leaf üret (obje/dizi kapat).
        var maxKind = depth >= 4 ? 4 : 6;
        var kind = rng.Next(0, maxKind + 1);
        return kind switch
        {
            0 => RandomInt(rng).ToString(CultureInfo.InvariantCulture),
            1 => RandomDecimalLexical(rng, pool),
            2 => "\"" + RandomString(rng) + "\"",
            3 => rng.Next(0, 2) == 0 ? "true" : "false",
            4 => "null",
            5 => RandomObject(rng, depth, pool),
            _ => RandomArray(rng, depth, pool),
        };
    }

    private static int RandomInt(Random rng) => rng.Next(-1000, 1000);

    /// <summary>
    /// Decimal-lexical sayı metni üretir — TEXT olarak yazılır ("1.50", "1e2", "-0.0" gibi) ki
    /// ham lexical biçimler (object serialize edilseydi kaybolacak biçimler) gerçekten oluşsun.
    /// Coordinator eklentisi: bazen int32 sınırını aşan tamsayılar, negatif/büyük-harf üstel
    /// biçimler ve 18+ hane hassasiyetli ondalıklar da üretilir (aynı tohum 42 ile) — tohum yeni
    /// bir sapma üretirse bu bir FIND'dir ve canonicalizer oracle'a göre uyarlanır.
    ///
    /// <paramref name="pool"/> = <see cref="NumberPool.Ordinary"/> ⇒ yalnız stil 0/1/2 seçilir.
    /// Her stilde işaret ("-") YALNIZ üretilen metnin sayısal büyüklüğü sıfırdan farklıysa
    /// uygulanır — negatif sıfırın hiçbir lexical formu (bkz. <see cref="NumberPool"/> DİKKAT notu)
    /// yapısal olarak üretilemez, stil bazlı patch değil tek bir kural.
    /// </summary>
    private static string RandomDecimalLexical(Random rng, NumberPool pool)
    {
        var intPart = rng.Next(0, 100);
        var style = pool == NumberPool.Ordinary ? rng.Next(0, 3) : rng.Next(0, 6);

        switch (style)
        {
            case 0: // "1.50" — büyüklük sıfır ⇔ intPart==0 VE kesir basamakları 0.
            {
                var frac = rng.Next(0, 100);
                var isZero = intPart == 0 && frac == 0;
                var sign = SignFor(rng, isZero);
                return $"{sign}{intPart}.{frac:D2}";
            }
            case 1: // "1e2" — büyüklük sıfır ⇔ intPart==0 (üs değeri etkilemez).
            {
                var exponent = rng.Next(0, 3);
                var sign = SignFor(rng, intPart == 0);
                return $"{sign}{intPart}e{exponent}";
            }
            case 2: // "1.0" — büyüklük sıfır ⇔ intPart==0.
            {
                var sign = SignFor(rng, intPart == 0);
                return $"{sign}{intPart}.0";
            }
            case 3: // int32'yi aşan tamsayı — asla sıfır büyüklük.
            {
                var sign = SignFor(rng, isZero: false);
                return $"{sign}{rng.NextInt64(3_000_000_000L, 9_007_199_254_740_995L)}";
            }
            case 4: // negatif / büyük-harf üstel biçim: "1e-5" / "1E+7" tarzı.
            {
                var sign = SignFor(rng, intPart == 0);
                var expChar = rng.Next(0, 2) == 0 ? "e" : "E";
                var expSign = rng.Next(0, 2) == 0 ? "-" : "+";
                return $"{sign}{intPart}{expChar}{expSign}{rng.Next(1, 10)}";
            }
            default: // 18+ hane hassasiyetli ondalık — double hassasiyetini aşar (kayıp beklenir).
            {
                var digits = string.Concat(Enumerable.Range(0, 20).Select(_ => (char)('0' + rng.Next(0, 10))));
                var isZero = intPart == 0 && digits.All(c => c == '0');
                var sign = SignFor(rng, isZero);
                return $"{sign}{intPart}.{digits}";
            }
        }
    }

    /// <summary>
    /// İşaret seçimi: büyüklük sıfırsa "-" ASLA seçilmez (negatif sıfırı yapısal olarak
    /// engelleyen tek nokta); aksi hâlde adil bir yazı-tura.
    /// </summary>
    private static string SignFor(Random rng, bool isZero) =>
        !isZero && rng.Next(0, 2) == 1 ? "-" : "";

    private static readonly string[] StringPool =
    [
        "x", "hello", "şğüİı", "a\"b\\c", "🙂", ""
    ];

    private static string RandomString(Random rng)
    {
        var raw = StringPool[rng.Next(StringPool.Length)];
        // JSON escape ki üretilen metin geçerli bir JSON string literali olsun.
        return JsonEncodedText.Encode(raw, JsonSerializerOptions.Default.Encoder).ToString();
    }
}
