using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Benchmarks.Legacy;
using BBT.Workflow.Canonicalization;
using BBT.Workflow.RepresentationEtag;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Representation ETag, old vs new. <c>Canonicalize_*</c> isolate the canonicalizer on the serialized
/// body; <c>Generate_*</c> are the full per-response cost (serialize DTO → canonicalize → SHA256 →
/// base64url). <see cref="Text"/>: <c>Ascii</c> is <see cref="PayloadFactory"/> as-is; <c>Turkish</c>
/// swaps its string values for Turkish text, which the default STJ encoder writes as <c>\uXXXX</c>
/// escapes (the realistic case for this runtime's bodies).
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class RepresentationEtagBenchmarks
{
    private readonly LegacyJsonCanonicalizer _legacy = new();
    private readonly JsonCanonicalizer _canonicalizer = new();
    private RepresentationEtagService _service = null!;
    private string _json = null!;
    private byte[] _utf8 = null!;
    private BenchmarkDto _dto = null!;

    [Params(1, 20, 200)]
    public int PayloadKb { get; set; }

    [Params("Ascii", "Turkish")]
    public string Text { get; set; } = "Ascii";

    public sealed class BenchmarkDto
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
        public JsonElement Data { get; set; }
    }

    [GlobalSetup]
    public void Setup()
    {
        var payload = PayloadFactory.Json(PayloadKb);
        if (Text == "Turkish")
        {
            payload = payload.Replace("Benchmark User", "Şükrü Öğüt").Replace("retail", "bireysel müşteri")
                .Replace("\"web\"", "\"şube içi işlem\"");
        }

        _dto = new BenchmarkDto
        {
            Id = Guid.Parse("4d2c6a2e-6a1f-4c1b-9d0e-7b8f3a1c2d3e"),
            Key = "benchmark-instance",
            Title = Text == "Turkish" ? "Kredi başvurusu — onay bekliyor" : "Loan application - pending",
            Status = "A",
            Tags = ["a", "b", "c"],
            Data = JsonDocument.Parse(payload).RootElement,
        };
        _json = JsonSerializer.Serialize(_dto, JsonSerializerConstants.JsonOptions);
        _utf8 = Encoding.UTF8.GetBytes(_json);
        _service = new RepresentationEtagService(_canonicalizer);

        if (_service.Generate(_dto) != Generate_Legacy())
            throw new InvalidOperationException("ETag parity broken for the benchmark payload.");
    }

    [Benchmark(Baseline = true)]
    public string Canonicalize_Legacy() => _legacy.Canonicalize(_json);

    [Benchmark]
    public string Canonicalize_New_String() => _canonicalizer.Canonicalize(_json);

    [Benchmark]
    public int Canonicalize_New_Utf8()
    {
        using var output = new PooledByteBufferWriter(_utf8.Length);
        _canonicalizer.Canonicalize(_utf8, output);
        return output.WrittenCount;
    }

    /// <summary>The pre-rewrite <c>RepresentationEtagService.Generate</c>, verbatim.</summary>
    [Benchmark]
    public string Generate_Legacy()
    {
        var json = JsonSerializer.Serialize<object>(_dto, JsonSerializerConstants.JsonOptions);
        var canonicalJson = new LegacyJsonCanonicalizer().Canonicalize(json);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToBase64String(hashBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    [Benchmark]
    public string Generate_New() => _service.Generate(_dto);
}
