using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Workflow;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared.Merging;
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
            var baseJson = RandomJson(rng, depth: 0);
            var deltaJson = RandomJson(rng, depth: 0);
            var expected = Oracle(baseJson, deltaJson);
            var actual = JsonCanonicalizer.MergeAndCanonicalize(
                new JsonData(baseJson).JsonElement, new JsonData(deltaJson).JsonElement);
            Assert.Equal(expected.NormalizedJson, actual.NormalizedJson);
        }
    }

    // RandomJson: yerel statik üretici — obje kökü; anahtarlar {a..h, A..D}; değerler:
    // int/decimal-metin ("1.50")/string/bool/null/alt-obje/dizi. Kod JSON'u TEXT olarak üretir
    // (obje serialize etmez) — böylece 1.0/1e2 gibi lexical sayı biçimleri gerçekten ortaya çıkar.

    private static readonly string[] KeyPool =
    [
        "a", "b", "c", "d", "e", "f", "g", "h",
        "A", "B", "C", "D"
    ];

    private static string RandomJson(Random rng, int depth)
    {
        // Root ve tüm iç içe obje seviyeleri obje köküdür (görev: "obje root").
        return RandomObject(rng, depth);
    }

    private static string RandomObject(Random rng, int depth)
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
            sb.Append(RandomValue(rng, depth + 1));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string RandomArray(Random rng, int depth)
    {
        var itemCount = rng.Next(0, 4); // 0..3 eleman (boş dizi dahil)
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < itemCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(RandomValue(rng, depth + 1));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string RandomValue(Random rng, int depth)
    {
        // Derinlik sınırı 4 — sınıra ulaşınca yalnız leaf üret (obje/dizi kapat).
        var maxKind = depth >= 4 ? 4 : 6;
        var kind = rng.Next(0, maxKind + 1);
        return kind switch
        {
            0 => RandomInt(rng).ToString(CultureInfo.InvariantCulture),
            1 => RandomDecimalLexical(rng),
            2 => "\"" + RandomString(rng) + "\"",
            3 => rng.Next(0, 2) == 0 ? "true" : "false",
            4 => "null",
            5 => RandomObject(rng, depth),
            _ => RandomArray(rng, depth),
        };
    }

    private static int RandomInt(Random rng) => rng.Next(-1000, 1000);

    /// <summary>
    /// Decimal-lexical sayı metni üretir — TEXT olarak yazılır ("1.50", "1e2", "-0.0" gibi) ki
    /// ham lexical biçimler (object serialize edilseydi kaybolacak biçimler) gerçekten oluşsun.
    /// </summary>
    private static string RandomDecimalLexical(Random rng)
    {
        var sign = rng.Next(0, 2) == 0 ? "" : "-";
        var intPart = rng.Next(0, 100);
        var style = rng.Next(0, 3);
        return style switch
        {
            0 => $"{sign}{intPart}.{rng.Next(0, 100):D2}", // "1.50"
            1 => $"{sign}{intPart}e{rng.Next(0, 3)}",       // "1e2"
            _ => $"{sign}{intPart}.0",                       // "1.0"
        };
    }

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
