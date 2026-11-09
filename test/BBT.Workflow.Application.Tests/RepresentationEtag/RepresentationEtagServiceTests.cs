using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Canonicalization;
using Shouldly;
using Xunit;

namespace BBT.Workflow.RepresentationEtag;

/// <summary>
/// Pins <see cref="RepresentationEtagService"/>: ETags must be stable for an unchanged body, must change
/// when only a backslash differs (the old canonicalizer dropped escaped backslashes, so such bodies shared
/// an ETag and produced a wrong 304), and must stay byte-identical to the previous formula for every body
/// without <c>\\</c> / <c>\"</c> escapes so clients' cached ETags keep matching after the rewrite.
/// </summary>
public class RepresentationEtagServiceTests
{
    private readonly RepresentationEtagService _sut = new(new JsonCanonicalizer());

    /// <summary>The pre-rewrite Generate, verbatim, over the verbatim legacy canonicalizer.</summary>
    private static string LegacyGenerate(object responseDto)
    {
        var json = JsonSerializer.Serialize(responseDto, JsonSerializerConstants.JsonOptions);
        var canonicalJson = new LegacyJsonCanonicalizer().Canonicalize(json);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToBase64String(hashBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public enum SampleStatus { Active, Completed }

    public sealed class SampleDto
    {
        public Guid Id { get; set; }
        public string? Key { get; set; }
        public string? Title { get; set; }
        public string? Ignored { get; set; }
        public SampleStatus Status { get; set; }
        public int Count { get; set; }
        public long Big { get; set; }
        public double Ratio { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<string> Tags { get; set; } = [];
        public Dictionary<string, object?> Attributes { get; set; } = new();
        public JsonElement? Data { get; set; }
        public SampleDto? Child { get; set; }
    }

    private static SampleDto Sample(string title) => new()
    {
        Id = Guid.Parse("4d2c6a2e-6a1f-4c1b-9d0e-7b8f3a1c2d3e"),
        Key = "instance-key",
        Title = title,
        Status = SampleStatus.Completed,
        Count = 42,
        Big = 9_007_199_254_740_993,
        Ratio = 0.1,
        Amount = 1234.50m,
        CreatedAt = new DateTime(2026, 11, 9, 10, 30, 0, DateTimeKind.Utc),
        Tags = ["b", "a", "şğü", "<&>'", "🙂"],
        Attributes = new Dictionary<string, object?>
        {
            ["Zeta"] = 1,
            ["alpha"] = "İstanbul",
            ["nested"] = new Dictionary<string, object?> { ["b"] = true, ["a"] = null, ["c"] = 1.5e-7 },
            ["list"] = new object?[] { 1, "two", null, 3000000000L },
        },
        Data = JsonDocument.Parse("""{"z":[1,1.0,1e5,-0],"a":"\u00e9\n\t\u0001","\uff01":1,"🙂":2}""").RootElement,
        Child = new SampleDto { Key = "child", Title = "çocuk", Tags = [] },
    };

    [Theory]
    [InlineData("plain ascii")]
    [InlineData("Türkçe karakterler: şğüİıÖÇ")]
    [InlineData("html <b>&amp;</b> 'quoted' a+b")]
    [InlineData("controls\n\t\r\b\f\u0001\u001f\u007f")]
    [InlineData("emoji 🙂 and \u2028 separator")]
    [InlineData("lone \ud800 surrogate")]
    [InlineData("")]
    public void Generate_MatchesLegacyFormula_ForBodiesWithoutBackslashOrQuoteEscapes(string title)
    {
        var dto = Sample(title);

        _sut.Generate(dto).ShouldBe(LegacyGenerate(dto));
    }

    [Fact]
    public void Generate_MatchesLegacyFormula_ForAnonymousObject()
    {
        var dto = new { id = 1, name = "x", items = new[] { new { b = 2, a = 1 } } };

        _sut.Generate(dto).ShouldBe(LegacyGenerate(dto));
    }

    [Fact]
    public void Generate_IsStable_ForSameBody()
    {
        _sut.Generate(Sample("same")).ShouldBe(_sut.Generate(Sample("same")));
    }

    [Fact]
    public void Generate_Differs_WhenOnlyABackslashDiffers()
    {
        _sut.Generate(Sample("C:\\x")).ShouldNotBe(_sut.Generate(Sample("C:x")));
    }

    [Fact]
    public void Generate_Differs_WhenOnlyAQuoteDiffers()
    {
        _sut.Generate(Sample("a\"b")).ShouldNotBe(_sut.Generate(Sample("ab")));
    }

    [Fact]
    public void Generate_DoesNotThrow_WhenStringEndsWithBackslash()
    {
        Should.NotThrow(() => _sut.Generate(Sample("trailing\\")));
    }

    [Fact]
    public void Generate_IsBase64UrlWithoutPadding()
    {
        var etag = _sut.Generate(Sample("x"));

        etag.Length.ShouldBe(43);
        etag.ShouldNotContain('+');
        etag.ShouldNotContain('/');
        etag.ShouldNotContain('=');
    }
}
