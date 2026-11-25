using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Headers;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.History;

/// <summary>
/// <see cref="HeaderExposureDenyList"/>: <c>Workflow:HeaderExposure:Deny</c>, matched exactly and case-insensitively; an
/// empty list changes nothing. There is no allow list.
/// </summary>
public sealed class HeaderExposureDenyListTests
{
    private const string Recorded = """
        { "authorization": "Bearer t", "cookie": "c=1", "x-actor": "11111111111", "x-request-id": "abc", "x-correlation-id": "c-1" }
        """;

    private static HeaderExposureDenyList List(params string[] deny) =>
        new(Options.Create(new HeaderExposureOptions { Deny = [.. deny] }));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string[] Names(JsonElement? element) =>
        element!.Value.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void AnEmptyList_ReturnsTheRecordedValueUnchanged()
    {
        var recorded = Json(Recorded);

        var list = List();

        list.IsEmpty.ShouldBeTrue();
        list.Apply(recorded)!.Value.GetRawText().ShouldBe(recorded.GetRawText());
    }

    [Fact]
    public void TheListedNames_AreDropped_EveryOtherNameIsKept()
        => Names(List("authorization", "cookie", "x-actor").Apply(Json(Recorded)))
            .ShouldBe(["x-request-id", "x-correlation-id"]);

    [Fact]
    public void NamesMatchCaseInsensitively_AndBlankEntriesAreIgnored()
    {
        var list = List("Authorization", " COOKIE ", " ", "");

        Names(list.Apply(Json(Recorded))).ShouldBe(["x-actor", "x-request-id", "x-correlation-id"]);
        list.IsDenied("AUTHORIZATION").ShouldBeTrue();
        list.IsDenied("x-request-id").ShouldBeFalse();
    }

    [Fact]
    public void OnlyBlankEntries_CountAsAnEmptyList()
        => List(" ", "").IsEmpty.ShouldBeTrue();

    [Fact]
    public void ValuesAreKeptAsRecorded()
        => List("authorization").Apply(Json(Recorded))!.Value.GetProperty("x-actor").GetString()
            .ShouldBe("11111111111");

    [Fact]
    public void AConfiguredList_WithholdsANonObjectValue()
        => List("authorization").Apply(Json("""[ "authorization" ]""")).ShouldBeNull();

    [Fact]
    public void AnAbsentOrNullValue_IsReturnedAsItIs()
    {
        var list = List("authorization");

        list.Apply(null).ShouldBeNull();
        list.Apply(Json("null"))!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Filter_ReturnsACaseInsensitiveCopyWithoutTheListedNames()
    {
        var source = new Dictionary<string, string?> { ["Authorization"] = "Bearer t", ["X-Request-Id"] = "abc" };

        var filtered = List("authorization").Filter(source);

        filtered.Keys.ShouldBe(["X-Request-Id"]);
        filtered.ContainsKey("x-request-id").ShouldBeTrue();
        source.Count.ShouldBe(2);
    }

    [Fact]
    public void Filter_WithAnEmptyList_StillReturnsANewDictionary()
    {
        var source = new Dictionary<string, string?> { ["a"] = "1" };

        var filtered = List().Filter(source);

        filtered.ShouldNotBeSameAs(source);
        filtered["a"].ShouldBe("1");
    }
}
