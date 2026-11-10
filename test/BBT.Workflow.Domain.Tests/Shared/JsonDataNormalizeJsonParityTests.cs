using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Shared.Merging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Shared;

/// <summary>
/// <see cref="JsonData.NormalizedJson"/> feeds the PERSISTED <c>InstanceData.DataHash</c> and the
/// field-encryption keyed hash, so its output must stay byte-identical. <see cref="LegacyNormalizeJson"/>
/// is a verbatim copy of the pre-single-pass implementation and is the oracle; it is never changed to
/// make a case pass.
/// </summary>
public class JsonDataNormalizeJsonParityTests
{
    // ---- Verbatim copy of the old JsonData.NormalizeJson / NormalizeJsonElement (oracle) ----

    private static string LegacyNormalizeJson(string json)
    {
        try
        {
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(json);
            var normalizedElement = LegacyNormalizeJsonElement(jsonElement);
            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = null,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            return JsonSerializer.Serialize(normalizedElement, options);
        }
        catch
        {
            return json;
        }
    }

    private static JsonElement LegacyNormalizeJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var sortedProperties = element.EnumerateObject()
                    .OrderBy(prop => prop.Name, StringComparer.Ordinal)
                    .ToDictionary(prop => prop.Name, prop => LegacyNormalizeJsonElement(prop.Value));
                return JsonSerializer.SerializeToElement(sortedProperties);
            case JsonValueKind.Array:
                var normalizedArray = element.EnumerateArray().Select(LegacyNormalizeJsonElement).ToArray();
                return JsonSerializer.SerializeToElement(normalizedArray);
            default:
                return element;
        }
    }

    // ---- Corpus ----

    public static IEnumerable<object[]> Corpus()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in RawCorpus())
        {
            if (seen.Add(json)) yield return new object[] { json };
        }
    }

    private static IEnumerable<string> RawCorpus()
    {
        // Every input of the merge-canonicalizer parity corpus, plus what Merge produces from it
        // (the strings NormalizedJson actually sees on the legacy append path).
        foreach (var pair in JsonCanonicalizerParityTests.Corpus())
        {
            var baseJson = (string)pair[0];
            var deltaJson = (string)pair[1];
            yield return baseJson;
            yield return deltaJson;
            yield return new JsonData(baseJson).Merge(new JsonData(deltaJson)).Json;
        }

        yield return "{}";
        yield return "[]";
        yield return """{"b":1,"a":{"d":[3,{"z":1,"y":2}],"c":null}}""";
        yield return """[{"id":2,"b":1,"a":0},{"id":1},[],{}]""";
        yield return """{"items":[{"sku":"x","amount":1.50},{"amount":2,"sku":"y"}]}""";
        yield return """{"Z":1,"a":2,"A":3,"_":4,"0":5,"aa":6,"a ":7}""";
        yield return """{"\uff01":1,"🙂":2,"\ue000":3,"z":4}""";
        yield return """{"tr":"şğüİı","emoji":"🙂","cjk":"漢字","u":"\u00e9\u015f\u0130"}""";
        yield return """{"esc":"a\"b\\c\/d","q":"\u0022","bs":"ends\\","path":"C:\\temp\\x"}""";
        yield return """{"ctl":"l1\nl2\tt\rr\bb\ff","c1":"\u0001","c1f":"\u001f","del":"\u007f","ls":"\u2028"}""";
        yield return """{"html":"<a href='x'>&amp;</a>","plus":"1+1","backtick":"`"}""";
        yield return """{"<k>":1,"a&b":2,"it's":3,"\u0001key":4,"k\\ey":5,"k\"q":6}""";
        yield return """{"n":[1,1.0,-0,0,-0.0,0.10,1e5,1E+5,1e-5,1.5E-07,3000000000,9007199254740993,1.2345678901234567890123,1e400,-1e400]}""";
        yield return """{"pair":"\ud83d\ude00"}""";
        // Lone surrogates: both implementations must agree on whatever STJ does with them.
        yield return """{"lone":"x\ud800y"}""";
        yield return """{"\udc00k":1}""";
        // Duplicate keys: legacy's ToDictionary throws and the whole input comes back unchanged.
        yield return """{"a":1,"a":2}""";
        yield return """{"x":{"k":1,"k":2},"b":3}""";
        yield return """[{"ok":1},{"d":1,"d":1}]""";
        yield return """{"A":1,"a":2}""";
        // Not JSON / not accepted by default options: returned unchanged.
        yield return "not json";
        yield return "{\"a\":1,}";
        yield return "{\"a\":1 /* c */}";
        yield return "\uFEFF{\"a\":1}";
        yield return "{\"a\":1} trailing";
        yield return "{\"a\":";
        // Top-level primitives.
        yield return "1.50";
        yield return "\"str\\\\\"";
        yield return "null";
        yield return "true";
        // Formatting noise.
        yield return "  {\n  \"spaced\" : [ 1 ,\t2 ] ,\r\n \"b\" : { \"c\" : null }\n}  ";
        // Depth: 30 levels (objects / arrays / mixed) and the 64 default-MaxDepth boundary.
        yield return DeepObject(30);
        yield return DeepArray(30);
        yield return DeepMixed(30);
        yield return DeepObject(63);
        yield return DeepObject(64);
        yield return DeepObject(65);
        yield return DeepArray(64);
        yield return DeepArray(65);
    }

    private static string DeepObject(int levels)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < levels; i++) sb.Append("{\"z\":0,\"l").Append(i).Append("\":");
        sb.Append("\"leaf\"");
        for (var i = 0; i < levels; i++) sb.Append(",\"a\":[1,{\"y\":2,\"x\":1}]}");
        return sb.ToString();
    }

    private static string DeepArray(int levels) => new string('[', levels) + "1" + new string(']', levels);

    private static string DeepMixed(int levels)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < levels; i++) sb.Append(i % 2 == 0 ? "{\"k\":" : "[0,");
        sb.Append("null");
        for (var i = levels - 1; i >= 0; i--) sb.Append(i % 2 == 0 ? "}" : "]");
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void NormalizedJson_ByteParity_WithLegacy(string json)
    {
        new JsonData(json).NormalizedJson.ShouldBe(LegacyNormalizeJson(json));
    }

    [Fact]
    public void NormalizedJson_RandomizedParity_WithLegacy()
    {
        var rng = new Random(20261109);
        for (var i = 0; i < 500; i++)
        {
            var json = RandomValue(rng, 0, forceObject: true);
            var expected = LegacyNormalizeJson(json);
            var actual = new JsonData(json).NormalizedJson;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException($"Case {i} diverged.\ninput:    {json}\nexpected: {expected}\nactual:   {actual}");
            }
        }
    }

    private static readonly string[] Keys = ["a", "b", "B", "z", "Z", "_", "0", "ş", "İ", "🙂", "\uff01", "a\\\\b", "q\\\"", "<k>"];

    private static readonly string[] Strings =
        ["", "x", "şğüİı", "a\\\"b\\\\c", "🙂", "\\u00e9", "\\n\\t", "<&>'", "\\/", "\\u0001", "\\\\"];

    private static readonly string[] Numbers =
        ["0", "-0", "1", "-17", "1.0", "0.10", "1e5", "1E-3", "-2.5e+3", "3000000000", "9007199254740993", "1.23456789012345678901"];

    private static string RandomValue(Random rng, int depth, bool forceObject = false)
    {
        var kind = forceObject ? 5 : rng.Next(0, depth >= 6 ? 5 : 7);
        switch (kind)
        {
            case 0: return Numbers[rng.Next(Numbers.Length)];
            case 1: return "\"" + Strings[rng.Next(Strings.Length)] + "\"";
            case 2: return rng.Next(2) == 0 ? "true" : "false";
            case 3: return "null";
            case 4: return Numbers[rng.Next(Numbers.Length)];
            case 5:
            {
                var count = rng.Next(0, 6);
                var used = new HashSet<string>();
                var parts = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var key = Keys[rng.Next(Keys.Length)];
                    // ~3% of objects keep a duplicate key on purpose (legacy fallback path).
                    if (!used.Add(key) && rng.Next(0, 30) != 0) continue;
                    parts.Add("\"" + key + "\":" + RandomValue(rng, depth + 1));
                }
                return "{" + string.Join(",", parts) + "}";
            }
            default:
            {
                var count = rng.Next(0, 4);
                var parts = Enumerable.Range(0, count).Select(_ => RandomValue(rng, depth + 1));
                return "[" + string.Join(",", parts) + "]";
            }
        }
    }

    /// <summary>Formatting-independence (the reason NormalizeJson exists) still holds.</summary>
    [Fact]
    public void NormalizedJson_IgnoresFormattingAndKeyOrder()
    {
        new JsonData("{ \"b\" : 1, \"a\" : { \"d\":2, \"c\":[ 1, 2 ] } }").NormalizedJson
            .ShouldBe("""{"a":{"c":[1,2],"d":2},"b":1}""");
    }

    [Fact]
    public void NormalizedJson_KeepsNumberTextAndRelaxedEscaping()
    {
        new JsonData("""{"n":1.50,"s":"ş<&>\u0022"}""").NormalizedJson
            .ShouldBe("{\"n\":1.50,\"s\":\"ş<&>\\\"\"}");
    }
}
