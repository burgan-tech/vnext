using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// PreservePrecision modunun sözleşmesi: (1) hassasiyet kaybı olan değerler DÜZELİR,
/// (2) E-gösterimli değerler düz gösterime geçer (bilinçli — spec §1 düzeltmesi),
/// (3) SIRADAN değerlerde çıktı Legacy ile BİREBİR aynıdır (kanonik formu kazara
/// genişletmediğimizin bekçisi).
/// </summary>
public class JsonCanonicalizerNumberPolicyTests
{
    private static (string NormalizedJson, string Hash) Oracle(string baseJson, string deltaJson)
    {
        var merged = new BBT.Workflow.JsonData(baseJson).Merge(new BBT.Workflow.JsonData(deltaJson));
        var normalized = merged.NormalizedJson;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return (normalized, hash);
    }

    private static string Canonical(string baseJson, string deltaJson, JsonNumberPolicy policy) =>
        JsonCanonicalizer.MergeAndCanonicalize(
            new BBT.Workflow.JsonData(baseJson).JsonElement,
            new BBT.Workflow.JsonData(deltaJson).JsonElement,
            policy).NormalizedJson;

    /// <summary>Kaybın olduğu ve düzelmesi BEKLENEN değerler.</summary>
    public static IEnumerable<object[]> LossyCorpus()
    {
        // int64 tavanı: bugün 9223372036854775808 (int64 DIŞI) yazılıyor.
        yield return new object[] { """{"v":9223372036854775807}""", """{"x":1}""", "9223372036854775807" };
        // 2^53+1: bugün ...992'ye yuvarlanıyor.
        yield return new object[] { """{"v":9007199254740993}""", """{"x":1}""", "9007199254740993" };
        // 20 haneli ondalık: bugün 17 haneye kırpılıyor.
        yield return new object[] { """{"v":0.12345678901234567890}""", """{"x":1}""", "0.1234567890123456789" };
        // Kuruş hassasiyeti: bugün ...6.8 oluyor.
        yield return new object[] { """{"v":1234567890123456.78}""", """{"x":1}""", "1234567890123456.78" };
    }

    [Theory]
    [MemberData(nameof(LossyCorpus))]
    public void PreservePrecision_FixesLossyValues(string baseJson, string deltaJson, string expectedNumberText)
    {
        var legacy = Canonical(baseJson, deltaJson, JsonNumberPolicy.Legacy);
        var preserved = Canonical(baseJson, deltaJson, JsonNumberPolicy.PreservePrecision);

        // Tam eşitlik: Contains, beklenen metnin SONUNA eklenmiş fazla haneyi yakalayamaz
        // (ör. 0.1234567890123456789 ⊂ 0.12345678901234567890) — bu yüzden tüm doküman pinlenir.
        Assert.Equal($$"""{"v":{{expectedNumberText}},"x":1}""", preserved);
        Assert.DoesNotContain($"\"v\":{expectedNumberText}", legacy); // bugün gerçekten bozuk
        Assert.Equal(legacy, Canonical(baseJson, deltaJson, JsonNumberPolicy.Legacy)); // determinizm
    }

    /// <summary>E-gösterimli değerler: kayıp YOK ama metin düzleşir (bilinçli).</summary>
    [Theory]
    [InlineData("""{"v":0.00001}""", "0.00001")]
    [InlineData("""{"v":1e18}""", "1000000000000000000")]
    [InlineData("""{"v":-0.00002}""", "-0.00002")]
    public void PreservePrecision_FlattensExponentNotation(string baseJson, string expectedNumberText)
    {
        var preserved = Canonical(baseJson, """{"x":1}""", JsonNumberPolicy.PreservePrecision);
        Assert.Equal($$"""{"v":{{expectedNumberText}},"x":1}""", preserved);
    }

    /// <summary>SIRADAN değerler: iki mod birebir aynı (asıl invaryant).</summary>
    public static IEnumerable<object[]> OrdinaryCorpus()
    {
        yield return new object[] { """{"a":1}""", """{"b":2}""" };
        yield return new object[] { """{"n1":1.0,"n2":1e5,"n3":-0,"n4":0.10}""", """{"n5":2.50}""" };
        yield return new object[] { """{"money":1234.56,"rate":0.075}""", """{"qty":3}""" };
        yield return new object[] { """{"big":3000000000}""", """{"x":1}""" };       // int64'e sığar, double da tam
        yield return new object[] { """{"arr":[{"a":1.50}]}""", """{"arr":[{"b":2}]}""" };
        yield return new object[] { """{"deep":{"deep":{"v":[1,2.5,3]}}}""", """{"x":null}""" };
    }

    [Theory]
    [MemberData(nameof(OrdinaryCorpus))]
    public void PreservePrecision_MatchesLegacy_ForOrdinaryValues(string baseJson, string deltaJson)
    {
        Assert.Equal(
            Canonical(baseJson, deltaJson, JsonNumberPolicy.Legacy),
            Canonical(baseJson, deltaJson, JsonNumberPolicy.PreservePrecision));
    }

    [Theory]
    [MemberData(nameof(OrdinaryCorpus))]
    public void LegacyPolicy_StillMatchesTheOracle(string baseJson, string deltaJson)
    {
        var expected = Oracle(baseJson, deltaJson);
        var actual = JsonCanonicalizer.MergeAndCanonicalize(
            new BBT.Workflow.JsonData(baseJson).JsonElement,
            new BBT.Workflow.JsonData(deltaJson).JsonElement,
            JsonNumberPolicy.Legacy);
        Assert.Equal(expected.NormalizedJson, actual.NormalizedJson);
        Assert.Equal(expected.Hash, actual.DataHash);
    }

    [Fact]
    public void PreservePrecision_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Virgül-ondalıklı kültür: format string InvariantCulture ile sabitlenmemişse kırar.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var preserved = Canonical("""{"v":1234567890123456.78}""", """{"x":1}""", JsonNumberPolicy.PreservePrecision);
            Assert.Contains("\"v\":1234567890123456.78", preserved);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
