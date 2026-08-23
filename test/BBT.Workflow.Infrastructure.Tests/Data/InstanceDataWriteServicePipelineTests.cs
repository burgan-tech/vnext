using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// End-to-end proof for the <c>InstanceDataWrite:LegacyAppendPipeline</c> kill-switch (B9):
/// <see cref="InstanceDataWriteService.PlanAppend"/> must produce byte-identical rows whichever
/// pipeline is configured, and a duplicate detected under one pipeline must also be detected
/// under the other — including when the head row was itself written by the OTHER pipeline. Both
/// pipelines are configured the way production does, via two <see cref="WorkflowExecutionOptions"/>
/// instances, even though <c>PlanAppend</c> itself only needs the resolved flag.
/// </summary>
public class InstanceDataWriteServicePipelineTests
{
    private static readonly bool LegacyPipeline = MakeOptions(legacy: true).Value.InstanceDataWrite.LegacyAppendPipeline;
    private static readonly bool NewPipeline = MakeOptions(legacy: false).Value.InstanceDataWrite.LegacyAppendPipeline;

    private static IOptions<WorkflowExecutionOptions> MakeOptions(bool legacy) =>
        Options.Create(new WorkflowExecutionOptions
        {
            InstanceDataWrite = new InstanceDataWriteOptions { LegacyAppendPipeline = legacy }
        });

    [Fact]
    public void Append_NewPipeline_ProducesSameRow_AsLegacy()
    {
        // A head with nested objects/arrays/null-delta-on-existing-key — the same shape the
        // JsonCanonicalizer parity corpus already pins at the merge-engine level; here it is
        // proven end-to-end through PlanAppend.
        var head = CreateHeadRow("{\"a\":1,\"nested\":{\"x\":1,\"y\":[1,2]},\"Keep\":true}", "1.2.3");
        var delta = new JsonData("{\"b\":2,\"nested\":{\"y\":[9],\"z\":null},\"Keep\":null}");

        var legacyPlan = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.IncreasePatch, LegacyPipeline);
        var newPlan = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.IncreasePatch, NewPipeline);

        newPlan.Version.ShouldBe(legacyPlan.Version);
        newPlan.IsDuplicate.ShouldBe(legacyPlan.IsDuplicate);
        newPlan.Content.NormalizedJson.ShouldBe(legacyPlan.Content.NormalizedJson);
        InstanceData.ComputeDataHash(newPlan.Content).ShouldBe(InstanceData.ComputeDataHash(legacyPlan.Content));
    }

    [Fact]
    public void Append_NewPipeline_ProducesSameRow_AsLegacy_WhenHeadIsNull()
    {
        // The first-ever append for an instance: legacy skips JsonData.Merge entirely (nothing to
        // merge with), so its NormalizedJson is sort-only — no camelCase, no number reformat.
        // Deliberately using a PascalCase key and a "1.50" lexical decimal: if the new pipeline
        // routed this through JsonCanonicalizer with an empty base (which replicates Merge's
        // Expando round-trip), the key would get camelCased and the number reformatted, breaking
        // parity right here. This pins that the head-null branch stays untouched by the
        // canonicalizer.
        var delta = new JsonData("{\"Start\":true,\"n\":1.50}");

        var legacyPlan = InstanceDataWriteService.PlanAppend(null, delta, VersionStrategy.IncreasePatch, LegacyPipeline);
        var newPlan = InstanceDataWriteService.PlanAppend(null, delta, VersionStrategy.IncreasePatch, NewPipeline);

        newPlan.Version.ShouldBe(legacyPlan.Version);
        newPlan.IsDuplicate.ShouldBe(legacyPlan.IsDuplicate);
        newPlan.Content.NormalizedJson.ShouldBe(legacyPlan.Content.NormalizedJson);
        InstanceData.ComputeDataHash(newPlan.Content).ShouldBe(InstanceData.ComputeDataHash(legacyPlan.Content));
    }

    [Fact]
    public void Append_DuplicateDelta_IsSkipped_OnBothPipelines()
    {
        // The head row is written the way LEGACY would persist it (first append, legacy
        // pipeline), then an idempotent duplicate delta is attempted via the NEW pipeline against
        // that legacy-written head. If the hash formulas ever diverged, this is exactly the case
        // that would surface it: a duplicate detected under one pipeline but missed under the
        // other because the "head" and the "candidate" were canonicalized differently.
        var firstAppend = InstanceDataWriteService.PlanAppend(
            null, new JsonData("{\"a\":1,\"rr_doc1\":true}"), VersionStrategy.None, LegacyPipeline);
        var head = new InstanceDataHeadRow
        {
            Version = firstAppend.Version,
            Data = firstAppend.Content.Json,
            DataHash = InstanceData.ComputeDataHash(firstAppend.Content)
        };

        var duplicateDelta = new JsonData("{\"rr_doc1\":true}");

        var legacyPlan = InstanceDataWriteService.PlanAppend(head, duplicateDelta, VersionStrategy.IncreaseMinor, LegacyPipeline);
        var newPlan = InstanceDataWriteService.PlanAppend(head, duplicateDelta, VersionStrategy.IncreaseMinor, NewPipeline);

        legacyPlan.IsDuplicate.ShouldBeTrue();
        newPlan.IsDuplicate.ShouldBeTrue();
    }

    private static InstanceDataHeadRow CreateHeadRow(string json, string version)
    {
        var data = new JsonData(json);
        return new InstanceDataHeadRow
        {
            Version = version,
            Data = data.Json,
            DataHash = InstanceData.ComputeDataHash(data)
        };
    }

    [Fact]
    public void PlanAppend_WithPrecisionFlag_PreservesLosslessNumbers()
    {
        var head = CreateHeadRow("{\"v\":1}", "1.0.0");
        var delta = new JsonData("""{"amount":1234567890123456.78}""");

        var withFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: false, preserveNumericPrecision: true);
        var withoutFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: false, preserveNumericPrecision: false);

        // The exact token, not a substring: ShouldContain would also accept a longer wrong number
        // (1234567890123456.789) or a sign flip, which is precisely what this test must rule out.
        AmountToken(withFlag).ShouldBe("1234567890123456.78");
        AmountToken(withoutFlag).ShouldBe("1234567890123456.8"); // today's precision loss, pinned
    }

    /// <summary>Raw JSON text of the merged <c>amount</c> property — key-order independent.</summary>
    private static string AmountToken(AppendPlan plan) =>
        System.Text.Json.JsonDocument.Parse(plan.Content.Json)
            .RootElement.GetProperty("amount").GetRawText();

    [Fact]
    public void PlanAppend_LegacyPipeline_IgnoresPrecisionFlag()
    {
        var head = CreateHeadRow("{\"v\":1}", "1.0.0");
        var delta = new JsonData("""{"amount":1234567890123456.78}""");

        var legacyWithFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: true, preserveNumericPrecision: true);
        var legacyWithoutFlag = InstanceDataWriteService.PlanAppend(head, delta, VersionStrategy.None,
            legacyPipeline: true, preserveNumericPrecision: false);

        legacyWithFlag.Content.NormalizedJson.ShouldBe(legacyWithoutFlag.Content.NormalizedJson);
    }
}
