using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Canonicalization;

/// <summary>
/// Pins the representation-ETag canonicalizer. <see cref="LegacyJsonCanonicalizer"/> (verbatim copy of
/// the pre-rewrite parser) is the byte-parity oracle for every input that does NOT contain the
/// <c>\\</c>, <c>\"</c> or <c>\/</c> escapes the old parser mangled; those escapes are pinned by the
/// explicit fix tests below instead.
/// </summary>
public class JsonCanonicalizerTests
{
    private readonly JsonCanonicalizer _sut = new();

    public static IEnumerable<object[]> ParityCorpus()
    {
        string[] corpus =
        [
            """{}""",
            """{"a":1}""",
            """{"b":2,"a":1}""",
            """{"Z":1,"a":2,"A":3,"_":4,"0":5}""",
            """{"şğü":1,"ölçü":{"İç":2},"İstanbul":3,"abc":4}""",
            // Supplementary (surrogate pair) vs U+E000+ keys: UTF-16 ordinal puts the surrogate
            // pair (0xD83D...) BEFORE U+E000 / U+FF01, UTF-8 byte order would put it after.
            """{"\uff01":1,"🙂":2,"\ue000":3,"z":4}""",
            """{"🙂":1,"\uFF01":2}""",
            """{"a":{"x":1,"y":[1,2,{"c":3,"b":2}]},"b":[[],[{}],[[1]]]}""",
            """{"deep":{"deep":{"deep":{"v":[{"k":1},{"k":2}]}}}}""",
            """{"n":[1,1.0,-0,0,-0.0,1e5,1E+5,1e-5,0.1,0.10,3000000000,1.5e-7,9007199254740993,1.2345678901234567890123,123456789012345678,-12,1e400,-1e400,2.5,1E300,5e-324]}""",
            """{"tr":"şğüİı","emoji":"🙂","cjk":"漢字","mixed":"a b\u00e9c"}""",
            """{"q":"say \u0022hi\u0022","html":"<a href='x'>&</a>","plus":"+"}""",
            """{"ctl":"line1\nline2\ttab\rcr\bbs\fff","c1":"\u0001","c1f":"\u001f","c1a":"\u001A","del":"\u007f"}""",
            """{"u":"\u00e9\u015f\u0130","pair":"\ud83d\ude00"}""",
            """{"dup":1,"dup":2,"x":{"k":"first","k":"last"}}""",
            """{"empty":"","emptyObj":{},"emptyArr":[],"nul":null,"t":true,"f":false}""",
            "  {\n  \"spaced\" : [ 1 ,\t2 ] ,\r\n \"b\" : { \"c\" : null }\n}  ",
            """{"items":[{"id":1,"name":"a"},{"id":2,"name":"b"},{"name":"c","id":3}]}""",
            """{"mixed":[1,"two",{"b":1,"a":2},[3,4],null,true,false]}""",
        ];
        foreach (var json in corpus) yield return new object[] { json };
        yield return new object[] { Deep(300) };
        yield return new object[] { "{\"a\":" + DeepArray(300) + "}" };
    }

    private static string Deep(int levels)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < levels; i++) sb.Append("{\"l").Append(i % 3).Append("\":");
        sb.Append("1");
        for (var i = 0; i < levels; i++) sb.Append('}');
        return sb.ToString();
    }

    private static string DeepArray(int levels) => new string('[', levels) + "1" + new string(']', levels);

    [Theory]
    [MemberData(nameof(ParityCorpus))]
    public void Canonicalize_String_ByteParity_WithLegacy(string json)
    {
        var expected = new LegacyJsonCanonicalizer().Canonicalize(json);

        _sut.Canonicalize(json).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(ParityCorpus))]
    public void Canonicalize_Utf8_ByteParity_WithLegacyBytes(string json)
    {
        // What the ETag actually hashes: legacy hashed Encoding.UTF8.GetBytes(canonicalString).
        var expected = Encoding.UTF8.GetBytes(new LegacyJsonCanonicalizer().Canonicalize(json));

        var output = new ArrayBufferWriter<byte>();
        _sut.Canonicalize(Encoding.UTF8.GetBytes(json), output);

        output.WrittenSpan.ToArray().ShouldBe(expected);
    }

    /// <summary>
    /// The legacy parser never consumed a top-level '[' (it only peeked it), so every top-level array
    /// threw or was mis-nested. Response DTOs are objects, so this never reached an ETag; the rewrite
    /// simply canonicalizes it (and top-level primitives) correctly.
    /// </summary>
    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("""[1,"two",{"b":1,"a":2},[3,4],null,true,false]""", """[1,"two",{"a":2,"b":1},[3,4],null,true,false]""")]
    [InlineData("1.0", "1")]
    [InlineData("\"x\"", "\"x\"")]
    [InlineData("null", "null")]
    public void TopLevelArrayAndPrimitive_AreCanonicalized(string json, string expected)
    {
        _sut.Canonicalize(json).ShouldBe(expected);
    }

    [Fact]
    public void Backslash_IsNotDropped()
    {
        _sut.Canonicalize("""{"p":"C:\\x"}""").ShouldNotBe(_sut.Canonicalize("""{"p":"C:x"}"""));
        _sut.Canonicalize("""{"p":"C:\\x"}""").ShouldBe("""{"p":"C:\\x"}""");
    }

    [Fact]
    public void StringEndingInBackslash_RoundTrips()
    {
        _sut.Canonicalize("""{"p":"ends\\"}""").ShouldBe("""{"p":"ends\\"}""");
        _sut.Canonicalize("""["\\"]""").ShouldBe("""["\\"]""");
    }

    [Fact]
    public void EscapedSlash_IsWrittenLiteral()
    {
        _sut.Canonicalize("""{"p":"a\/b"}""").ShouldBe("""{"p":"a/b"}""");
    }

    [Fact]
    public void EscapedQuote_EqualsUnicodeEscapedQuote()
    {
        var plain = _sut.Canonicalize("""{"p":"a\"b"}""");
        plain.ShouldBe("""{"p":"a\"b"}""");
        _sut.Canonicalize("""{"p":"a\u0022b"}""").ShouldBe(plain);
    }

    [Fact]
    public void EscapedBackslashInKey_IsKept()
    {
        _sut.Canonicalize("""{"a\\b":1,"a":2}""").ShouldBe("""{"a":2,"a\\b":1}""");
    }

    [Fact]
    public void LoneSurrogateEscape_HashesLikeLegacy()
    {
        // Legacy decoded "\ud800" to a lone char and Encoding.UTF8.GetBytes replaced it with EF BF BD.
        const string json = """{"s":"x\ud800y"}""";
        var expected = Encoding.UTF8.GetBytes(new LegacyJsonCanonicalizer().Canonicalize(json));

        var output = new ArrayBufferWriter<byte>();
        _sut.Canonicalize(Encoding.UTF8.GetBytes(json), output);

        output.WrittenSpan.ToArray().ShouldBe(expected);
    }
}
