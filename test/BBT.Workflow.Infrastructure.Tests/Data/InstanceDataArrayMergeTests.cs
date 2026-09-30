using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// Pins the per-transition <c>arrayMerge</c> setting (vnext-client-sdk-core#58, AB-18) at the place
/// it actually decides an outcome: <see cref="InstanceDataWriteService.PlanAppend"/>.
/// </summary>
/// <remarks>
/// The trap this exists to close: today an incoming array REPLACES the stored one, so a caller
/// holding a stale copy silently erases items another request just added. The two values are
/// opposite trade-offs — <c>R</c> keeps removal-by-omission, <c>M</c> keeps concurrent additions —
/// which is why the behaviour is authored per transition rather than simply "fixed".
/// </remarks>
public class InstanceDataArrayMergeTests
{
    // ---- Default (absent / R): today's behaviour must not move -------------------------------

    [Fact]
    public void Absent_ReplacesTheWholeArray_TheHistoricalBehaviour()
    {
        var head = CreateHeadRow("""{"documents":["a","b"]}""");

        var plan = Plan(head, """{"documents":["b","c"]}""", mergeArrays: false);

        // "a" is gone — the incoming array won outright. This is how a caller deletes an item.
        plan.Content.Json.ShouldNotContain("\"a\"");
        plan.Content.Json.ShouldContain("\"b\"");
        plan.Content.Json.ShouldContain("\"c\"");
    }

    /// <summary>The data-loss scenario in AB-18, reproduced so the fix has something to be a fix OF.</summary>
    [Fact]
    public void Absent_StaleCopySilentlyErasesAConcurrentAddition()
    {
        // Another request already added C.
        var head = CreateHeadRow("""{"documents":["A","B","C"]}""");

        // This caller is holding a copy from before C existed.
        var plan = Plan(head, """{"documents":["A","B","D"]}""", mergeArrays: false);

        plan.Content.Json.ShouldNotContain("\"C\"");   // silently lost, no error
    }

    // ---- M: union ----------------------------------------------------------------------------

    [Fact]
    public void Merge_KeepsTheStoredItemsAndFoldsInTheNewOnes()
    {
        var head = CreateHeadRow("""{"documents":["A","B","C"]}""");

        var plan = Plan(head, """{"documents":["A","B","D"]}""", mergeArrays: true);

        // The same stale body as above now loses nothing.
        foreach (var expected in new[] { "A", "B", "C", "D" })
        {
            plan.Content.Json.ShouldContain($"\"{expected}\"");
        }
    }

    [Fact]
    public void Merge_DropsDuplicateScalars()
    {
        var head = CreateHeadRow("""{"tags":["x","y"]}""");

        var plan = Plan(head, """{"tags":["y","z"]}""", mergeArrays: true);

        plan.Content.Json.ShouldBe("""{"tags":["x","y","z"]}""");
    }

    /// <summary>
    /// The case that decides the whole design: an array of OBJECTS. Matching by <c>id</c> is what
    /// lets an existing document be UPDATED rather than duplicated — exact-value matching would
    /// leave both the old and the new revision of id 2 in the list.
    /// </summary>
    [Fact]
    public void Merge_MatchesObjectsById_SoAnEditUpdatesInsteadOfDuplicating()
    {
        var head = CreateHeadRow("""{"documents":[{"id":1,"name":"passport"},{"id":2,"name":"bill"}]}""");

        var plan = Plan(
            head,
            """{"documents":[{"id":2,"name":"bill-UPDATED"},{"id":3,"name":"payslip"}]}""",
            mergeArrays: true);

        var json = plan.Content.Json;
        json.ShouldContain("passport");        // id 1 survived — it was not in the incoming body
        json.ShouldContain("bill-UPDATED");    // id 2 was updated in place…
        json.ShouldNotContain("\"bill\"");     // …and its previous revision is gone, not duplicated
        json.ShouldContain("payslip");         // id 3 appended
    }

    [Fact]
    public void Merge_ResendingTheSamePayload_IsIdempotent()
    {
        var head = CreateHeadRow("""{"documents":[{"id":1,"name":"passport"}]}""");

        var plan = Plan(head, """{"documents":[{"id":1,"name":"passport"}]}""", mergeArrays: true);

        // Nothing changed, so the no-change dedup must recognise it — no duplicate item, no new row.
        plan.IsDuplicate.ShouldBeTrue();
    }

