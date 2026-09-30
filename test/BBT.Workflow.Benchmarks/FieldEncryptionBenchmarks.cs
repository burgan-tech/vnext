using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BBT.Workflow.Authorization;
using BBT.Workflow.Encryption;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// E3 (council <c>2026-09-29-field-encryption-encrypt</c>): CPU cost of the <c>x-encryption</c> passes the write funnel and
/// the lazy read add to one InstanceData row, against the plain-row paths they replace. Secret lookup (L1 hit) is
/// included; database round trips are not — they are measured on the live runtime.
/// <list type="bullet">
///   <item><see cref="SealFresh"/> — every encrypt path gets a new token (first write, or all values changed)</item>
///   <item><see cref="SealCarry"/> — every value unchanged: tokens are carried forward from the head (the common later write)</item>
///   <item><see cref="Open"/> — lazy read of a token-carrying row; <see cref="OpenPlain"/> is the same call on a row without tokens</item>
///   <item><see cref="Hash"/> — hash paths replaced by HMAC digests; <see cref="HashCarry"/> keeps existing digests</item>
///   <item><see cref="KeyedDataHash"/> vs <see cref="PlainDataHash"/> — the dedup hash of a token row vs a plain row</item>
///   <item><see cref="SanitizePlainDelta"/> — the reserved-prefix scan on a delta that carries none (every write pays it)</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class FieldEncryptionBenchmarks
{
    private const string Schema = "bench";
    private static readonly Guid InstanceId = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");

    private InstanceDataProtector _protector = null!;
    private InstanceSecretMaterial _secret = null!;
    private IReadOnlySet<string> _encryptPaths = null!;
    private IReadOnlyDictionary<string, string> _hashPaths = null!;
    private string _plainJson = null!;
    private string _storedJson = null!;
    private string _hashedJson = null!;
    private InstanceDataView _head = null!;
    private JsonData _delta = null!;

    /// <summary>Approximate size of the rest of the document.</summary>
    [Params(2, 20)]
    public int DocKb { get; set; }

    /// <summary>How many string fields carry <c>encrypt</c> (and, separately, <c>hash</c>).</summary>
    [Params(2, 10)]
    public int ProtectedFields { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var store = new InstanceSecretStore(Options.Create(new SchemaEncryptionOptions()));
        _secret = new InstanceSecretMaterial(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        store.Put(Schema, InstanceId, _secret);
        _protector = new InstanceDataProtector(store);

        var rest = PayloadFactory.Json(DocKb);
        var vault = string.Join(",", Enumerable.Range(0, ProtectedFields).Select(i => $"\"e{i}\":\"user{i}@example.com\""));
        var ids = string.Join(",", Enumerable.Range(0, ProtectedFields).Select(i => $"\"h{i}\":\"1000000{i:D4}\""));
        _plainJson = "{\"vault\":{" + vault + "},\"ids\":{" + ids + "}," + rest[1..];

        _encryptPaths = Enumerable.Range(0, ProtectedFields).Select(i => $"vault.e{i}").ToHashSet(StringComparer.Ordinal);
        _hashPaths = Enumerable.Range(0, ProtectedFields).ToDictionary(i => $"ids.h{i}", _ => "sha256", StringComparer.Ordinal);

        _hashedJson = _protector.ApplyHashes(new JsonData(_plainJson), _hashPaths, _secret).Json;
        _storedJson = _protector.Protect(InstanceId, new JsonData(_hashedJson), _encryptPaths, null, _secret).Stored.Json;
        _head = _protector.Unprotect(Schema, InstanceId, new JsonData(_storedJson));
        _delta = new JsonData("""{"taskResult":{"status":"ok","note":"plain"}}""");
    }

    // Fresh JsonData per call: JsonData memoizes its parsed element, and the funnel parses each row once.

    [Benchmark]
    public JsonData SealFresh() =>
        _protector.Protect(InstanceId, new JsonData(_hashedJson), _encryptPaths, null, _secret).Stored;

    [Benchmark]
    public JsonData SealCarry() =>
        _protector.Protect(InstanceId, new JsonData(_hashedJson), _encryptPaths, _head, _secret).Stored;

    [Benchmark]
    public JsonData Open() => _protector.Unprotect(Schema, InstanceId, new JsonData(_storedJson)).Plain;

    [Benchmark(Baseline = true)]
    public JsonData OpenPlain() => _protector.Unprotect(Schema, InstanceId, new JsonData(_plainJson)).Plain;

    [Benchmark]
    public JsonData Hash() => _protector.ApplyHashes(new JsonData(_plainJson), _hashPaths, _secret);

    [Benchmark]
    public JsonData HashCarry() => _protector.ApplyHashes(new JsonData(_hashedJson), _hashPaths, _secret);

    [Benchmark]
    public string KeyedDataHash() => InstanceDataProtector.KeyedDataHash(new JsonData(_hashedJson), _secret);

    /// <summary>Same computation as the internal <c>InstanceData.ComputeDataHash</c> (SHA-1 of the normalized JSON).</summary>
    [Benchmark]
    public string PlainDataHash() =>
        Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(new JsonData(_plainJson).NormalizedJson))).ToLowerInvariant();

    [Benchmark]
    public JsonData SanitizePlainDelta() =>
        _protector.SanitizeDelta(InstanceId, _delta, new JsonData(_storedJson), _head, _hashPaths);
}
