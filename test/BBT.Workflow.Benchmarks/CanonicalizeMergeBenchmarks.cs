using BenchmarkDotNet.Attributes;
using BBT.Workflow.Shared.Merging;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Append pipeline, old vs. new (B9): <see cref="Legacy"/> is the pre-Katman-2 chain
/// (<c>JsonData.Merge</c> → <c>NormalizedJson</c> — two Expando round-trips + a separate
/// normalize pass, still live behind <c>LegacyAppendPipeline</c>); <see cref="Canonical"/> is
/// <see cref="JsonCanonicalizer.MergeAndCanonicalize"/>, the single-pass <c>Utf8JsonWriter</c>
/// replacement PlanAppend uses by default now.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class CanonicalizeMergeBenchmarks
{
    private string _baseJson = null!;
    private BBT.Workflow.JsonData _delta = null!;

    [Params(10, 50, 200)]
    public int DocKb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // The base document string is kept, not a shared JsonData instance: JsonData.JsonElement
        // is now memoized (Katman 2 / Task 1), so a shared instance would only measure the parse
        // once and hide it on every subsequent invocation. Both benchmarks below construct a fresh
        // JsonData from _baseJson per invocation for the same reason InstanceDataAccessBenchmarks
        // does (see its comment) — this keeps the comparison honest: each iteration pays the same
        // cold base-parse cost the real PlanAppend call pays per transition.
        _baseJson = PayloadFactory.Json(DocKb);
        // Delta is small and fixed; its parse cost is negligible relative to the base document at
        // every DocKb tier, so a single shared JsonData is fine here — it mirrors how PlanAppend
        // receives one already-parsed delta per call regardless of the base document's size.
        _delta = new BBT.Workflow.JsonData("""{"taskResult":{"status":"ok"}}""");
    }

    /// <summary>Old pipeline: Merge (two parses + Expando merge + serialize) then NormalizedJson
    /// (canonical rebuild with per-node SerializeToElement) — the full pre-Katman-2 chain's work.</summary>
    [Benchmark]
    public string Legacy() => new BBT.Workflow.JsonData(_baseJson).Merge(_delta).NormalizedJson;

    /// <summary>New pipeline: single Utf8JsonWriter pass producing normalized JSON + hash together.</summary>
    [Benchmark]
    public JsonCanonicalizer.CanonicalResult Canonical() =>
        JsonCanonicalizer.MergeAndCanonicalize(new BBT.Workflow.JsonData(_baseJson).JsonElement, _delta.JsonElement);
}
