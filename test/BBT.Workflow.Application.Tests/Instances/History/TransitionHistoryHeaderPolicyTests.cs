using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.History;

/// <summary>
/// <see cref="TransitionHistoryHeaderPolicy"/>: <c>Workflow:TransitionHistory:Headers:Read</c> allow and deny lists,
/// matched exactly and case-insensitively; deny wins; empty configuration changes nothing.
/// </summary>
public sealed class TransitionHistoryHeaderPolicyTests
{
    private const string Recorded = """
        { "authorization": "Bearer t", "cookie": "c=1", "x-actor": "11111111111", "x-request-id": "abc", "x-correlation-id": "c-1" }
        """;

    private static TransitionHistoryHeaderPolicy Policy(string[]? allow = null, string[]? deny = null) =>
        new(Options.Create(new TransitionHistoryHeaderOptions
        {
            Read = new TransitionHistoryHeaderLists { Allow = [.. allow ?? []], Deny = [.. deny ?? []] }
        }));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string[] Names(JsonElement? element) =>
        element!.Value.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void EmptyConfiguration_ReturnsTheRecordedValueUnchanged()
    {
        var recorded = Json(Recorded);

        var result = Policy().Apply(recorded);

        result!.Value.GetRawText().ShouldBe(recorded.GetRawText());
    }

    [Fact]
    public void DenyList_DropsTheListedNames()
        => Names(Policy(deny: ["authorization", "cookie", "x-actor"]).Apply(Json(Recorded)))
            .ShouldBe(["x-request-id", "x-correlation-id"]);

    [Fact]
    public void AllowList_KeepsOnlyTheListedNames()
        => Names(Policy(allow: ["x-request-id", "x-correlation-id"]).Apply(Json(Recorded)))
            .ShouldBe(["x-request-id", "x-correlation-id"]);

    [Fact]
    public void DenyWinsOverAllow()
        => Names(Policy(allow: ["x-request-id", "authorization"], deny: ["authorization"]).Apply(Json(Recorded)))
            .ShouldBe(["x-request-id"]);

    [Fact]
    public void NamesMatchCaseInsensitively_AndBlankEntriesAreIgnored()
        => Names(Policy(allow: ["X-Request-ID", " ", ""], deny: ["  "]).Apply(Json(Recorded)))
            .ShouldBe(["x-request-id"]);

    [Fact]
    public void ValuesAreKeptAsRecorded()
        => Policy(allow: ["x-actor"]).Apply(Json(Recorded))!.Value.GetProperty("x-actor").GetString()
            .ShouldBe("11111111111");

    [Fact]
    public void AConfiguredPolicy_WithholdsANonObjectValue()
        => Policy(deny: ["authorization"]).Apply(Json("""[ "authorization" ]""")).ShouldBeNull();

    [Fact]
    public void AnAbsentOrNullValue_IsReturnedAsItIs()
    {
        var policy = Policy(deny: ["authorization"]);

        policy.Apply(null).ShouldBeNull();
        policy.Apply(Json("null"))!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