    /// <summary>
    /// Documented limitation: without an <c>id</c> the runtime cannot tell an edit from a new item,
    /// so an edited object is appended. Pinned so the behaviour is a decision, not a surprise.
    /// </summary>
    [Fact]
    public void Merge_ObjectsWithoutId_CannotExpressAnUpdate_SoAnEditAppends()
    {
        var head = CreateHeadRow("""{"documents":[{"name":"passport"}]}""");

        var plan = Plan(head, """{"documents":[{"name":"passport-EDITED"}]}""", mergeArrays: true);

        plan.Content.Json.ShouldContain("passport\"");         // original still there
        plan.Content.Json.ShouldContain("passport-EDITED");    // edit arrived as a separate entry
    }

    [Fact]
    public void Merge_LeavesObjectMergingAlone_ItStillGoesKeyByKey()
    {
        var head = CreateHeadRow("""{"customer":{"name":"ada","city":"london"}}""");

        var plan = Plan(head, """{"customer":{"city":"leeds"}}""", mergeArrays: true);

        // arrayMerge governs arrays only; objects have always merged per key and still do.
        plan.Content.Json.ShouldContain("ada");
        plan.Content.Json.ShouldContain("leeds");
    }

    /// <summary>
    /// The setting is body-wide and depth-wide: a transition declaring M gets M for EVERY array in
    /// its body, at any nesting depth. Pinned because it is the fact most likely to surprise — there
    /// is no way to merge one array and replace another on the same transition.
    /// </summary>
    [Fact]
    public void Merge_AppliesToNestedArraysToo_NotJustTopLevelOnes()
    {
        var head = CreateHeadRow("""{"case":{"documents":["A"],"notes":{"items":["n1"]}}}""");

        var plan = Plan(head, """{"case":{"documents":["B"],"notes":{"items":["n2"]}}}""", mergeArrays: true);

        var json = plan.Content.Json;
        json.ShouldContain("\"A\"");   // depth 2
        json.ShouldContain("\"B\"");
        json.ShouldContain("n1");     // depth 3
        json.ShouldContain("n2");
    }

    /// <summary>
    /// Dedup is incoming-vs-stored only. A duplicate the STORED array already carried was written by
    /// something else and is left alone — M is a merge rule, not a repair pass.
    /// </summary>
    [Fact]
    public void Merge_DoesNotCleanUpDuplicatesTheStoredArrayAlreadyHad()
    {
        var head = CreateHeadRow("""{"tags":["x","x","y"]}""");

        var plan = Plan(head, """{"tags":["y","z"]}""", mergeArrays: true);

        // The pre-existing double "x" survives; only the incoming "y" is recognised as already present.
        plan.Content.Json.ShouldBe("""{"tags":["x","x","y","z"]}""");
    }

    /// <summary>
    /// The kill-switch override: a transition that opted into M keeps M even when the legacy pipeline
    /// is on, because the legacy merger cannot express it and degrading back to R would silently
    /// restore the data loss the setting exists to prevent.
    /// </summary>
    [Fact]
    public void Merge_SurvivesTheLegacyPipelineKillSwitch()
    {
        var head = CreateHeadRow("""{"documents":["A","B","C"]}""");

        var plan = InstanceDataWriteService.PlanAppend(
            head,
            new JsonData("""{"documents":["A","B","D"]}"""),
            VersionStrategy.IncreasePatch,
            legacyPipeline: true,
            preserveNumericPrecision: false,
            mergeArrays: true);

        foreach (var expected in new[] { "A", "B", "C", "D" })
        {
            plan.Content.Json.ShouldContain($"\"{expected}\"");
        }
    }

    /// <summary>And the switch still governs every ordinary (R) append — the override is M-only.</summary>
    [Fact]
    public void Replace_StillTakesTheLegacyPipeline_WhenTheKillSwitchIsOn()
    {
        var head = CreateHeadRow("""{"documents":["A","B","C"]}""");

        var plan = InstanceDataWriteService.PlanAppend(
            head,
            new JsonData("""{"documents":["A","B","D"]}"""),
            VersionStrategy.IncreasePatch,
            legacyPipeline: true,
            preserveNumericPrecision: false,
            mergeArrays: false);

        plan.Content.Json.ShouldNotContain("\"C\"");
    }

    private static AppendPlan Plan(
        InstanceDataHeadRow head, string delta, bool mergeArrays) =>
        InstanceDataWriteService.PlanAppend(
            head,
            new JsonData(delta),
            VersionStrategy.IncreasePatch,
            legacyPipeline: false,
            preserveNumericPrecision: false,
            mergeArrays: mergeArrays);

    private static InstanceDataHeadRow CreateHeadRow(string json)
    {
        var data = new JsonData(json);
        return new InstanceDataHeadRow
        {
            Version = "1.0.0",
            Data = data.Json,
            DataHash = InstanceData.ComputeDataHash(data)
        };
    }
}
